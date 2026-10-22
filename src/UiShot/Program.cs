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

        // 游戏页：通过反射进入某个已安装适配包的修改页。
        // 侧栏移除后，游戏页是重构的核心产物，必须单独截图验证：
        //   - 顶部有游戏名与状态胶囊
        //   - 内容区全宽（不再被侧栏挤窄）
        //   - 未运行时是中性空状态而不是一片红
        try
        {
            var adaptersField = guiType.GetField("_profileList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (adaptersField != null)
            {
                // 从适配包目录直接读一个 GameAdapter，构造后调 EnterGamePage
                string adaptersDir = Path.Combine(skillRoot, "adapters");
                if (Directory.Exists(adaptersDir))
                {
                    string firstAdapter = Directory.GetDirectories(adaptersDir)
                        .Select(d => Path.Combine(d, "adapter.json"))
                        .FirstOrDefault(File.Exists);

                    if (firstAdapter != null)
                    {
                        var adapterType = Type.GetType("GameTrainer.Adapter.GameAdapter, GameTrainer");
                        var loadMethod = adapterType?.GetMethod("Load",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                        if (loadMethod != null)
                        {
                            object adapter = loadMethod.Invoke(null, new object[] { firstAdapter });
                            var enter = guiType.GetMethod("EnterGamePage",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                            if (enter != null && adapter != null)
                            {
                                gui.Show();
                                Pump(200);
                                enter.Invoke(gui, new[] { adapter });
                                Pump(700);   // 等页面过渡动画跑完

                                // DrawToBitmap 对自绘控件不会主动触发 OnPaint，
                                // 必须先让控件真正渲染到屏幕一次。用 Invalidate + Update 强制。
                                var gvForRefresh = guiType.GetField("_gameView",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(gui) as Control;
                                if (gvForRefresh != null)
                                {
                                    gvForRefresh.Invalidate();
                                    gvForRefresh.Update();
                                    Pump(300);
                                    gvForRefresh.Invalidate();
                                    gvForRefresh.Update();
                                    Pump(200);
                                }
                                results.Add(Capture(gui,
                                    Path.Combine(outDir, "game-page.png"), gui.Size));
                                gui.Hide();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"game-page.capture.failed = {ex.Message}");
        }

        // 日志展开后的游戏页：验证内容区是否让出空间、拖动把手是否可见
        try
        {
            var gvF = guiType.GetField("_gameView",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var gvL = gvF?.GetValue(gui) as Control;

            var toggle = guiType.GetMethod("ToggleLogCollapsed",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var logPadF = guiType.GetField("_logPad",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var logPad = logPadF?.GetValue(gui) as Panel;

            if (toggle != null && logPad != null)
            {
                gui.Show();
                Pump(200);
                toggle.Invoke(gui, new object[] { logPad });
                Pump(900);   // 等 400ms 动画 + 余量

                var logCardF = guiType.GetField("_logCard",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var logCard = logCardF?.GetValue(gui) as Control;
                var handleF = guiType.GetField("_logResizeHandle",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var handle = handleF?.GetValue(gui) as Control;

                report.AppendLine($"log-open.gameView = {gvL?.Bounds}");
                report.AppendLine($"log-open.logCard  = {logCard?.Bounds}");
                report.AppendLine($"log-open.handle   = {handle?.Bounds} visible={handle?.Visible}");
                report.AppendLine($"log-open.overlap  = {((logCard != null && gvL != null) ? (gvL.Bottom > logCard.Top) : false)}");

                results.Add(Capture(gui, Path.Combine(outDir, "game-log-open.png"), gui.Size));
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"log-open.failed = {ex.GetType().Name}: {ex.Message}");
        }

        // 有数据时的游戏页：用假数据验证卡片布局（标题、数值、输入框、应用按钮）。
        // 真实数据需要游戏处于存档内，截图环境不一定满足，所以单独造一份。
        try
        {
            var gvField2 = guiType.GetField("_gameView",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var gv2 = gvField2?.GetValue(gui) as Control;

            var adapterField = guiType.GetField("_adapter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var ad = adapterField?.GetValue(gui);

            if (gv2 != null && ad != null)
            {
                // 构造 ReadResult 字典
                var readResultType = Type.GetType("GameTrainer.Adapter.AdapterRunner+ReadResult, GameTrainer");
                var collItemType = Type.GetType("GameTrainer.Adapter.AdapterRunner+CollectionItem, GameTrainer");
                var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), readResultType);
                var dict = (System.Collections.IDictionary)Activator.CreateInstance(dictType);

                var roster = new (int Id, string Name, string Good, string Bad, string Med)[]
                {
                    (1001, "林语溪", "85", "12", "72"),
                    (1203, "罗伯特·戴维斯", "64", "30", "58"),
                    (1208, "托马斯·杰克逊", "41", "55", "63"),
                    (2001, "王小红", "77", "8", "81"),
                    (2020, "普里亚·夏尔马", "52", "19", "69"),
                    (2500, "安小萌", "90", "5", "76"),
                };

                string[] semKeys = { "employee_good", "employee_bad", "employee_medical" };

                foreach (var sem in semKeys)
                {
                    var items = (System.Collections.IList)Activator.CreateInstance(
                        typeof(List<>).MakeGenericType(collItemType));

                    foreach (var r in roster)
                    {
                        string v = sem switch
                        {
                            "employee_good" => r.Good,
                            "employee_bad" => r.Bad,
                            _ => r.Med
                        };
                        // CollectionItem 是 record，没有无参构造，必须走带参构造
                        var it = Activator.CreateInstance(collItemType, r.Id, r.Name, v);
                        items.Add(it);
                    }

                    // ReadResult 是 record：(Semantic, Ok, Value, Where, Error)
                    var rr = Activator.CreateInstance(readResultType,
                        new object[] { sem, true, null, null, null });
                    readResultType.GetProperty("Items").SetValue(rr, items);
                    dict[sem] = rr;
                }

                // gold_total 用单值
                var goldRr = Activator.CreateInstance(readResultType,
                    new object[] { "gold_total", true, "2000070", null, null });
                dict["gold_total"] = goldRr;

                var setData = gv2.GetType().GetMethod("SetData");
                if (setData != null)
                {
                    var adapterType2 = ad.GetType();
                    setData.Invoke(gv2, new object[] { ad, dict });
                    gui.Show();
                    Pump(1200);
                    gv2.Invalidate();
                    gv2.Update();
                    Pump(400);
                    results.Add(Capture(gui, Path.Combine(outDir, "game-data.png"), gui.Size));
                    report.AppendLine("game-data.captured = true");
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"game-data.failed = {ex.GetType().Name}: {ex.Message}");
        }
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private static string Capture(Form f, string path, Size size)
    {
        using var bmp = new Bitmap(f.Width, f.Height);

        // 先试 PrintWindow：它会向窗口发 WM_PRINT，触发真实绘制流程，
        // 自绘控件（UserPaint）也能正确出图。
        // DrawToBitmap 对 UserPaint 控件不触发 OnPaint，会得到空白区域（实测）。
        bool ok = false;
        try
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                ok = PrintWindow(f.Handle, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }
        }
        catch { ok = false; }

        // PrintWindow 失败（某些 GPU/远程桌面环境）时回退 DrawToBitmap
        if (!ok)
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));

        bmp.Save(path, ImageFormat.Png);
        return $"{path}  {size.Width}x{size.Height}";
    }

}
