using System.Diagnostics;
using GameTrainer.Core;
using GameTrainer.Engine;
using GameTrainer.Memory;
using GameTrainer.Native;
using GameTrainer.Profiles;
using GameTrainer.UI;

namespace GameTrainer;

/// <summary>
/// 统一入口主窗口。
///
/// 布局（按 desktop-tool-ui-release / ui-design.md）：
///   顶栏：产品标识 + 视图切换 + 右侧状态徽标
///   左侧栏：游戏 profile 列表（核心对象浏览，首屏可见）
///   内容区：修改项卡片（主工作流）+ 运行日志卡片
///   状态栏：附加状态 / 反作弊 / 开机自启 / 热键提示
///
/// 材质：HWND 创建后尝试 DWM Acrylic + 全窗 frame 扩展，
/// 任一步失败整体回退到不透明表面，不出现半透明标题栏配不透明正文的混合态。
/// </summary>
internal sealed class GuiForm : Form
{
    private readonly string _skillRoot;
    private ProcessMemory _mem;
    private GameProfile _profile;
    private PatchEngine _engine;
    private SaveEngine _save;
    private AuditLog _log;
    private HotkeyManager _hotkeys;
    private readonly ActiveStateStore _activeStore;
    private readonly SingleInstanceGuard _single;

    // 顶栏
    private readonly Badge _attachBadge = new();
    private readonly Badge _antiCheatBadge = new();
    private readonly FlatButton _attachBtn = new();
    private readonly FlatButton _revertAllBtn = new();
    private readonly FlatButton _settingsBtn = new();

    // 侧栏
    private readonly ProfileListBox _profileList = new();
    private readonly Label _profileEmpty = new();

    // 内容区
    private readonly Panel _patchHost = new();
    private readonly EmptyState _patchEmpty = new();
    private readonly LogView _logBox = new();
    private readonly CardPanel _patchCard = new();
    private readonly CardPanel _logCard = new();

    // 状态栏
    private readonly Label _statusAttach = new();
    private readonly Label _statusAntiCheat = new();
    private readonly Label _statusStartup = new();
    private readonly Label _hotkeyHint = new();

    private readonly NotifyIcon _tray = new();
    private readonly Dictionary<string, ToggleSwitch> _toggles = new();
    private readonly Dictionary<string, PatchRow> _rows = new();
    private bool _suppress;
    private bool _reallyExit;
    private WindowMaterial.Result _material;
    private TableLayoutPanel _contentGrid = new();

    public static int Run(string skillRoot, bool startBackground = false)
    {
        ApplicationConfiguration.Initialize();   // DPI 由 API 配置，manifest 不再声明
        using var guard = new SingleInstanceGuard("GameTrainer.SingleInstance", "GameTrainer");
        if (!guard.IsFirstInstance)
        {
            guard.TryActivateExisting("GameTrainer — 单机游戏修改器");
            return 0;
        }
        using var f = new GuiForm(skillRoot, guard);
        if (startBackground)
        {
            // 自启动：只驻留托盘，不弹窗抢焦点
            f.WindowState = FormWindowState.Minimized;
            f.ShowInTaskbar = false;
            f.Opacity = 0;
        }
        Application.Run(f);
        return 0;
    }

    private GuiForm(string skillRoot, SingleInstanceGuard guard)
    {
        _skillRoot = skillRoot;
        _single = guard;
        _activeStore = new ActiveStateStore(skillRoot);

        Text = "GameTrainer — 单机游戏修改器";
        BackColor = Tokens.PageBack;
        ForeColor = Tokens.TextPrimary;
        Font = Tokens.FontBody;
        MinimumSize = new Size(880, 620);
        Size = new Size(1120, 760);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        KeyPreview = true;

        BuildLayout();
        BuildTray();

        Load += (_, _) =>
        {
            LoadProfiles();
            InitHotkeys();
            RefreshStartupStatus();
            CheckResidueOnStartup();
        };

        FormClosing += OnFormClosing;
    }

