using GameTrainer.Core;
using GameTrainer.Core.Updates;

namespace GameTrainer.UI;

/// <summary>
/// 发现新版本时的弹窗。
///
/// 结构照 PinNote：左上是版本号，中间是可滚动的更新说明，
/// 底部是进度条 + 状态文字 + 三个按钮（跳过此版本 / 稍后 / 立即更新）。
///
/// 关键流程在「立即更新」：
///   下载 → 校验 → 复制 Updater 到临时目录 → 启动 Updater（传主程序 PID）→ 主程序退出
///
/// 不能在本进程内直接替换文件：Windows 上运行中的 exe 无法被覆盖。
/// </summary>
internal sealed class UpdateDialog : Form
{
    private readonly UpdateInfo _update;
    private readonly UpdatePrefs _prefs;

    private readonly ProgressBar _bar = new();
    private readonly Label _status = new();
    private readonly FlatButton _installBtn = new();
    private readonly FlatButton _laterBtn = new();
    private readonly FlatButton _skipBtn = new();

    /// <summary>点「立即更新」并通过校验后置 true，调用方据此退出程序。</summary>
    public bool ReadyToRestart { get; private set; }

    public UpdateDialog(UpdateInfo update, UpdatePrefs prefs)
    {
        _update = update;
        _prefs = prefs;

        BuildUi();
    }

    private void BuildUi()
    {
        Text = "发现新版本";
        AppIcon.Apply(this);
        BackColor = Tokens.Surface;
        ForeColor = Tokens.TextPrimary;
        Font = Tokens.FontBody;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Size = new Size(560, 480);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(Tokens.S5),
            BackColor = Tokens.Surface
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        // ---- 标题区 ----
        var head = new Panel { Dock = DockStyle.Fill, BackColor = Tokens.Surface };
        var title = new Label
        {
            Text = "发现新版本",
            Font = Tokens.FontTitle,
            ForeColor = Tokens.TextPrimary,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var ver = new Label
        {
            Text = $"{AppVersion.Display}  →  {_update.Version.ToString(3)}",
            Font = Tokens.FontBody,
            ForeColor = Tokens.Accent,
            AutoSize = true,
            Location = new Point(0, 34)
        };
        head.Controls.Add(title);
        head.Controls.Add(ver);
        root.Controls.Add(head, 0, 0);

        // ---- 更新说明 ----
        var notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Tokens.PageBack,
            ForeColor = Tokens.TextSecondary,
            Font = Tokens.FontBody,
            Text = string.IsNullOrWhiteSpace(_update.ReleaseNotes)
                ? "（本次更新未提供说明）"
                : _update.ReleaseNotes
        };
        root.Controls.Add(notes, 0, 1);

        // ---- 进度与状态 ----
        var prog = new Panel { Dock = DockStyle.Fill, BackColor = Tokens.Surface };
        _bar.SetBounds(0, 8, 496, 6);
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Minimum = 0;
        _bar.Maximum = 100;
        _bar.Visible = false;

        _status.SetBounds(0, 20, 496, 32);
        _status.Text = $"更新包 {_update.Size / 1024.0 / 1024.0:0.0} MB";
        _status.Font = Tokens.FontCaption;
        _status.ForeColor = Tokens.TextMuted;

        prog.Controls.Add(_bar);
        prog.Controls.Add(_status);
        root.Controls.Add(prog, 0, 2);

        // ---- 按钮 ----
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Tokens.Surface,
            Padding = new Padding(0, 6, 0, 0)
        };

        _installBtn.Text = "立即更新";
        _installBtn.Kind = ButtonKind.Primary;
        _installBtn.Width = 108;
        _installBtn.Height = 34;
        _installBtn.Click += async (_, _) => await StartUpdateAsync();

        _laterBtn.Text = "稍后";
        _laterBtn.Kind = ButtonKind.Secondary;
        _laterBtn.Width = 76;
        _laterBtn.Height = 34;
        _laterBtn.Click += (_, _) => Close();

