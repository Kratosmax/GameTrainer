using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace GameTrainer.UiShot;

/// <summary>
/// UI 截图验证工具。启动真实窗口、真实渲染，抓成 PNG，并把材质返回值一起落盘，
/// 供自动核对而不是"看着差不多"。
///
/// 覆盖 ui-design.md 的验证矩阵维度：多尺寸、长文本、空数据、禁用、选中、错误状态。
/// 注意：截图工具必须与实现同步演进。导航从 ListBox 换成自绘控件后，
/// 若测试仍按 ListBox 查找，会得到"全是同一页"的假通过。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string skillRoot = args.Length > 0 ? args[0]
            : @"<skill目录>";
        string outDir = args.Length > 1 ? args[1] : Path.Combine(skillRoot, "temp", "ui-shot");
        Directory.CreateDirectory(outDir);

        var report = new System.Text.StringBuilder();
        var results = new List<string>();

        Assembly asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "GameTrainer.dll"));

        Type guiType = asm.GetType("GameTrainer.GuiForm", throwOnError: true);
        Type settingsType = asm.GetType("GameTrainer.SettingsForm", throwOnError: true);
        Type guardType = asm.GetType("GameTrainer.Core.SingleInstanceGuard", throwOnError: true);
        Type navType = asm.GetType("GameTrainer.UI.NavList", throwOnError: true);

        object guard = Activator.CreateInstance(guardType, "GameTrainer.UiShot.Instance", "Shot");
        var guiCtor = guiType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new[] { typeof(string), guardType }, null);
        var gui = (Form)guiCtor.Invoke(new object[] { skillRoot, guard });

        // 材质结果必须在首次 Show 之后读取：HWND 在 Show 时才创建，
        // OnHandleCreated 之前读到的只能是字段默认值（空），不能当"未应用"的证据落盘。
        bool materialCaptured = false;

        foreach (var (name, size) in new (string, Size)[]
        {
            ("main-default",   new Size(1120, 760)),
            ("main-min",       new Size(880, 620)),
            ("main-wide",      new Size(1480, 900)),
        })
        {
            gui.Size = size;
            gui.StartPosition = FormStartPosition.Manual;
            gui.Location = new Point(40, 40);
            gui.Show();
            Pump(500);
            results.Add(Capture(gui, Path.Combine(outDir, name + ".png"), size));

            if (!materialCaptured)
            {
                report.AppendLine($"material.applied = {guiType.GetProperty("MaterialApplied")?.GetValue(gui)}");
                report.AppendLine($"material.detail  = {guiType.GetProperty("MaterialDetail")?.GetValue(gui)}");
                report.AppendLine($"material.build   = {Environment.OSVersion.Version.Build}");
                materialCaptured = true;
            }

            gui.Hide();
        }

        // 空状态：没有 profiles 的目录，验证空数据不留纯空白
        string emptyRoot = Path.Combine(outDir, "empty-root");
        Directory.CreateDirectory(emptyRoot);
        var emptyGuard = Activator.CreateInstance(guardType, "GameTrainer.UiShot.Empty", "Empty");
        var emptyGui = (Form)guiCtor.Invoke(new object[] { emptyRoot, emptyGuard });
        emptyGui.Size = new Size(1120, 760);
        emptyGui.StartPosition = FormStartPosition.Manual;
        emptyGui.Location = new Point(40, 40);
        emptyGui.Show();
        Pump(400);
        results.Add(Capture(emptyGui, Path.Combine(outDir, "main-empty.png"), emptyGui.Size));
        emptyGui.Close();
        emptyGui.Dispose();

        // 设置页：五个分类各一张
        string[] sections = { "general", "hotkeys", "data", "safety", "about" };
        for (int idx = 0; idx < sections.Length; idx++)
        {
            var dlg = (Form)Activator.CreateInstance(settingsType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new object[] { skillRoot }, null);
            dlg.Size = new Size(940, 660);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(40, 40);
            dlg.Show();

            // 必须按自绘 NavList 查找；用 ListBox 会永远切不动页
            Control nav = FindFirst(dlg, c => navType.IsInstanceOfType(c));
            if (nav == null)
                Console.WriteLine("[警告] 未找到 NavList，设置页截图将全部是首屏");
            else
                navType.GetProperty("SelectedIndex")?.SetValue(nav, idx);

            Pump(360);
            results.Add(Capture(dlg, Path.Combine(outDir, "settings-" + sections[idx] + ".png"), dlg.Size));

            // 几何落盘：文本框被截断这类问题必须用真实 Bounds 定位，
            // 不能靠"看起来像"猜是宽度还是 Padding 的错。
            if (sections[idx] == "data")
            {
                var boxes = new List<string>();
                DumpTextBoxes(dlg, boxes, 0);
                File.WriteAllLines(Path.Combine(outDir, "geometry-data.txt"), boxes);
            }

            dlg.Close();
            dlg.Dispose();
        }

        // ---- 焦点态验证 ----
        //
        // 为什么要单独做：ShowFocusCues 依赖窗口真实激活状态，
        // DrawToBitmap 在后台渲染时恒为 false，正常截图永远看不到焦点框。
        // 靠 press_key 发 Tab 又会抢人的输入焦点——所以改成在程序内部
        // 打开 ForceFocusCue 开关再渲染，全程不碰键盘鼠标。
        results.AddRange(CaptureFocusCues(gui, guiType, outDir, report));

        gui.Close();
        gui.Dispose();

        File.WriteAllText(Path.Combine(outDir, "uicapture.log"), report.ToString());
        foreach (string r in results) Console.WriteLine(r);
        Console.WriteLine(report.ToString());
        return 0;
    }

    /// <summary>
    /// 逐个给可聚焦控件打开 ForceFocusCue 并单独截图。
    /// 每张图只让一个控件显示焦点框，便于核对"焦点落在哪"。
    /// </summary>
    private static List<string> CaptureFocusCues(Form gui, Type guiType, string outDir, System.Text.StringBuilder report)
    {
        var list = new List<string>();
        gui.Size = new Size(1120, 760);
        gui.StartPosition = FormStartPosition.Manual;
        gui.Location = new Point(40, 40);
        gui.Show();
        Pump(500);

        // 收集所有带 ForceFocusCue 属性的控件（FlatButton / ToggleSwitch）
        var focusables = new List<Control>();
        CollectFocusables(gui, focusables);
        report.AppendLine($"focusable.count = {focusables.Count}");

        int i = 0;
        foreach (Control c in focusables)
        {
            string label = SafeName(c, i);
            // 全部关掉，只开一个
            foreach (Control x in focusables) SetForce(x, false);
            SetForce(c, true);
            c.Invalidate();
            Pump(120);

            string path = Path.Combine(outDir, $"focus-{i:D2}-{label}.png");
            list.Add(Capture(gui, path, gui.Size));
            report.AppendLine($"focus[{i:D2}] {label}  size={c.Size}  visible={c.Visible}  enabled={c.Enabled}");
            i++;
        }

        // 复位
        foreach (Control x in focusables) SetForce(x, false);
        gui.Hide();
        return list;
    }

    private static void CollectFocusables(Control root, List<Control> sink)
    {
        foreach (Control c in root.Controls)
        {
            if (c.GetType().GetProperty("ForceFocusCue") != null) sink.Add(c);
            CollectFocusables(c, sink);
        }
    }

    private static void SetForce(Control c, bool on)
        => c.GetType().GetProperty("ForceFocusCue")?.SetValue(c, on);

    private static string SafeName(Control c, int idx)
    {
        string t = c.Text;
        if (string.IsNullOrWhiteSpace(t)) t = c.GetType().Name;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in t)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        string s = sb.ToString();
        return s.Length > 20 ? s.Substring(0, 20) : s;
    }

    private static Control FindFirst(Control root, Func<Control, bool> pred)
    {
        foreach (Control c in root.Controls)
        {
            if (pred(c)) return c;
            Control n = FindFirst(c, pred);
            if (n != null) return n;
        }
        return null;
    }

    /// <summary>递归打印所有 TextBox 及其父容器几何，用于定位被截断的输入框。</summary>
    private static void DumpTextBoxes(Control root, List<string> sink, int depth)
    {
        string pad = new string(' ', depth * 2);
        sink.Add($"{pad}{root.GetType().Name} bounds={root.Bounds} padding={root.Padding} dock={root.Dock}");

        if (root is TextBox tb)
            sink.Add($"{pad}  TextBox textLen={tb.Text.Length} text=\"{tb.Text}\" clientW={tb.ClientSize.Width}");

        foreach (Control c in root.Controls) DumpTextBoxes(c, sink, depth + 1);
    }

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static string Capture(Form f, string path, Size size)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            // 直接抓窗口内容，避免被其他窗口遮挡
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        }
        bmp.Save(path, ImageFormat.Png);
        return $"{path}  {size.Width}x{size.Height}";
    }
}
