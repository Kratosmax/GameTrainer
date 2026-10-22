using System.Diagnostics;
using GameTrainer.Core;
using GameTrainer.Core.Updates;
using GameTrainer.UI;

namespace GameTrainer;

/// <summary>
/// 统一设置页。一个窗口内按分类分隔，用自绘侧栏定位；主窗口与设置页共用同一套令牌与控件。
///
/// 布局要点（踩过的坑）：
///  - 不在 AutoScroll 容器里用绝对坐标，那会画到 Padding 之外
///  - 用 FlowLayoutPanel 自上而下排布，子项宽度随容器变化
///  - 侧栏用自绘 NavList，不退回系统 ListBox 默认选中样式
///  - 首屏必须在句柄创建后主动渲染（NavList.SetItems 已把索引置 0，
///    只依赖 SelectedIndexChanged 会导致首屏空白）
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly string _skillRoot;
    private readonly ToggleSwitch _startupToggle = new();
    private readonly Label _startupHint = new();
    private readonly NavList _nav = new();
    /// <summary>
    /// 内容滚动区。用 Panel 而非 FlowLayoutPanel：
    /// FlowLayoutPanel 在 TopDown + AutoScroll 下，滚动条出现会改宽度并重排，
    /// 底部内容被裁。Panel + 手动堆叠尺寸可控。
    /// </summary>
    private readonly Panel _flow = new();
    private readonly Label _pageTitle = new();
    private readonly Label _barTitle = new();
    private readonly Label _pageSub = new();


    private static readonly (string Title, string Sub)[] Sections =
    {
        ("常规", "启动行为与窗口关闭方式"),
        ("快捷键", "全局热键与游戏内操作"),
        ("数据与备份", "profile、快照与审计日志的位置"),
        ("安全说明", "这个工具做什么、不做什么"),
        ("关于", "版本、运行环境与自检"),
    };

    public SettingsForm(string skillRoot)
    {
        _skillRoot = skillRoot;            AppIcon.Apply(this);

        Text = "设置";
        BackColor = Tokens.PageBack;
        ForeColor = Tokens.TextPrimary;
        Font = Tokens.FontBody;
        MinimumSize = new Size(760, 560);
        Size = new Size(940, 660);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;
        DoubleBuffered = true;

        BuildLayout();

        Shown += (_, _) =>
        {
            RenderSection();
            RefreshStartup();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Tokens.PageBack,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Tokens.TopBarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeader(), 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Tokens.PageBack,
            Margin = new Padding(0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _nav.Dock = DockStyle.Fill;
        _nav.SectionTitle = "设置";
        _nav.SectionTop = 32;
        _nav.SetItems(Sections.Select(s => s.Title).ToArray());
        // 必须先挂事件再主动渲染首屏
        _nav.SelectedIndexChanged += (_, _) => RenderSection();

        var contentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.PageBack,
            Padding = new Padding(Tokens.S5, Tokens.S4, Tokens.S5, Tokens.S5),
            Margin = new Padding(0)
        };

        var contentWrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Tokens.PageBack,
            Margin = new Padding(0)
        };
        // 头部行高 76：23pt 标题行高约 40px + 副标题 24px + 留白。
        // 原来 64 会让标题被裁掉上缘（实测「数据与备份」顶部缺一截）。
        contentWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        contentWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var headHost = new Panel { Dock = DockStyle.Fill, BackColor = Tokens.PageBack, Margin = new Padding(0) };
        _pageTitle.Dock = DockStyle.Top;
        _pageTitle.Height = 44;
        _pageTitle.Font = Tokens.FontTitle;
        _pageTitle.ForeColor = Tokens.TextPrimary;
        _pageTitle.TextAlign = ContentAlignment.MiddleLeft;
        _pageSub.Dock = DockStyle.Top;
        _pageSub.Height = 24;
        _pageSub.Font = Tokens.FontCaption;
        _pageSub.ForeColor = Tokens.TextSecondary;
        _pageSub.TextAlign = ContentAlignment.MiddleLeft;
        headHost.Controls.Add(_pageSub);
        headHost.Controls.Add(_pageTitle);

        // cardBack 自己画圆角，_flow 若 Dock=Fill 会盖住圆角边缘，
        // 给内边距让滚动区缩进到圆角内（右侧多留一点给滚动条）。
        var cardBack = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.Surface,
            Margin = new Padding(0),
            Padding = new Padding(Tokens.S2, Tokens.S2, Tokens.S2, Tokens.S2)
        };
        cardBack.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Draw.Card(e.Graphics, new Rectangle(0, 0, cardBack.Width - 1, cardBack.Height - 1),
                Tokens.Surface, Tokens.Border);
        };

        // 用 Panel + AutoScroll + 手动纵向堆叠，不用 FlowLayoutPanel。
        // FlowLayoutPanel 在 TopDown + WrapContents=false + AutoScroll 时，
        // 垂直滚动条出现会改变可用宽度并触发重排，底部内容被裁（实测）。
        _flow.Dock = DockStyle.Fill;
        _flow.AutoScroll = true;
        _flow.BackColor = Tokens.Surface;
        _flow.Padding = new Padding(Tokens.S5, Tokens.S4, Tokens.S5, Tokens.S5);
        cardBack.Controls.Add(_flow);

        contentWrap.Controls.Add(headHost, 0, 0);
        contentWrap.Controls.Add(cardBack, 0, 1);
        contentHost.Controls.Add(contentWrap);

        body.Controls.Add(_nav, 0, 0);
        body.Controls.Add(contentHost, 1, 0);

        root.Controls.Add(body, 0, 1);
        Controls.Add(root);

        _flow.SizeChanged += (_, _) => ResizeChildren();
    }

    private Control BuildHeader()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.Surface,
            Padding = new Padding(Tokens.S5, 0, Tokens.S5, 0),
            Margin = new Padding(0)
        };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Tokens.Border, 1f);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        // 顶部 bar 显示"设置 · 分区名"作为位置指示。
        // 原来固定写"设置"，与窗口标题、内容区标题三重重复，
        // 使用者看不出自己在哪一层。
        _barTitle.Text = "设置";
        _barTitle.Dock = DockStyle.Fill;
        _barTitle.Font = Tokens.FontSection;
        _barTitle.ForeColor = Tokens.TextPrimary;
        _barTitle.TextAlign = ContentAlignment.MiddleLeft;
        _barTitle.Margin = new Padding(0);
        bar.Controls.Add(_barTitle);
        return bar;
    }

    /// <summary>
    /// 往滚动区追加一个块。统一管理纵向堆叠与宽度。
    /// 所有内容块都必须走这里，不要直接 _flow.Controls.Add。
    /// </summary>
    private void AddRow(Control c)
    {
        if (c == null) return;

        c.Left = _flow.Padding.Left;
        c.Top = _flowPaddingCursor;
        c.Width = ContentWidth;

        _flow.Controls.Add(c);
        _flowPaddingCursor = c.Bottom + Tokens.S3;
    }


    /// <summary>下一个块的纵向起点。</summary>
    private int _flowPaddingCursor;

    /// <summary>
    /// 按当前宽度重排所有块，并设置滚动范围。
    /// 窗口缩放、切换分区、动态增删块之后都要调。
    /// </summary>
    private void RelayoutFlow()
    {
        int y = _flow.Padding.Top;

        foreach (Control c in _flow.Controls)
        {
            c.Left = _flow.Padding.Left;
            c.Top = y;
            c.Width = ContentWidth;

            y = c.Bottom + Tokens.S3;
        }

        _flowPaddingCursor = y;

        // AutoScroll 需要显式的最小尺寸，否则内容超出时不出现滚动条
        _flow.AutoScrollMinSize = new Size(0, y + _flow.Padding.Bottom);
    }

    private void ResizeChildren()
    {
        int w = ContentWidth;
        foreach (Control c in _flow.Controls)
        {
            if (c.Tag as string == "full") c.Width = w;
        }
    }

    private void RenderSection()
    {
        int idx = _nav.SelectedIndex;
        if (idx >= 0 && idx < Sections.Length)
        {
            _pageTitle.Text = Sections[idx].Title;
            _pageSub.Text = Sections[idx].Sub;
            _barTitle.Text = "设置 · " + Sections[idx].Title;
        }

        _sectionCount = 0;
        _flow.SuspendLayout();
        _flow.Controls.Clear();

        switch (idx)
        {
            case 0: RenderGeneral(); break;
            case 1: RenderHotkeys(); break;
            case 2: RenderData(); break;
            case 3: RenderSafety(); break;
            default: RenderAbout(); break;
        }

        _flow.ResumeLayout();
        RelayoutFlow();
        ResizeChildren();
    }

    // ---- 构建辅助：全部走 FlowLayoutPanel，宽度随容器 ----

    private int ContentWidth => Math.Max(160,
        _flow.ClientSize.Width - _flow.Padding.Horizontal - 20);

    /// <summary>
    /// 分区标题。
    ///
    /// 参照 PinNote 的设置页（SettingsPanel.xaml）：
    /// 不做卡片分组，而是「分区标题 + 缩进内容 + 一条细线分隔」。
    /// 设置项本来就密集，套卡片会让每块内容都被 24px 内边距和阴影包起来，
    /// 一屏能看的信息量掉一半。
    ///
    /// 之前的实现给每块都套了大圆角卡片，是过度设计。
    /// </summary>
    private Label Title(string text)
    {
        // 分区之间用细线分隔，不用间距硬隔
        if (_sectionCount > 0)
        {
            var line = new Panel
            {
                Height = 1,
                BackColor = Tokens.Border,
                Margin = new Padding(0, Tokens.S4, 0, Tokens.S4),
                Tag = "full"
            };
            AddRow(line);
            Gap(Tokens.S2);
        }

        _sectionCount++;

        var l = new Label
        {
            Text = text,
            Font = Tokens.FontSection,
            ForeColor = Tokens.TextPrimary,
            AutoSize = false,
            Height = 28,
            Width = ContentWidth,
            Margin = new Padding(0, 0, 0, Tokens.S2),
            TextAlign = ContentAlignment.MiddleLeft,
            Tag = "full"
        };
        AddRow(l);
        return l;
    }

    /// <summary>当前页已有的分区数，用于决定是否画分隔线。</summary>
    private int _sectionCount;

    /// <summary>
    /// 单行说明。文字变长时必须按实测高度换行，
    /// 固定 26 高度会把长句裁成半行（「窗口行为」那段实测被裁掉）。
    /// </summary>
    private Label Body(string text, Color? color = null)
    {
        Font font = Tokens.FontBody;
        int h = MeasureLabelHeight(text, font, ContentWidth, 26);

        var l = new Label
        {
            Text = text,
            Font = font,
            ForeColor = color ?? Tokens.TextSecondary,
            AutoSize = false,
            Height = h,
            Width = ContentWidth,
            Margin = new Padding(0, 0, 0, 2),
            TextAlign = ContentAlignment.TopLeft,
            Tag = "full"
        };
        AddRow(l);
        return l;
    }

    /// <summary>按给定宽度测量 Label 换行后的实际高度，不写死。</summary>
    private static int MeasureLabelHeight(string text, Font font, int width, int minHeight)
    {
        using var probe = new Label { Text = text, Font = font, AutoSize = true };
        int h = probe.GetPreferredSize(new Size(Math.Max(60, width), 0)).Height;
        return Math.Max(minHeight, h + 2);
    }

    /// <summary>多行说明：按实际文字测量高度，不写死。</summary>
    private Label Wrap(string text, Color? color = null)
    {
        var probe = new Label { Text = text, Font = Tokens.FontCaption, Width = ContentWidth, AutoSize = true };
        int h = Math.Max(20, probe.GetPreferredSize(new Size(ContentWidth, 0)).Height);
        probe.Dispose();

        var l = new Label
        {
            Text = text,
            Font = Tokens.FontCaption,
            ForeColor = color ?? Tokens.TextSecondary,
            AutoSize = false,
            Height = h + 6,
            Width = ContentWidth,
            Margin = new Padding(0, 0, 0, Tokens.S3),
            TextAlign = ContentAlignment.TopLeft,
            Tag = "full"
        };
        AddRow(l);
        return l;
    }

    /// <summary>
    /// 等宽路径框。无边框 + 外包一层自绘圆角面板，
    /// 避免系统 TextBox 的直角黑边与整体风格不一致。
    /// 内容可选中复制，长路径不截断（水平滚动）。
    /// </summary>
    private TextBox PathBox(string path)
    {
        var host = new RoundedField
        {
            Width = ContentWidth,
            Height = 34,
            Margin = new Padding(0, 0, 0, Tokens.S3),
            Tag = "full",
            Padding = new Padding(Tokens.S3, 0, Tokens.S3, 0)
        };

        var tb = new TextBox
        {
            Text = path,
            Font = Tokens.FontMono,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Tokens.SurfaceSunken,
            ForeColor = Tokens.TextPrimary,
            Dock = DockStyle.Fill,      // 交给 Dock 布局，不手工设 Left/Width/Top
            ShortcutsEnabled = true
        };
        // Dock=Fill 会撑满高度；单行 TextBox 文字始终顶部对齐，
        // 用上下 Padding 把它压到垂直中间。
        host.Padding = new Padding(Tokens.S3, Math.Max(0, (host.Height - tb.PreferredHeight) / 2),
                                   Tokens.S3, 0);

        host.Controls.Add(tb);
        AddRow(host);
        return tb;
    }

    private void Gap(int px = Tokens.S4)
    {
        AddRow(new Panel
        {
            Height = px,
            Width = 1,
            Margin = new Padding(0),
            BackColor = Color.Transparent
        });
    }

    private void ButtonRow(params Control[] buttons)
    {
        int totalW = buttons.Sum(b => b.Width + Tokens.S2);
        var host = new Panel
        {
            Height = Tokens.ControlHeight + Tokens.S2,
            Width = Math.Max(ContentWidth, totalW),
            Margin = new Padding(0, 0, 0, Tokens.S3),
            BackColor = Color.Transparent,
            Tag = "full"
        };

        int x = 0;
        foreach (Control b in buttons)
        {
            b.Left = x;
            b.Top = 0;
            host.Controls.Add(b);
            x += b.Width + Tokens.S2;
        }
        AddRow(host);
    }

    // ---- 分类内容 ----

    private void RenderGeneral()
    {
        Title("启动");

        var row = new Panel
        {
            Height = 44,
            Width = ContentWidth,
            Margin = new Padding(0),
            BackColor = Tokens.Surface
        };

        var label = new Label
        {
            Text = "开机自动启动",
            Font = Tokens.FontBody,
            ForeColor = Tokens.TextPrimary,
            AutoSize = false,
            Left = 0,
            Top = 10,
            Width = Math.Max(80, ContentWidth - 70),
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
        };

        _startupToggle.Left = Math.Max(0, ContentWidth - 46);
        _startupToggle.Top = 10;
        _startupToggle.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _startupToggle.CheckedChanged -= OnStartupToggled;   // 防重复订阅
        _startupToggle.CheckedChanged += OnStartupToggled;

        row.Controls.Add(label);
        row.Controls.Add(_startupToggle);
        AddRow(row);

        _startupHint.Font = Tokens.FontCaption;
        _startupHint.ForeColor = Tokens.TextSecondary;
        _startupHint.AutoSize = false;
        _startupHint.Width = ContentWidth;
        _startupHint.Height = 40;
        _startupHint.Margin = new Padding(0);
        AddRow(_startupHint);

        // ---- 窗口行为 ----
        Title("窗口行为");
        Body("有启用中的修改时关闭窗口 → 询问「回滚并退出」/「直接退出」/「最小化到托盘」");
        Body("没有启用中的修改 → 直接退出");
        Wrap("首次最小化到托盘会弹一次气泡提示，之后按你的选择静默处理。"
           + "真正退出请用托盘菜单「全部回滚并退出」，或关闭时选择回滚。");
        // ---- 界面材质 ----
        Title("界面材质");
        Wrap("窗口在 Windows 11 上尝试 DWM Acrylic 全窗材质与系统圆角。"
           + "任一原生调用失败都会整体回退为不透明浅色表面，"
           + "不会留下「标题栏有材质、正文不透明」的混合状态。"
           + "当前系统：" + DescribeMaterial());
    }

    private static string DescribeMaterial()
    {
        int build = Environment.OSVersion.Version.Build;
        return build < 22621
            ? $"构建 {build}，不支持系统背景材质，使用不透明表面。"
            : $"构建 {build}，支持系统背景材质。";
    }

    private void RefreshStartup()
    {
        StartupRegistration.Info info = StartupRegistration.Query();
        _startupToggle.CheckedChanged -= OnStartupToggled;

        switch (info.Status)
        {
            case StartupRegistration.Status.Registered:
                _startupToggle.Checked = true;
                _startupHint.Text = "已启用。注册表值：\n" + info.RegisteredValue;
                _startupHint.ForeColor = Tokens.TextSecondary;
                break;
            case StartupRegistration.Status.NotRegistered:
                _startupToggle.Checked = false;
                _startupHint.Text = "未启用。开启后修改器随系统登录自动驻留托盘，不弹窗抢焦点。";
                _startupHint.ForeColor = Tokens.TextSecondary;
                break;
            case StartupRegistration.Status.PathStale:
                _startupToggle.Checked = true;
                _startupHint.Text = "已启用，但指向旧路径（程序被移动过）。关掉再开即可修复：\n"
                                  + info.RegisteredValue;
                _startupHint.ForeColor = Tokens.Warning;
                break;
            default:
                _startupToggle.Checked = false;
                _startupHint.Text = "无法读取注册表：" + info.Error;
                _startupHint.ForeColor = Tokens.Danger;
                break;
        }

        _startupToggle.CheckedChanged += OnStartupToggled;
    }

    private void OnStartupToggled(object sender, EventArgs e)
    {
        bool want = _startupToggle.Checked;
        bool ok = want ? StartupRegistration.Enable(out string err)
                       : StartupRegistration.Disable(out err);

        if (!ok)
        {
            _startupToggle.CheckedChanged -= OnStartupToggled;
            _startupToggle.Checked = !want;
            _startupToggle.CheckedChanged += OnStartupToggled;
            _startupHint.Text = "设置失败：" + err;
            _startupHint.ForeColor = Tokens.Danger;
            return;
        }
        RefreshStartup();
    }

    private void RenderHotkeys()
    {
        Title("全局快捷键");
        Body("Ctrl + Alt + F1        启用全部修改项");
        Body("Ctrl + Alt + F2        停用全部修改项并回滚");
        Gap();
        Wrap("热键用系统 RegisterHotKey 注册，不注入进程、不钩键盘。"
           + "若组合已被其他程序占用，注册会失败；此时程序会注销已成功的那一半，"
           + "并在日志里说明，不会留下只生效一半的状态。");
        Gap();
        Title("游戏内操作");
        Wrap("独占全屏下覆盖层不可靠，因此本工具不做屏幕覆盖层。"
           + "请用托盘菜单或全局热键操作；需要看日志时切回窗口。");
    }

    private void RenderData()
    {
        Title("目录");
        Body("程序目录");
        PathBox(_skillRoot);
        Gap(Tokens.S2);
        Body("profiles        每个游戏一份 json");
        PathBox(Path.Combine(_skillRoot, "profiles"));
        Gap(Tokens.S2);
        Body("snapshots        存档快照、扫描状态、激活记录");
        PathBox(Path.Combine(_skillRoot, "snapshots"));
        Gap(Tokens.S2);
        Body("audit.log        每次写入与回滚的追加式记录");
        PathBox(Path.Combine(_skillRoot, "audit.log"));

        Gap();
        Title("快捷打开");
        var b1 = new FlatButton { Text = "打开快照目录", Width = 132 };
        b1.Click += (_, _) => OpenPath(Path.Combine(_skillRoot, "snapshots"));
        var b2 = new FlatButton { Text = "打开审计日志", Width = 132 };
        b2.Click += (_, _) => OpenFile(Path.Combine(_skillRoot, "audit.log"));
        ButtonRow(b1, b2);
    }

    private void OpenPath(string p)
    {
        try
        {
            Directory.CreateDirectory(p);
            Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
        }
        catch (Exception ex) { Body("打开失败：" + ex.Message, Tokens.Danger); }
    }

    private void OpenFile(string p)
    {
        try
        {
            if (!File.Exists(p))
            {
                Body("文件尚未创建：" + p, Tokens.TextSecondary);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
        }
        catch (Exception ex) { Body("打开失败：" + ex.Message, Tokens.Danger); }
    }

    private void RenderSafety()
    {
        Title("本工具会做什么");
        Body("· 按 profile 定位并修改单机游戏的内存数值", Tokens.TextPrimary);
        Body("· 对文本存档做快照、修改与恢复", Tokens.TextPrimary);
        Body("· 所有写入先过签名唯一性、前值、区间、字节上限四道校验", Tokens.TextPrimary);

        Gap();
        Title("本工具不做什么");
        Body("· 不做反作弊绕过、隐藏或校验伪造", Tokens.Danger);
        Body("· 不修改联机内容", Tokens.Danger);
        Body("· 不做 DRM 破解", Tokens.Danger);
        Body("· 不编辑二进制存档（IndexedDB / LevelDB 等），只做整体快照与恢复", Tokens.Danger);

        Gap();
        Title("检测到反作弊时");
        Wrap("程序检查三层：目标进程内已加载模块、同机运行的反作弊进程、内核驱动服务。"
           + "任一层命中即强制只读，拒绝一切写入，并在日志里列出具体命中项。"
           + "这是硬阻断，界面上没有可以关掉它的开关。");

        Gap();
        Title("回滚的边界");
        Body("正常退出 / 托盘退出 → 自动回滚", Tokens.Success);
        Body("游戏进程退出 → 修改随进程消失", Tokens.Success);
        Body("修改器被强杀或崩溃 → 修改残留在游戏内", Tokens.Warning);
        Wrap("第三种情况程序无法自动处理。重启后会自动读取激活记录并给出提示，"
           + "可用 revert <profile> <patch> --by-profile 按 profile 语义补救还原。"
           + "该命令只在当前值恰好等于本工具写入的值时才还原，避免覆盖你后续的正常进度。");
    }

    // ---- 更新区（关于页最上方） ----

    private readonly Prefs _prefs = Prefs.Load();
    private CheckBox _autoUpdateBox;
    private Label _updateStatus;
    private FlatButton _checkUpdateBtn;
    private readonly UpdatePrefs _updatePrefs = UpdatePrefs.Load();

    /// <summary>
    /// 更新区：自动检查开关 + 当前版本 + 状态 + 检查按钮。
    /// 结构照 PinNote 的设置页更新小节。
    /// </summary>
    /// <summary>
    /// 网络子区块：手动指定 HTTP 出网代理。
    ///
    /// 自动探测已经能覆盖大多数情况（环境变量 → 系统代理 → 常见端口），
    /// 但用户仍需要显式指定：企业代理、非标准端口、或想强制直连。
    /// 更新检查与适配包下载共用这一份设置。
    /// </summary>
    /// <summary>
    /// 网络设置。
    ///
    /// 用 TableLayoutPanel 三列布局，参照 PinNote 的 SettingsPanel：
    ///   [固定 150px 标签] [弹性内容] [40px 操作]
    ///
    /// 之前用固定 Location 硬编码（标签 x=0、下拉 x=96、输入框 x=258），
    /// 窗口拉宽时全部挤在左边 500px 内，右侧空白——用户反馈"根本拉不开"。
    /// 表格布局让内容列自动吃掉多余宽度。
    /// </summary>
    private void RenderNetworkSubsection()
    {
        var netTitle = Body("网络", Tokens.TextPrimary);
        netTitle.Font = Tokens.FontBodyStrong;
        AddRow(netTitle);

        // 用 Wrap 而不是固定高度 Label：
        // 固定 34px 会把两行文字裁成一行半（实测"常见本地端口"被切掉）。
        Wrap("更新检查与适配包下载共用的出网方式。自动会依次尝试环境变量、系统代理与常见本地端口。");

        // ---- 出网方式 ----
        var modeRow = MakeSettingRow("出网方式");

        _proxyMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Tokens.FontBody,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, Tokens.S2, 2),
            BackColor = Tokens.Surface,
            ForeColor = Tokens.TextPrimary
        };
        _proxyMode.Items.AddRange(new object[] { "自动", "自定义", "强制直连" });

        string current = _prefs.HttpProxy ?? "auto";
        if (current.Equals("direct", StringComparison.OrdinalIgnoreCase)) _proxyMode.SelectedIndex = 2;
        else if (current.Equals("auto", StringComparison.OrdinalIgnoreCase) || current.Length == 0) _proxyMode.SelectedIndex = 0;
        else _proxyMode.SelectedIndex = 1;

        _proxyInput = new RoundedField
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 2),
            BackColor = Tokens.Surface
        };
        _proxyInput.Text = _proxyMode.SelectedIndex == 1 ? current : "";
        _proxyInput.Enabled = _proxyMode.SelectedIndex == 1;

        _proxyMode.SelectedIndexChanged += (_, _) =>
        {
            _proxyInput.Enabled = _proxyMode.SelectedIndex == 1;
            SaveProxySetting();
        };

        // 出网方式行：标签 | 下拉 | 输入框（跨两列用嵌套面板）
        var modeCell = new Panel { Dock = DockStyle.Fill, BackColor = Tokens.Surface };
        modeRow.Controls.Add(modeCell, 1, 0);

        var modeInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Tokens.Surface,
            Margin = new Padding(0)
        };
        modeInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        modeInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        modeInner.Controls.Add(_proxyMode, 0, 0);
        modeInner.Controls.Add(_proxyInput, 1, 0);

        modeCell.Controls.Add(modeInner);
        AddRow(modeRow);

        // 保存按钮并进出网方式行的操作列，不再单独占一行。
        // 之前它孤零零一行、左边空着，看起来像漏了标签。
        var saveBtn = new FlatButton
        {
            Text = "保存",
            Kind = ButtonKind.Secondary,
            Width = 76,
            Height = 30,
            Margin = new Padding(0, 2, 0, 2),
            Anchor = AnchorStyles.Left
        };
        saveBtn.Click += (_, _) => { SaveProxySetting(); };

        // 操作列太窄放不下按钮，改成把按钮塞进内容列右侧的嵌套面板
        var modeCellReal = modeRow.GetControlFromPosition(1, 0) as Panel;
        if (modeCellReal != null && modeCellReal.Controls.Count > 0)
        {
            var inner = modeCellReal.Controls[0] as TableLayoutPanel;
            if (inner != null)
            {
                inner.ColumnStyles.Clear();
                inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
                inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
                inner.ColumnCount = 3;

                inner.Controls.Add(saveBtn, 2, 0);
            }
        }

        // 状态提示单独一行（保存结果、错误信息）
        _proxyStatus = new Label
        {
            Text = "",
            Font = Tokens.FontCaption,
            ForeColor = Tokens.TextMuted,
            AutoSize = false,
            Width = ContentWidth,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, Tokens.S1),
            BackColor = Tokens.Surface
        };
        AddRow(_proxyStatus);
    }

    /// <summary>
    /// 造一个三列设置行：[固定 150px 标签] [弹性内容] [40px 操作]。
    /// 标签列宽度固定，内容列吃满剩余空间——这是 PinNote 的做法。
    /// 返回的 TableLayoutPanel 已填好标签，调用方往 (1,0) 放内容。
    /// </summary>
    private TableLayoutPanel MakeSettingRow(string labelText)
    {
        var row = new TableLayoutPanel
        {
            Width = ContentWidth,
            Height = 40,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Tokens.Surface,
            Margin = new Padding(0, 0, 0, Tokens.S2),
            Tag = "full"
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var label = new Label
        {
            Text = labelText,
            Font = Tokens.FontBody,
            ForeColor = Tokens.TextPrimary,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Tokens.Surface
        };
        row.Controls.Add(label, 0, 0);

        return row;
    }

    private ComboBox _proxyMode;
    private RoundedField _proxyInput;
    private Label _proxyStatus;

    /// <summary>
    /// 保存代理设置。自定义模式必须能解析成合法 http/https URI，
    /// 否则拒绝保存——避免存进去之后所有下载静默失败。
    /// </summary>
    private void SaveProxySetting()
    {
        string value;
        switch (_proxyMode.SelectedIndex)
        {
            case 2:
                value = "direct";
                break;
            case 1:
                value = _proxyInput.Text.Trim();
                if (value.Length == 0)
                {
                    SetProxyStatus("请输入代理地址，例如 http://127.0.0.1:7890", Tokens.Danger);
                    return;
                }
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    SetProxyStatus("只支持 http:// 或 https:// 开头的地址", Tokens.Danger);
                    return;
                }
                break;
            default:
                value = "auto";
                break;
        }

        _prefs.HttpProxy = value;
        if (_prefs.Save())
            SetProxyStatus("已保存", Tokens.Success);
        else
            SetProxyStatus("保存失败：配置目录不可写", Tokens.Danger);
    }

    private void SetProxyStatus(string text, Color color)
    {
        if (_proxyStatus == null) return;
        _proxyStatus.Text = text;
        _proxyStatus.ForeColor = color;
    }
    private void RenderUpdateSection()
    {
        Title("更新");

        var row = new Panel
        {
            Width = ContentWidth,
            Height = 32,
            Margin = new Padding(0, 0, 0, Tokens.S1),
            BackColor = Tokens.Surface
        };

        _autoUpdateBox = new CheckBox
        {
            Text = "启动时自动检查更新",
            Checked = _updatePrefs.AutoCheck,
            AutoSize = true,
            Font = Tokens.FontBody,
            ForeColor = Tokens.TextPrimary,
            BackColor = Tokens.Surface,
            Location = new Point(0, 6)
        };
        _autoUpdateBox.CheckedChanged += (_, _) =>
        {
            _updatePrefs.AutoCheck = _autoUpdateBox.Checked;
            _updatePrefs.Save();
        };

        var ver = new Label
        {
            Text = "当前版本 " + AppVersion.Display,
            Font = Tokens.FontCaption,
            ForeColor = Tokens.TextMuted,
            AutoSize = true,
            BackColor = Tokens.Surface,
            Location = new Point(220, 9)
        };

        row.Controls.Add(_autoUpdateBox);
        row.Controls.Add(ver);
        AddRow(row);

        RenderNetworkSubsection();

        _updateStatus = new Label
        {
            Text = "点「检查更新」查看是否有新版本",
            Font = Tokens.FontCaption,
            ForeColor = Tokens.TextMuted,
            AutoSize = false,
            Width = ContentWidth,
            Height = 34,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(220, 0, 0, Tokens.S2),
            BackColor = Tokens.Surface
        };
        AddRow(_updateStatus);

        var btnRow = new FlowLayoutPanel
        {
            Width = ContentWidth,
            Height = 38,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Tokens.Surface,
            Margin = new Padding(0, 0, 0, Tokens.S2)
        };
        _checkUpdateBtn = new FlatButton
        {
            Text = "检查更新",
            Kind = ButtonKind.Secondary,
            Width = 100,
            Height = 34
        };
        _checkUpdateBtn.Click += async (_, _) => await CheckUpdateAsync();
        btnRow.Controls.Add(_checkUpdateBtn);
        AddRow(btnRow);
    }

    /// <summary>
    /// 检查更新。失败时只写状态文字，不弹框——没网就弹错误很烦。
    /// </summary>
    private async Task CheckUpdateAsync()
    {
        _checkUpdateBtn.Enabled = false;
        _checkUpdateBtn.Text = "检查中…";
        _updateStatus.Text = "正在连接 GitHub…";
        _updateStatus.ForeColor = Tokens.TextMuted;

        try
        {
            string channel = Trust.CurrentChannel;
            string url = UpdateClient.ManifestUrlFor(channel);
            Version current = AppVersion.Full;

            var result = await Task.Run(() => UpdateClient.CheckAsync(current, channel, url));

            if (!result.HasUpdate)
            {
                _updateStatus.Text = result.Message;
                _updateStatus.ForeColor = Tokens.Success;
                return;
            }

            if (_updatePrefs.IsSkipped(result.Latest.Version))
            {
                _updateStatus.Text = $"新版本 {result.Latest.Version.ToString(3)} 已被跳过";
                _updateStatus.ForeColor = Tokens.TextMuted;
                return;
            }

            _updateStatus.Text = result.Message;
            _updateStatus.ForeColor = Tokens.Accent;

            using var dlg = new UpdateDialog(result.Latest, _updatePrefs);
            dlg.ShowDialog(this);

            if (dlg.ReadyToRestart)
                Application.Exit();   // 更新器已在等本进程退出
        }
        catch (Exception ex)
        {
            _updateStatus.Text = "检查失败：" + FirstLine(ex.Message);
            _updateStatus.ForeColor = Tokens.Danger;
        }
        finally
        {
            _checkUpdateBtn.Enabled = true;
            _checkUpdateBtn.Text = "检查更新";
        }
    }

    /// <summary>多行错误只取第一行，状态区放不下整段。</summary>
    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "未知错误";
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s[..i];
    }

    private void RenderAbout()
    {
        // 更新区放最上面：这是用户来到「关于」页最常做的事
        RenderUpdateSection();

        Gap();
        Title("GameTrainer");
        Body("单机游戏内存 / 存档修改器 · 统一入口", Tokens.TextPrimary);
        Gap(Tokens.S2);
        Body("版本        " + AppVersion.Display);
        Body("运行环境    Windows 10 / 11 x64，.NET 8");
        Body("权限要求    普通用户即可读写同用户、同完整性级别的进程");
        Body("提权场景    仅当目标游戏本身以管理员身份运行时");
        Body("隐私        除更新检查外无联网行为，游戏数据全部留在本机");

        Gap();
        Title("自检");

        var result = new Label
        {
            Text = "（点「运行自检」检查 profile 与目录状态）",
            Font = Tokens.FontMono,
            ForeColor = Tokens.TextMuted,
            AutoSize = false,
            Width = ContentWidth,
            Height = 150,
            Margin = new Padding(0, Tokens.S2, 0, Tokens.S2),
            TextAlign = ContentAlignment.TopLeft,
            Tag = "full"
        };

        var btn = new FlatButton { Text = "运行自检", Width = 108 };
        btn.Click += (_, _) =>
        {
            result.Text = RunSelfCheck();
            result.ForeColor = Tokens.TextSecondary;
        };
        ButtonRow(btn);
        AddRow(result);

        Gap();
        Wrap("profile 只保存偏移与签名，不分发游戏原始数据或任何资源文件。");
    }

    private string RunSelfCheck()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            string profDir = Path.Combine(_skillRoot, "profiles");
            sb.AppendLine("profile 目录  : " + (Directory.Exists(profDir) ? "存在" : "缺失"));
            string[] files = Directory.Exists(profDir)
                ? Directory.GetFiles(profDir, "*.json") : Array.Empty<string>();
            sb.AppendLine("profile 数量  : " + files.Length);

            int okCount = 0, badCount = 0;
            foreach (string f in files)
            {
                try { Profiles.ProfileLoader.Load(f); okCount++; }
                catch { badCount++; }
            }
            sb.AppendLine("校验通过      : " + okCount + "，失败 " + badCount);
            sb.AppendLine("审计日志      : " + (File.Exists(Path.Combine(_skillRoot, "audit.log")) ? "存在" : "未创建"));
            sb.AppendLine("开机自启      : " + StartupRegistration.Query().Describe());
            sb.AppendLine("系统构建      : " + Environment.OSVersion.Version.Build);
        }
        catch (Exception ex)
        {
            sb.AppendLine("自检失败：" + ex.Message);
        }
        return sb.ToString();
    }
}