        _skipBtn.Text = "跳过此版本";
        _skipBtn.Kind = ButtonKind.Secondary;
        _skipBtn.Width = 100;
        _skipBtn.Height = 34;
        _skipBtn.Click += (_, _) =>
        {
            _prefs.Skip(_update.Version);
            Close();
        };

        buttons.Controls.Add(_installBtn);
        buttons.Controls.Add(_laterBtn);
        buttons.Controls.Add(_skipBtn);
        root.Controls.Add(buttons, 0, 3);

        Controls.Add(root);
    }

    /// <summary>
    /// 下载 → 校验 → 启动 Updater。
    /// 任一步失败都停在弹窗里显示原因，不静默退出。
    /// </summary>
    private async Task StartUpdateAsync()
    {
        _installBtn.Enabled = false;
        _laterBtn.Enabled = false;
        _skipBtn.Enabled = false;
        _bar.Visible = true;
        _bar.Value = 0;

        try
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "gametrainer-update");
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }

            _status.Text = "正在下载…";
            _status.ForeColor = Tokens.TextSecondary;

            var progress = new Progress<double>(p =>
            {
                int v = (int)Math.Round(p * 100);
                if (v >= 0 && v <= 100) _bar.Value = v;
                _status.Text = $"正在下载… {v}%";
            });

            string pkg = await Task.Run(() =>
                UpdateInstaller.DownloadAsync(_update, tempRoot, progress));
            _bar.Value = 100;

            // 清单写进临时目录，Updater 要自己再验一遍签名
            string manifestPath = Path.Combine(tempRoot, "manifest.json");
            File.WriteAllText(manifestPath, _update.RawManifest);

            _status.Text = "正在准备更新器…";

            // Updater 必须复制到程序目录之外：它要替换的就是程序目录里的文件，
            // 从里面运行会锁住自己。
            string updaterDir = Path.Combine(tempRoot, "updater");
            Directory.CreateDirectory(updaterDir);
            string updaterExe = CopyUpdater(updaterDir);

            string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            var psi = new System.Diagnostics.ProcessStartInfo(updaterExe)
            {
                UseShellExecute = false,
                WorkingDirectory = updaterDir
            };
            psi.ArgumentList.Add("--package"); psi.ArgumentList.Add(pkg);
            psi.ArgumentList.Add("--manifest"); psi.ArgumentList.Add(manifestPath);
            psi.ArgumentList.Add("--target"); psi.ArgumentList.Add(target);
            psi.ArgumentList.Add("--pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            System.Diagnostics.Process.Start(psi);

            ReadyToRestart = true;
            _status.Text = "更新器已启动，程序即将关闭…";
            _status.ForeColor = Tokens.Success;

            // 给 Updater 一点时间进入等待循环再退
            await Task.Delay(600);
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "更新失败：" + ex.Message;
            _status.ForeColor = Tokens.Danger;
            _installBtn.Enabled = true;
            _laterBtn.Enabled = true;
            _skipBtn.Enabled = true;
            _bar.Visible = false;
        }
    }

    /// <summary>把 Updater 及其依赖复制到临时目录，返回 exe 路径。</summary>
    private static string CopyUpdater(string destDir)
    {
        string src = AppContext.BaseDirectory;
        string[] files =
        {
            "GameTrainer.Updater.exe",
            "GameTrainer.Updater.dll",
            "GameTrainer.Updater.deps.json",
            "GameTrainer.Updater.runtimeconfig.json",
            "GameTrainer.dll"
        };

        foreach (string f in files)
        {
            string from = Path.Combine(src, f);
            if (!File.Exists(from)) continue;   // 缺依赖时让 Updater 自己报错更清楚
            File.Copy(from, Path.Combine(destDir, f), overwrite: true);
        }

        string exe = Path.Combine(destDir, "GameTrainer.Updater.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("找不到 GameTrainer.Updater.exe，无法执行自动更新。");
        return exe;
    }
}