    /// <summary>
    /// HWND 就绪后才应用材质。OnHandleCreated 早于首次绘制，
    /// 此时改背景色不会闪烁。失败时整体走不透明回退。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _material = WindowMaterial.ApplyGlass(Handle);

        // 无论材质是否生效，根表面都用不透明 PageBack：
        // 窗口本身不是分层窗口（AllowsTransparency=false），DWM backdrop 只装饰
        // 标题栏与边框区域；客户区必须自己保证可读，不能让正文交给"透明"碰运气。
        // 这样材质成功与失败只有装饰差异，可读性不依赖原生调用结果。
        BackColor = Tokens.PageBack;
    }

    // ---------------- 布局 ----------------

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Tokens.PageBack,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Tokens.TopBarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Tokens.StatusBarHeight));

        root.Controls.Add(BuildTopBar(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildStatusBar(), 0, 2);

        Controls.Add(root);
    }

    private Control BuildTopBar()
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

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = Tokens.Surface,
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // logo+标题
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // 弹性留白
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 徽标
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 徽标
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 按钮组
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 产品标识：圆角方块 + 名称，参考 DisplayControl 顶栏
        var brand = new Panel
        {
            Width = 210,
            Height = Tokens.TopBarHeight,
            BackColor = Tokens.Surface,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Left
        };
        brand.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var box = new Rectangle(0, (brand.Height - 28) / 2, 28, 28);
            using (var path = Draw.RoundedRect(box, 7))
            using (var b = new SolidBrush(Tokens.Accent))
                g.FillPath(b, path);
            Draw.Text(g, "G", Tokens.FontBodyStrong, Tokens.TextOnAccent, box, HorizontalAlignment.Center);

            var nameRect = new Rectangle(38, 0, brand.Width - 38, brand.Height);
            Draw.Text(g, "GameTrainer", Tokens.FontSection, Tokens.TextPrimary, nameRect);
        };

        _attachBadge.SetStyle(Tokens.SurfaceSunken, Tokens.TextSecondary, "未附加");
        _attachBadge.Margin = new Padding(0, 0, Tokens.S2, 0);
        _attachBadge.Anchor = AnchorStyles.Right;

        _antiCheatBadge.SetStyle(Tokens.SurfaceSunken, Tokens.TextSecondary, "反作弊未检测");
        _antiCheatBadge.Margin = new Padding(0, 0, Tokens.S3, 0);
        _antiCheatBadge.Anchor = AnchorStyles.Right;

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Tokens.Surface,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right
        };

        _attachBtn.Text = "附加到游戏";
        _attachBtn.Kind = ButtonKind.Primary;
        _attachBtn.Width = 116;
        _attachBtn.Margin = new Padding(0, 0, Tokens.S2, 0);
        _attachBtn.Click += (_, _) => DoAttach();

        _revertAllBtn.Text = "全部回滚";
        _revertAllBtn.Kind = ButtonKind.Danger;
        _revertAllBtn.Width = 104;
        _revertAllBtn.Margin = new Padding(0, 0, Tokens.S2, 0);
        _revertAllBtn.Enabled = false;
        _revertAllBtn.Click += (_, _) => DoRevertAll();

        _settingsBtn.Text = "设置";
        _settingsBtn.Width = 76;
        _settingsBtn.Margin = new Padding(0);
        _settingsBtn.Click += (_, _) => OpenSettings();

        actions.Controls.Add(_attachBtn);
        actions.Controls.Add(_revertAllBtn);
        actions.Controls.Add(_settingsBtn);

        layout.Controls.Add(brand, 0, 0);
        // 第 1 列是弹性留白，不放控件
        layout.Controls.Add(_attachBadge, 2, 0);
        layout.Controls.Add(_antiCheatBadge, 3, 0);
        layout.Controls.Add(actions, 4, 0);

        bar.Controls.Add(layout);
        return bar;
    }

    private Control BuildBody()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.PageBack,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Tokens.PageBack,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Tokens.SidebarWidth));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        body.Controls.Add(BuildSidebar(), 0, 0);
        body.Controls.Add(BuildContent(), 1, 0);

        host.Controls.Add(body);
        return host;
    }

    private Control BuildSidebar()
    {
        var side = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.SidebarBack,
            Padding = new Padding(Tokens.S3, Tokens.S4, Tokens.S3, Tokens.S3),
            Margin = new Padding(0)
        };

        var head = new Label
        {
            Text = "游戏",
            Dock = DockStyle.Top,
            Height = 24,
            Font = Tokens.FontCaptionStrong,
            ForeColor = Tokens.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };

        _profileEmpty.Text = "还没有 profile\n在 profiles\\ 下新建 json";
        _profileEmpty.Dock = DockStyle.Fill;
        _profileEmpty.TextAlign = ContentAlignment.TopLeft;
        _profileEmpty.ForeColor = Tokens.TextMuted;
        _profileEmpty.Font = Tokens.FontCaption;
        _profileEmpty.Padding = new Padding(Tokens.S1, Tokens.S3, 0, 0);

        _profileList.Dock = DockStyle.Fill;
        _profileList.Visible = false;
        _profileList.SelectionChanged += (_, _) => OnProfileSelected();

        side.Controls.Add(_profileList);
        side.Controls.Add(_profileEmpty);
        side.Controls.Add(head);
        return side;
    }

    private Control BuildContent()
    {
        var wrap = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.PageBack,
            Padding = new Padding(Tokens.S5, Tokens.S4, Tokens.S5, Tokens.S4),
            Margin = new Padding(0)
        };

        _contentGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Tokens.PageBack,
            Margin = new Padding(0)
        };
        _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        _contentGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 左卡片：修改项（主工作流）
        _patchCard.Title = "修改项";
        _patchCard.Dock = DockStyle.Fill;
        _patchCard.Margin = new Padding(0, 0, Tokens.S3, 0);

        _patchHost.Dock = DockStyle.Fill;
        _patchHost.AutoScroll = true;
        _patchHost.BackColor = Tokens.Surface;
        _patchHost.Padding = new Padding(Tokens.S3, 0, Tokens.S3, Tokens.S3);

        _patchEmpty.Dock = DockStyle.Fill;
        _patchEmpty.Heading = "选择一个游戏并附加";
        _patchEmpty.Detail = "附加成功后，这里会列出该游戏的修改项\n每项可单独启用或停用";
        _patchEmpty.Visible = true;

        _patchCard.Controls.Add(_patchHost);
        _patchCard.Controls.Add(_patchEmpty);

        // 右卡片：运行日志
        _logCard.Title = "运行日志";
        _logCard.Dock = DockStyle.Fill;
        _logCard.Margin = new Padding(0);

        var logPad = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.Surface,
            Padding = new Padding(Tokens.S3, 0, Tokens.S3, Tokens.S3)
        };
        var logInner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.SurfaceSunken,
            Padding = new Padding(Tokens.S2)
        };
        _logBox.Dock = DockStyle.Fill;
        logInner.Controls.Add(_logBox);
        logPad.Controls.Add(logInner);
        _logCard.Controls.Add(logPad);

        _contentGrid.Controls.Add(_patchCard, 0, 0);
        _contentGrid.Controls.Add(_logCard, 1, 0);

        wrap.Controls.Add(_contentGrid);

        // 窄窗口时切换为上下堆叠，避免两个卡片都被压到不可读
        wrap.SizeChanged += (_, _) => RelayoutForWidth(wrap.ClientSize.Width);
        return wrap;
    }

    private bool _stacked;

    private void RelayoutForWidth(int width)
    {
        // 内容区可用宽度扣掉侧栏与左右边距后仍不足 620 时改为上下堆叠
        int contentWidth = width - Tokens.S5 * 2;
        bool shouldStack = contentWidth < 620;
        if (shouldStack == _stacked) return;
        _stacked = shouldStack;

        _contentGrid.SuspendLayout();
        _contentGrid.ColumnStyles.Clear();
        _contentGrid.RowStyles.Clear();

        if (shouldStack)
        {
            _contentGrid.ColumnCount = 1;
            _contentGrid.RowCount = 2;
            _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _contentGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
            _contentGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            _contentGrid.SetCellPosition(_patchCard, new TableLayoutPanelCellPosition(0, 0));
            _contentGrid.SetCellPosition(_logCard, new TableLayoutPanelCellPosition(0, 1));
            _patchCard.Margin = new Padding(0, 0, 0, Tokens.S3);
        }
        else
        {
            _contentGrid.ColumnCount = 2;
            _contentGrid.RowCount = 1;
            _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            _contentGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _contentGrid.SetCellPosition(_patchCard, new TableLayoutPanelCellPosition(0, 0));
            _contentGrid.SetCellPosition(_logCard, new TableLayoutPanelCellPosition(1, 0));
            _patchCard.Margin = new Padding(0, 0, Tokens.S3, 0);
        }

        _contentGrid.ResumeLayout(true);
    }

    private Control BuildStatusBar()
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
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Tokens.Surface,
            Margin = new Padding(0)
        };
        // 前两列承载动态文本，给弹性宽度；后两列是短提示
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        ConfigureStatusLabel(_statusAttach, "未附加到游戏", Tokens.TextSecondary);
        ConfigureStatusLabel(_statusAntiCheat, "反作弊：未检测", Tokens.TextSecondary);
        ConfigureStatusLabel(_statusStartup, "开机自启：未启用", Tokens.TextSecondary);
        ConfigureStatusLabel(_hotkeyHint, "Ctrl+Alt+F1 启用 / F2 停用", Tokens.TextSecondary);

        layout.Controls.Add(_statusAttach, 0, 0);
        layout.Controls.Add(_statusAntiCheat, 1, 0);
        layout.Controls.Add(_statusStartup, 2, 0);
        layout.Controls.Add(_hotkeyHint, 3, 0);

        bar.Controls.Add(layout);
        return bar;
    }

    private static void ConfigureStatusLabel(Label l, string text, Color color)
    {
        l.Text = text;
        l.Dock = DockStyle.Fill;
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.ForeColor = color;
        l.Font = Tokens.FontCaption;
        l.AutoEllipsis = true;
        l.Margin = new Padding(0);
    }

    private void BuildTray()
    {
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "GameTrainer";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new ContextMenuStrip { Font = Tokens.FontBody };
        menu.Items.Add("显示窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("启用全部修改", null, (_, _) => ToggleAll(true));
        menu.Items.Add("停用全部修改", null, (_, _) => ToggleAll(false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("全部回滚并退出", null, (_, _) => { DoRevertAll(); _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---------------- 热键 ----------------

    private void InitHotkeys()
    {
        _hotkeys = new HotkeyManager(Handle);
        var failures = new List<string>();

        // 成组注册：任一失败则回滚已注册项，不留半套热键
        if (!_hotkeys.TryRegister("Ctrl+Alt+F1", () => ToggleAll(true), out string e1))
            failures.Add($"Ctrl+Alt+F1（全部启用）: {e1}");
        if (!_hotkeys.TryRegister("Ctrl+Alt+F2", () => ToggleAll(false), out string e2))
            failures.Add($"Ctrl+Alt+F2（全部停用）: {e2}");

        if (failures.Count > 0)
        {
            _hotkeys.Dispose();          // 回滚：注销已成功的
            _hotkeys = new HotkeyManager(Handle);
            _hotkeyHint.Text = "热键不可用";
            _hotkeyHint.ForeColor = Tokens.Warning;
            foreach (string f in failures) Log("[警告] 热键注册失败：" + f);
            Log("[警告] 已回滚全部热键，避免只注册一半。可在设置里改用其他组合。");
        }
        else
        {
            Log("热键就绪：Ctrl+Alt+F1 全部启用，Ctrl+Alt+F2 全部停用");
        }
    }

    protected override void WndProc(ref Message m)
    {
        _hotkeys?.HandleMessage(m.Msg, m.WParam);
        base.WndProc(ref m);
    }

    // ---------------- 业务 ----------------

    private void LoadProfiles()
    {
        _profileList.Clear();
        string dir = Path.Combine(_skillRoot, "profiles");
        if (!Directory.Exists(dir))
        {
            Log("没有 profiles 目录，请先创建 profile。");
            _profileEmpty.Text = "没有 profiles 目录\n请在程序目录下创建 profiles\\";
            return;
        }

        string[] files = Directory.GetFiles(dir, "*.json");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string f in files)
        {
            try
            {
                GameProfile p = ProfileLoader.Load(f);
                string sub = $"{p.Exe} · {p.Patches.Count} 个修改项";
                _profileList.Add(p.Name, sub, p, invalid: false);
            }
            catch (Exception ex)
            {
                string msg = ex.Message.Split('\n')[0];
                // 长错误不塞满侧栏，取首句
                if (msg.Length > 40) msg = msg[..40] + "…";
                _profileList.Add(Path.GetFileName(f), "[无效] " + msg, null, invalid: true);
            }
        }

        bool has = _profileList.Count > 0;
        _profileList.Visible = has;
        _profileEmpty.Visible = !has;

        if (has) _profileList.Select(0);
        else Log("profiles 目录为空。");
    }

    private void OnProfileSelected()
    {
        if (_profileList.SelectedTag is not GameProfile p) return;
        _profile = p;
        BuildPatchRows();
        Log($"已选择 profile：{p.Id}（{p.Patches.Count} 个修改项）");
    }

    private void BuildPatchRows()
    {
        _patchHost.Controls.Clear();
        _toggles.Clear();
        _rows.Clear();
        if (_profile == null) return;

        bool empty = _profile.Patches.Count == 0;
        _patchEmpty.Visible = empty;
        _patchHost.Visible = !empty;
        if (empty)
        {
            _patchEmpty.Heading = "这个 profile 还没有修改项";
            _patchEmpty.Detail = "在 profile json 的 patches 数组里添加后重新加载";
            return;
        }

        // 行用 Dock=Top 交给 WinForms 布局：
        // 手工算 Left/Width 在 AutoScroll 容器里遇到垂直滚动条出现时会算出过宽的行，
        // 导致最右侧开关被滚动条压住（实测踩过）。Dock 会自动按客户区宽度收缩。
        // 注意 Dock=Top 的叠加顺序是后加的先占位，所以反向插入。
        var defs = _profile.Patches;
        for (int i = defs.Count - 1; i >= 0; i--)
        {
            PatchDef patch = defs[i];
            var row = new PatchRow
            {
                Title = string.IsNullOrWhiteSpace(patch.Label) ? patch.Id : patch.Label,
                Subtitle = DescribePatch(patch),
                State = _engine == null ? PatchRow.StateKind.Idle : PatchRow.StateKind.Ready,
                Dock = DockStyle.Top,
                Height = Tokens.RowHeight
            };

            var toggle = new ToggleSwitch
            {
                Tag = patch.Id,
                Enabled = false,
                Dock = DockStyle.Right,
                Width = 46,
                Margin = new Padding(0, (Tokens.RowHeight - 24) / 2, Tokens.S2, 0)
            };
            toggle.CheckedChanged += (s, _) => OnToggle(patch.Id, ((ToggleSwitch)s!).Checked);

            row.Controls.Add(toggle);
            _patchHost.Controls.Add(row);

            _toggles[patch.Id] = toggle;
            _rows[patch.Id] = row;
        }
    }


    private static string DescribePatch(PatchDef p)
    {
        if (p.Kind == "save") return "存档修改 · 需先备份";
        string lockMode = p.Freeze ? $"持续锁定 {p.FreezeIntervalMs}ms" : "一次性写入";
        return $"{p.Writes.Count} 处写入 · {lockMode}";
    }

    private void DoAttach()
    {
        if (_profile == null) { Log("请先选择 profile。"); return; }

        try
        {
            _mem?.Dispose();
            _mem = ProcessMemory.AttachByName(_profile.Exe);
            _log = new AuditLog(Path.Combine(_skillRoot, "audit.log"));
            _engine = new PatchEngine(_mem, _profile, _log, new RollbackStack(), _activeStore);
            _save = new SaveEngine(_profile, _log, _skillRoot, _activeStore);

            _engine.ApplyAntiCheat(_profile.Anticheat);

            if (_engine.ReadOnlyForced)
            {
                _attachBadge.SetStyle(Tokens.DangerSubtle, Tokens.Danger, $"已附加 PID {_mem.Pid}");
                _antiCheatBadge.SetStyle(Tokens.DangerSubtle, Tokens.Danger, "反作弊：已阻断写入");
                _statusAttach.Text = $"已附加 PID {_mem.Pid} · 只读";
                _statusAttach.ForeColor = Tokens.Danger;
                _statusAntiCheat.Text = "反作弊：已阻断写入";
                _statusAntiCheat.ForeColor = Tokens.Danger;
                Log("[阻断] " + _engine.ReadOnlyReason);
            }
            else
            {
                _attachBadge.SetStyle(Tokens.SuccessSubtle, Tokens.Success, $"已附加 PID {_mem.Pid}");
                _antiCheatBadge.SetStyle(Tokens.SurfaceSunken, Tokens.TextSecondary, "反作弊未检测");
                _statusAttach.Text = $"已附加 PID {_mem.Pid} · {_profile.Exe}";
                _statusAttach.ForeColor = Tokens.Success;
                _statusAntiCheat.Text = "反作弊：未检测";
                _statusAntiCheat.ForeColor = Tokens.TextSecondary;
                Log($"已附加 PID {_mem.Pid}，内存区域 {_mem.Regions.Count} 个");
            }

            foreach (ToggleSwitch t in _toggles.Values) t.Enabled = !_engine.ReadOnlyForced;
            foreach (PatchRow r in _rows.Values)
                r.State = _engine.ReadOnlyForced ? PatchRow.StateKind.Blocked : PatchRow.StateKind.Ready;

            _revertAllBtn.Enabled = true;
            _patchEmpty.Visible = false;
        }
        catch (Exception ex)
        {
            Log("[错误] 附加失败：" + ex.Message);
            _attachBadge.SetStyle(Tokens.DangerSubtle, Tokens.Danger, "附加失败");
            _statusAttach.Text = "附加失败";
            _statusAttach.ForeColor = Tokens.Danger;
        }
    }

    private void OnToggle(string patchId, bool on)
    {
        if (_suppress) return;
        if (_engine == null) { Log("尚未附加进程。"); return; }

        ApplyResult r;
        try
        {
            r = on ? _engine.Enable(patchId) : _engine.Disable(patchId);
        }
        catch (Exception ex)
        {
            r = ApplyResult.Fail($"操作异常：{ex.Message}");
        }

        Log((r.Ok ? "[成功] " : "[失败] ") + r.Message);
        foreach (string d in r.Details) Log("    " + d);

        if (_rows.TryGetValue(patchId, out PatchRow row))
        {
            row.State = r.Ok
                ? (on ? PatchRow.StateKind.Active : PatchRow.StateKind.Ready)
                : PatchRow.StateKind.Blocked;
        }

        if (!r.Ok && _toggles.TryGetValue(patchId, out ToggleSwitch t))
        {
            _suppress = true;
            t.Checked = !on;
            _suppress = false;
        }
    }

    private void ToggleAll(bool on)
    {
        if (_engine == null) { Log("尚未附加进程。"); return; }

        int ok = 0, fail = 0;
        foreach (var kv in _toggles)
        {
            if (kv.Value.Checked == on) continue;
            _suppress = true;
            kv.Value.Checked = on;
            _suppress = false;
            try
            {
                ApplyResult r = on ? _engine.Enable(kv.Key) : _engine.Disable(kv.Key);
                if (r.Ok)
                {
                    ok++;
                    if (_rows.TryGetValue(kv.Key, out PatchRow row))
                        row.State = on ? PatchRow.StateKind.Active : PatchRow.StateKind.Ready;
                }
                else
                {
                    fail++;
                    Log("[失败] " + r.Message);
                    if (_rows.TryGetValue(kv.Key, out PatchRow row2))
                        row2.State = PatchRow.StateKind.Blocked;
                }
            }
            catch (Exception ex)
            {
                fail++;
                Log($"[失败] {kv.Key}：{ex.Message}");
            }
        }
        Log($"批量{(on ? "启用" : "停用")}完成：成功 {ok}，失败 {fail}");
    }

    private void DoRevertAll()
    {
        if (_engine == null) return;
        _engine.StopAllFreezes();
        int n = _engine.RevertAllPending();
        _activeStore.ClearAll();
        Log($"已回滚全部修改：{n} 处");

        foreach (ToggleSwitch t in _toggles.Values)
        {
            _suppress = true;
            t.Checked = false;
            _suppress = false;
        }
        foreach (PatchRow r in _rows.Values)
            r.State = PatchRow.StateKind.Ready;
    }

    private void RefreshStartupStatus()
    {
        StartupRegistration.Info info = StartupRegistration.Query();
        _statusStartup.Text = "开机自启：" + info.Describe();
        _statusStartup.ForeColor = info.Status switch
        {
            StartupRegistration.Status.Registered => Tokens.Success,
            StartupRegistration.Status.PathStale => Tokens.Warning,
            StartupRegistration.Status.Error => Tokens.Danger,
            _ => Tokens.TextSecondary
        };
    }

    /// <summary>
    /// 启动时核验上次是否留下未回滚的修改。
    /// 有残留就明确提示并提供恢复入口，而不是假装没事。
    /// </summary>
    private void CheckResidueOnStartup()
    {
        var residue = _activeStore.DetectResidue();
        if (residue.Count == 0) return;

        var alive = residue.Where(r => r.ProcessStillAlive).ToList();
        if (alive.Count == 0)
        {
            // 进程已退出，内存修改随之消失，只需清记录
            _activeStore.ClearAll();
            Log("上次的修改随游戏进程退出已自动失效，记录已清理。");
            return;
        }

        var names = string.Join("、", alive.Select(r => r.Entry.PatchLabel ?? r.Entry.PatchId));
        Log($"[提示] 检测到上次未正常回滚的修改：{names}");
        Log("        游戏进程仍在运行，内存中可能仍保留修改后的值。");
        Log("        可选中对应 profile 后使用「全部回滚」，或对具体项执行 revert --by-profile。");

        _statusAttach.ForeColor = Tokens.Warning;
    }

    // ---------------- 设置 ----------------

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_skillRoot);
        dlg.ShowDialog(this);
        RefreshStartupStatus();
    }

    // ---------------- 日志与退出 ----------------

    private void Log(string msg)
    {
        if (_logBox.InvokeRequired) { _logBox.BeginInvoke(() => Log(msg)); return; }
        _logBox.Append($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }

    /// <summary>首次关闭提示"已转到后台"，之后静默。</summary>
    private static bool _hideHintShown;

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_reallyExit)
        {
            PerformExit();
            return;
        }

        if (_engine != null && _engine.ActivePatches.Count > 0)
        {
            DialogResult r = MessageBox.Show(this,
                "当前仍有启用的修改项。\n\n"
                + "「是」= 先回滚全部修改，再退出\n"
                + "「否」= 不回滚直接退出（修改会残留在游戏内）\n"
                + "「取消」= 留在托盘继续运行，修改保持生效",
                "GameTrainer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

            if (r == DialogResult.Cancel)
            {
                e.Cancel = true;
                Hide();
                WindowState = FormWindowState.Minimized;
                if (!_hideHintShown)
                {
                    _hideHintShown = true;
                    _tray.ShowBalloonTip(2000, "GameTrainer", "已转到后台，双击托盘图标可恢复窗口。", ToolTipIcon.Info);
                }
                return;
            }

            if (r == DialogResult.Yes) DoRevertAll();
        }

        PerformExit();
    }

    private void PerformExit()
    {
        if (_engine != null)
        {
            _engine.StopAllFreezes();
            _engine.RevertAllPending();
        }
        _hotkeys?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _mem?.Dispose();
    }

    /// <summary>供自检/诊断读取当前材质结果，UI 测试也用它断言回退行为。</summary>
    public string MaterialDetail => _material.Detail;
    public bool MaterialApplied => _material.Applied;
}
