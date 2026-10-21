using System.Diagnostics;
using GameTrainer.Adapter;
using GameTrainer.Core;
using GameTrainer.Core.Updates;
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
    private GameAdapter _adapter;
    private readonly Dictionary<string, ValueCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CollectionCard> _collectionCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PersistentWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private GameProfile _profile;
    /// <summary>
    /// 旧 profile 写入引擎。DoAttach 删除后已无赋值点，恒为 null——
    /// 所有 `_engine == null` 判断都会走"尚未附加"分支。
    /// 保留字段让旧 profile 动作代码仍能编译；整条链路属遗留死代码，
    /// 清理需另立议题，不在本次范围。
    /// </summary>
#pragma warning disable CS0649 // 恒为 null：见上方说明，这是刻意的
    private PatchEngine _engine;
#pragma warning restore CS0649
    private HotkeyManager _hotkeys;
    private readonly ActiveStateStore _activeStore;
    private readonly SingleInstanceGuard _single;

    // 顶栏
    private readonly Badge _attachBadge = new();
    private readonly Badge _antiCheatBadge = new();
    private readonly FlatButton _attachBtn = new();
    private readonly FlatButton _refreshBtn = new();
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
        AppIcon.Apply(this);   // 标题栏 / 任务栏 / Alt+Tab 用程序自身图标
        BackColor = Tokens.PageBack;
        ForeColor = Tokens.TextPrimary;
        Font = Tokens.FontBody;
        MinimumSize = new Size(1000, 700);
        Size = new Size(1440, 900);
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
            ScheduleAutoUpdateCheck();
        };

        FormClosing += OnFormClosing;
    }
    /// <summary>
    /// 启动后延迟 3 秒做一次更新检查。
    ///
    /// 为什么延迟：启动瞬间要读配置、建界面、加载适配包，
    /// 这时发网络请求会拖慢首屏；窗口没画完就弹窗体验也差。
    ///
    /// 为什么静默失败：没网、被墙、GitHub 挂了都不该打扰用户——
    /// 更新是锦上添花，不是启动的必要条件。只有真发现新版本才提示。
    /// </summary>
    private void ScheduleAutoUpdateCheck()
    {
        var prefs = UpdatePrefs.Load();
        if (!prefs.AutoCheck) return;

        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            try
            {
                string channel = Trust.CurrentChannel;
                string url = UpdateClient.ManifestUrlFor(channel);
                var r = await Task.Run(() => UpdateClient.CheckAsync(AppVersion.Full, channel, url));

                if (!r.HasUpdate) return;
                if (prefs.IsSkipped(r.Latest.Version)) return;
                if (IsDisposed || !IsHandleCreated) return;

                Log($"发现新版本 {r.Latest.Version.ToString(3)}（当前 {AppVersion.Display}）");
                using var dlg = new UpdateDialog(r.Latest, prefs);
                dlg.ShowDialog(this);

                if (dlg.ReadyToRestart) Application.Exit();
            }
            catch
            {
                // 静默：启动时的更新检查失败不打扰用户
            }
        };
        timer.Start();
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

        _attachBadge.SetStyle(Tokens.SurfaceSunken, Tokens.TextSecondary, "未选择");
        _attachBadge.Margin = new Padding(0, 0, Tokens.S2, 0);
        _attachBadge.Anchor = AnchorStyles.Right;

        _antiCheatBadge.SetStyle(Tokens.SurfaceSunken, Tokens.TextSecondary, "未连接");
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

        // 适配包模式：不需要「附加」，改为「启动游戏 / 刷新数值」
        _attachBtn.Text = "启动游戏";
        _attachBtn.AccessibleName = _attachBtn.Text;
        _attachBtn.Kind = ButtonKind.Primary;
        _attachBtn.Width = 116;
        _attachBtn.Margin = new Padding(0, 0, Tokens.S2, 0);
        _attachBtn.Click += (_, _) => _ = LaunchOrRefreshAsync();

        // 次要样式而非危险红：红色保留给破坏性操作，「刷新」是安全操作。
        // 选中适配包后才启用——之前它继承旧「全部回滚」按钮的启用逻辑，
        // 在适配包（推荐路径）下永远禁用，用户无法手动刷新。
        _refreshBtn.Text = "刷新数值";
        _refreshBtn.AccessibleName = _refreshBtn.Text;
        _refreshBtn.Kind = ButtonKind.Secondary;
        _refreshBtn.Width = 104;
        _refreshBtn.Margin = new Padding(0, 0, Tokens.S2, 0);
        _refreshBtn.Enabled = false;
        _refreshBtn.Click += (_, _) => { if (_adapter != null) _ = RefreshValuesAsync(); };

        _settingsBtn.Text = "设置";
        _settingsBtn.AccessibleName = _settingsBtn.Text;
        _settingsBtn.Width = 76;
        _settingsBtn.Margin = new Padding(0);
        _settingsBtn.Click += (_, _) => OpenSettings();

        actions.Controls.Add(_attachBtn);
        actions.Controls.Add(_refreshBtn);
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

        _logPad = new Panel
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
        _logPad.Controls.Add(logInner);
        _logCard.Controls.Add(_logPad);

        // 日志区可收起：点标题栏右侧的「收起」切换。
        // 依据 ui-design.md：低频区占用主要空间时应提供可发现且状态稳定的折叠能力。
        _logCard.ActionText = "收起";
        _logCard.Cursor = Cursors.Hand;
        _logCard.HeaderClick += () => ToggleLogCollapsed(_logPad);

        _contentGrid.Controls.Add(_patchCard, 0, 0);
        _contentGrid.Controls.Add(_logCard, 1, 0);

        wrap.Controls.Add(_contentGrid);

        // 窄窗口时切换为上下堆叠，避免两个卡片都被压到不可读
        wrap.SizeChanged += (_, _) => RelayoutForWidth(wrap.ClientSize.Width);
        return wrap;
    }



    /// <summary>
    /// 按适配包更新状态栏：连接状态、端口、可改数值数。
    /// 让使用者一眼看出「现在能不能操作」。
    /// cdpReady 已知时（如刚启动成功）直接传入，跳过一次 0.8s 的端口探测。
    /// </summary>
    private void UpdateStatusForAdapter(GameAdapter a, bool? cdpReady = null)
    {
        _statusAttach.Text = $"{a.Name} · {a.Engine}";

        bool ok = cdpReady ?? GameLauncher.IsCdpReady(a.Connection.DebugPort, out _);
        _statusAntiCheat.Text = ok ? $"调试端口：已连接 ({a.Connection.DebugPort})" : $"调试端口：未连接 ({a.Connection.DebugPort})";
        _statusAntiCheat.ForeColor = ok ? Tokens.Success : Tokens.Warning;

        _statusStartup.Text = $"{a.Bindings.Count} 个数值 · {a.Mode}";
        _statusStartup.ForeColor = Tokens.TextSecondary;
    }

    private bool _launching;

    /// <summary>
    /// 顶栏「启动游戏」：适配包模式下先确保游戏带调试端口运行，再刷新数值。
    /// 已连接时直接刷新，不重复启动。
    /// 整体走后台线程：Launch 内部是秒级轮询等待（Thread.Sleep + HTTP 探测，
    /// 最长 launchTimeoutSec），在 UI 线程同步调用会让窗口冻结「未响应」直到超时。
    /// </summary>
    private async Task LaunchOrRefreshAsync()
    {
        if (_adapter == null)
        {
            Log("请先在左侧选择一个游戏适配包。");
            return;
        }
        if (_launching) { Log("启动/刷新进行中，请稍候…"); return; }

        var adapter = _adapter;
        int port = adapter.Connection.DebugPort;
        bool ready = await Task.Run(() => GameLauncher.IsCdpReady(port, out _));

        if (ready)
        {
            Log("调试端口已就绪，直接刷新数值。");
            await RefreshValuesAsync();
            return;
        }

        _launching = true;
        _attachBtn.Enabled = false;
        _refreshBtn.Enabled = false;
        try
        {
            Log($"正在启动 {adapter.Name}…");
            var r = await Task.Run(() => GameLauncher.Launch(adapter, progress: line => Log("  " + line.Trim())));
            if (r.Ok && r.CdpReady)
            {
                _attachBadge.SetStyle(Tokens.SuccessSubtle, Tokens.Success, "已连接");
                _antiCheatBadge.SetStyle(Tokens.SuccessSubtle, Tokens.Success, "调试端口就绪");
                Log("启动成功，正在读取数值…");
                UpdateStatusForAdapter(adapter, cdpReady: true);
                _refreshBtn.Enabled = true;
                await RefreshValuesAsync();
            }
            else
            {
                _attachBadge.SetStyle(Tokens.DangerSubtle, Tokens.Danger, "启动失败");
                Log("[失败] " + r.Message.Replace("\n", " "));
            }
        }
        finally
        {
            _launching = false;
            _attachBtn.Enabled = true;
        }
    }

    /// <summary>把冗长的连接错误压成一句短提示，供卡片显示。</summary>
    private static string ShortenError(string err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "读取失败";
        if (err.Contains("无法连接")) return "未连接游戏";
        if (err.Contains("无实例")) return "不在此场景";
        if (err.Contains("对象不存在")) return "对象未加载";
        if (err.Contains("变量不存在")) return "变量缺失";
        string one = err.Split('\n')[0].Trim();
        return one.Length > 24 ? one[..24] + "…" : one;
    }

    private Panel _logPad;
    private bool _stacked;
    private bool _logCollapsed;

    /// <summary>
    /// 收起 / 展开日志区。
    /// 收起时把右列压到最小宽度，把空间让给数值卡片——日志是低频信息，
    /// 不该长期占用 42% 宽度（ui-design.md：低频区占主要空间时应可折叠）。
    /// </summary>
    private void ToggleLogCollapsed(Panel logPad)
    {
        _logCollapsed = !_logCollapsed;
        logPad.Visible = !_logCollapsed;
        _logCard.ActionText = _logCollapsed ? "展开" : "收起";
        _logCard.Invalidate();

        if (!_stacked)
        {
            _contentGrid.SuspendLayout();
            _contentGrid.ColumnStyles.Clear();
            if (_logCollapsed)
            {
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            }
            else
            {
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            }
            _contentGrid.ResumeLayout(true);
        }

        Log(_logCollapsed ? "日志区已收起，点「展开」恢复。" : "日志区已展开。");
    }

    private void RelayoutForWidth(int width)
    {
        // 内容区可用宽度扣掉侧栏与左右边距后仍不足 620 时改为上下堆叠
        int contentWidth = width - Tokens.S5 * 2;
        bool shouldStack = contentWidth < 620;
        if (shouldStack == _stacked) return;
        _stacked = shouldStack;

        // 窄窗口下日志自动收起：卡片是主工作流，日志是低频信息。
        // 只在进入窄布局时自动收一次，之后尊重用户的显式选择。
        if (shouldStack && !_logCollapsed && _logPad != null)
        {
            _logCollapsed = true;
            _logPad.Visible = false;
            _logCard.ActionText = "展开";
            _logCard.Invalidate();
        }

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
            // 尊重用户手动收起的日志：窗口从窄拉宽后不该把它重新撑开
            if (_logCollapsed)
            {
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            }
            else
            {
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
                _contentGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            }
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

        // 状态栏改为反映「适配包 + 连接」语义，而不是旧的「附加/反作弊」。
        // 依据 ui-design.md：状态文案要能说明当前是否可操作。
        ConfigureStatusLabel(_statusAttach, "未选择适配包", Tokens.TextSecondary);
        ConfigureStatusLabel(_statusAntiCheat, "调试端口：未知", Tokens.TextSecondary);
        ConfigureStatusLabel(_statusStartup, "开机自启：未启用", Tokens.TextSecondary);
        ConfigureStatusLabel(_hotkeyHint, "Ctrl+Alt+F1 启用 / Ctrl+Alt+F2 停用", Tokens.TextSecondary);

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
        _tray.Icon = AppIcon.Get(16);   // 托盘要 16x16，用程序自身图标
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

    /// <summary>
    /// 加载适配包（adapters/&lt;id&gt;/adapter.json）到侧栏。
    /// 适配包是推荐路径：主程序只认语义，换游戏不用改代码。
    /// 旧的 profiles/ 仍保留兼容，但排在适配包之后。
    /// </summary>
    private void LoadProfiles()
    {
        _profileList.Clear();
        int adapters = LoadAdapters();
        int profiles = LoadLegacyProfiles();

        bool has = _profileList.Count > 0;
        _profileList.Visible = has;
        _profileEmpty.Visible = !has;

        if (has)
        {
            _profileList.Select(0);
        }
        else
        {
            _profileEmpty.Text = "还没有可用的游戏配置\n用 probe 生成适配包，或在 profiles\\ 放 json";
            Log($"未找到任何配置（适配包 {adapters} 个，旧 profile {profiles} 个）。");
        }
    }

    /// <summary>扫描 adapters 目录。</summary>
    private int LoadAdapters()
    {
        string dir = Path.Combine(_skillRoot, "adapters");
        if (!Directory.Exists(dir)) return 0;

        int n = 0;
        foreach (string sub in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            string f = Path.Combine(sub, "adapter.json");
            if (!File.Exists(f)) continue;
            try
            {
                var a = GameAdapter.Load(f);
                string sub2 = $"{a.Engine} · {a.Bindings.Count} 个数值";
                _profileList.Add(a.Name, sub2, a, invalid: false);
                n++;
            }
            catch (Exception ex)
            {
                string msg = ex.Message.Split('\n')[0];
                if (msg.Length > 40) msg = msg[..40] + "…";
                _profileList.Add(Path.GetFileName(sub), "[无效] " + msg, null, invalid: true);
            }
        }
        return n;
    }

    /// <summary>扫描旧 profiles 目录（兼容）。</summary>
    private int LoadLegacyProfiles()
    {
        string dir = Path.Combine(_skillRoot, "profiles");
        if (!Directory.Exists(dir)) return 0;

        string[] files = Directory.GetFiles(dir, "*.json");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        int n = 0;
        foreach (string f in files)
        {
            try
            {
                GameProfile p = ProfileLoader.Load(f);
                _profileList.Add(p.Name, $"{p.Exe} · {p.Patches.Count} 个修改项", p, invalid: false);
                n++;
            }
            catch (Exception ex)
            {
                string msg = ex.Message.Split('\n')[0];
                if (msg.Length > 40) msg = msg[..40] + "…";
                _profileList.Add(Path.GetFileName(f), "[无效] " + msg, null, invalid: true);
            }
        }
        return n;
    }

    private void OnProfileSelected()
    {
        switch (_profileList.SelectedTag)
        {
            case GameAdapter a:
                StopAllWatchers("切换游戏");
                _adapter = a;
                _profile = null;
                BuildValueCards();
                UpdateStatusForAdapter(a);
                _refreshBtn.Enabled = true;
                Log($"已选择适配包：{a.Id}（{a.Bindings.Count} 个数值）");
                _ = RefreshValuesAsync();
                break;

            case GameProfile p:
                _adapter = null;
                _profile = p;
                BuildPatchRows();
                Log($"已选择 profile：{p.Id}（{p.Patches.Count} 个修改项）");
                break;
        }
    }

    /// <summary>
    /// 按适配包的绑定生成数值卡片网格。
    /// 布局：自适应列数（每列至少 260px），多行多列，容器可纵向滚动。
    /// </summary>
    private void BuildValueCards()
    {
        _patchHost.Controls.Clear();
        _cards.Clear();
        if (_adapter == null) return;

        bool empty = _adapter.Bindings.Count == 0;
        _patchEmpty.Visible = empty;
        _patchHost.Visible = !empty;
        if (empty)
        {
            _patchEmpty.Heading = "这个适配包还没有绑定数值";
            _patchEmpty.Detail = "用 probe 重新生成，或手工编辑 adapter.json 的 bindings";
            return;
        }

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Tokens.Surface,
            Padding = new Padding(0),
            Margin = new Padding(0),
            ColumnCount = 1,
            RowCount = 0
        };

        foreach (var b in _adapter.Bindings)
        {
            // 集合语义用专门的卡片：一张卡里按条目分行，而不是只显示汇总。
            if (string.Equals(b.Kind, "memory_collection", StringComparison.OrdinalIgnoreCase)
                && !b.OwnerField)
            {
                // 不要在这里设 Dock=Fill：集合卡片高度随条目数变化，
                // Fill 会让它被 TableLayoutPanel 压成 0 高度而看不见。
                var cc = new CollectionCard(b.Semantic, CollectionDisplayName(b.Semantic));
                cc.ApplyRequested += (sem, id, v) => _ = ApplyCollectionValueAsync(sem, id, v);
                _collectionCards[b.Semantic] = cc;
                continue;
            }

            var style = b.Style switch
            {
                "fill" => ValueCard.Style.Fill,
                "step" => ValueCard.Style.Step,
                "toggle" => ValueCard.Style.Toggle,
                _ => ValueCard.Style.Number
            };

            var card = new ValueCard(
                b.Semantic,
                Semantics.Display(b.Semantic),
                style,
                b.Range,
                b.Step,
                b.FillFrom,
                b.AllowPersist,
                b.OnValue,
                b.Warning)
            {
                Margin = new Padding(Tokens.S2, Tokens.S2, Tokens.S2, Tokens.S2),
                Dock = DockStyle.Fill
            };

            card.ApplyRequested += (sem, val) => _ = ApplyValueAsync(sem, val.ToString());
            card.FillRequested += sem => _ = FillValueAsync(sem);
            card.ToggleRequested += (sem, on, persist) => _ = ToggleValueAsync(sem, on, persist);

            _cards[b.Semantic] = card;
        }

        _patchHost.Controls.Add(grid);
        _patchHost.Resize += (_, _) => RelayoutGrid(grid);
        RelayoutGrid(grid);
    }

    /// <summary>集合语义的显示名。适配包没配中文名时用语义名兜底。</summary>
    private static string CollectionDisplayName(string semantic) => semantic switch
    {
        "employee_good" => "员工好感度",
        "employee_bad" => "员工猥琐值",
        "employee_medical" => "员工医术",
        _ => Semantics.Display(semantic)
    };

    /// <summary>写集合里某个条目的值，成功后刷新整张卡片。</summary>
    private async Task ApplyCollectionValueAsync(string semantic, int entryId, int value)
    {
        if (_adapter == null) return;
        try
        {
            string raw = $"{entryId}={value}";
            var r = await Task.Run(() => AdapterRunner.Write(_adapter, semantic, raw, dryRun: false));
            if (!r.Ok) { Log($"[错误] {r.Error}"); return; }

            Log($"{Semantics.Display(semantic)}：{r.Before} → {r.After}");

            // 写成功后重读整张卡片，保证显示的数值与实际一致。
            var results = await Task.Run(() => AdapterRunner.Read(_adapter, new[] { semantic }));
            foreach (var rr in results)
            {
                if (_collectionCards.TryGetValue(rr.Semantic, out var card))
                    card.SetItems(rr.Items ?? new List<AdapterRunner.CollectionItem>(), rr.Ok, rr.Error);
            }
        }
        catch (Exception ex) { Log("[异常] " + ex.Message); }
    }

    private void RelayoutGrid(TableLayoutPanel grid)
    {
        if (grid == null || grid.IsDisposed) return;
        int w = _patchHost.ClientSize.Width - _patchHost.Padding.Horizontal;
        if (w < 200) return;

        // 集合卡片一行要放「名字+数值+输入框+应用」，实测 340px 才不裁名字。
        const int minCol = 380;
        int cols = Math.Max(1, w / minCol);
        if (cols > 2) cols = 2;

        // 单值卡片与集合卡片都要铺进网格。只取 _cards 会导致集合卡片
        // 完全不显示（它不在 _cards 里），表现为界面上只剩一张金币卡。
        var cards = new List<Control>();
        cards.AddRange(_cards.Values);
        cards.AddRange(_collectionCards.Values);
        if (cards.Count == 0) return;

        grid.SuspendLayout();
        grid.Controls.Clear();
        grid.ColumnStyles.Clear();
        grid.RowStyles.Clear();
        grid.ColumnCount = cols;
        for (int i = 0; i < cols; i++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));

        int rows = (cards.Count + cols - 1) / cols;
        grid.RowCount = rows;
        for (int i = 0; i < rows; i++)
        {
            // 行高按「本行最高卡片的真实内容高度」算。
            // 不能读 Control.Height：表格行高会反过来钳制卡片，
            // 读到的永远是上一次布局的值，导致永远差一截（10 行只显示 3 行）。
            int maxH = 0;
            for (int k = i * cols; k < Math.Min((i + 1) * cols, cards.Count); k++)
            {
                int h = cards[k] is CollectionCard cc ? cc.RequiredHeight : cards[k].Height;
                maxH = Math.Max(maxH, h);
            }
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(maxH + 8, 190)));
        }

        // Dock=None 的卡片要显式给定宽度：按列宽均分。
        // 高度用卡片自算值（SetItems 里按条目数得出），行高再按它设。
        int cellW = Math.Max(280, w / cols);
        foreach (Control card in cards)
            card.Width = cellW - 12;

        for (int i = 0; i < cards.Count; i++)
            grid.Controls.Add(cards[i], i % cols, i / cols);

        grid.ResumeLayout(true);
    }

    /// <summary>异步刷新全部数值（合并成 2 次往返，不阻塞 UI）。</summary>
    private bool _refreshing;

    private async Task RefreshValuesAsync()
    {
        if (_adapter == null) return;
        // 防重入：快速连点会并发建立 CDP 连接，前一次未完成不再发起（ui-design.md：长任务期间禁用冲突操作）
        if (_refreshing) { Log("刷新进行中，请稍候…"); return; }
        _refreshing = true;

        // 必须用 try/finally 释放标志：异常路径若提前 return，
        // 标志会永久为 true，之后再也刷新不了。
        try
        {
            // 「刷新数值」= 用户主动要求重新定位存档。
            // 清掉定位缓存，让下一次读取重新扫描（读档、换档后走这条）。
            SaveLocator.Invalidate();

            var adapter = _adapter;

            Dictionary<string, AdapterRunner.ReadResult> results;
            try
            {
                results = await Task.Run(() => AdapterRunner.ReadAll(adapter));
            }
            catch (Exception ex)
            {
                // 这里的 ex 是连接级失败（如端口没开），同样要压短：
                // 完整信息只在日志出现一次，卡片上只给一句原因。
                string shortMsg = ShortenError(ex.Message);
                foreach (var card in _cards.Values)
                    card.SetValue(null, false, shortMsg);
                Log("[错误] 读取失败：" + ex.Message.Replace("\n", " "));
                _attachBadge.SetStyle(Tokens.DangerSubtle, Tokens.Danger, "读取失败");
                return;
            }

            int ok = 0, fail = 0;
            string firstError = null;

            foreach (var kv in results)
            {
                // 集合语义走另一个字典：员工列表这类数据一张卡片里有 N 行，
                // 不能按单值卡片的 SetValue 处理，必须走 SetItems 重建行。
                if (_collectionCards.TryGetValue(kv.Key, out var ccard))
                {
                    ccard.SetItems(kv.Value.Items ?? new List<AdapterRunner.CollectionItem>(),
                                   kv.Value.Ok, ShortenError(kv.Value.Error));
                    if (kv.Value.Ok) ok++;
                    else { firstError ??= kv.Value.Error; fail++; }
                    continue;
                }

                if (!_cards.TryGetValue(kv.Key, out var card)) continue;

                if (kv.Value.Ok)
                {
                    card.SetValue(kv.Value.Value, true);
                    ok++;
                }
                else
                {
                    // 卡片上只显示简短原因，完整错误统一在日志里出现一次，
                    // 否则同一个连接失败会在 11 张卡片上重复 11 遍（实测截图确认过）。
                    card.SetValue(null, false, ShortenError(kv.Value.Error));
                    firstError ??= kv.Value.Error;
                    fail++;
                }
            }

            if (fail == 0)
            {
                Log($"已刷新数值：成功 {ok}");
                _attachBadge.SetStyle(Tokens.SuccessSubtle, Tokens.Success, "已连接");
            }
            else if (ok > 0)
            {
                // 部分成功 = 已连上，但有些值不在当前场景（如未开局时的局内数值）。
                // 这不是错误，用警告色而非危险色，避免使用者误判连接断了。
                Log($"已刷新数值：成功 {ok}，{fail} 项不在当前场景");
                _attachBadge.SetStyle(Tokens.SuccessSubtle, Tokens.Success, "已连接");
            }
            else
            {
                Log($"已刷新数值：失败 {fail} 项");
                if (firstError != null) Log("[错误] " + firstError.Replace("\n", " "));
                // 全失败才可能是连接问题，但要靠错误内容区分：
                // 若是"无实例/不在此场景"，仍是连上了只是不在局内。
                bool sceneOnly = firstError != null
                    && (firstError.Contains("无实例") || firstError.Contains("不在此场景"));
                if (sceneOnly)
                {
                    Log("[提示] 已连接，但当前不在对局内。进入游戏后点「刷新数值」。");
                    _attachBadge.SetStyle(Tokens.WarningSubtle, Tokens.Warning, "不在局内");
                }
                else
                {
                    _attachBadge.SetStyle(Tokens.DangerSubtle, Tokens.Danger, "读取失败");
                }
            }
        }
        finally
        {
            _refreshing = false;

            // 集合卡片填充后高度会变化，必须重排一次网格，
            // 否则行高停留在初始值，卡片被裁掉看不见。
            foreach (Control c in _patchHost.Controls)
            {
                if (c is not TableLayoutPanel tlp) continue;
                tlp.SuspendLayout();
                // 先让每张卡按内容确认自己的高度，再按最大值设行高。
                // 少了这一步，行高会是填充数据之前的旧值，
                // 表现为 10 个员工只显示 3 行、其余被裁掉。
                foreach (Control card in tlp.Controls) card.PerformLayout();
                RelayoutGrid(tlp);
                tlp.ResumeLayout(true);
            }
        }
    }

    /// <summary>同一语义的写入防重入：CDP 写超时可达 30 秒，连点不能并发写两次。</summary>
    private readonly HashSet<string> _writing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 写入单个语义。必须走后台线程：CDP 是同步阻塞 IO（超时 30 秒），
    /// 在 UI 线程执行会让窗口冻结——游戏卡顿时点任何卡片都假死。
    /// </summary>
    private async Task ApplyValueAsync(string semantic, string raw)
    {
        if (_adapter == null) return;
        if (!_writing.Add(semantic)) { Log($"{Semantics.Display(semantic)} 正在写入，请稍候…"); return; }
        var adapter = _adapter;
        try
        {
            var r = await Task.Run(() => AdapterRunner.Write(adapter, semantic, raw, dryRun: false));
            if (r.Ok)
            {
                Log($"{Semantics.Display(semantic)}: {r.Before} → {r.After}" + (r.Realtime ? "  [实时]" : "  [需重载]"));
                if (_cards.TryGetValue(semantic, out var card))
                    card.SetValue(r.After, true);
            }
            else
            {
                Log($"[失败] {Semantics.Display(semantic)}: {r.Error}");
                if (_cards.TryGetValue(semantic, out var card))
                    card.SetValue(null, false, r.Error);
            }
        }
        catch (Exception ex)
        {
            Log("[错误] " + ex.Message);
        }
        finally
        {
            _writing.Remove(semantic);
        }
    }

    /// <summary>「回满」：取 FillFrom 指向的语义当前值作为目标。读取走后台线程。</summary>
    private async Task FillValueAsync(string semantic)
    {
        if (_adapter == null) return;
        var b = _adapter.FindBinding(semantic);
        string target = null;

        if (!string.IsNullOrWhiteSpace(b?.FillFrom))
        {
            var adapter = _adapter;
            var src = await Task.Run(() => AdapterRunner.Read(adapter, new[] { b.FillFrom }));
            var hit = src.FirstOrDefault(x => x.Ok);
            if (hit != null) target = hit.Value;
        }

        // 退路：没有 fillFrom 时用 binding 自身 range 上限
        if (string.IsNullOrWhiteSpace(target) && b?.Range is { Length: 2 })
            target = b.Range[1].ToString("0");

        if (string.IsNullOrWhiteSpace(target))
        {
            Log($"[提示] {Semantics.Display(semantic)} 没有配置 fillFrom，无法回满。");
            return;
        }

        await ApplyValueAsync(semantic, target);
    }

    private async Task ToggleValueAsync(string semantic, bool on, bool persist)
    {
        if (_adapter == null) return;
        var b = _adapter.FindBinding(semantic);
        double target = on ? (b?.OnValue ?? 1) : 0;
        await ApplyValueAsync(semantic, target.ToString("0"));

        // 持久监控：只有用户显式勾选且开关为开时才启动
        StopWatcher(semantic);

        if (on && persist)
        {
            var w = new PersistentWatcher(_adapter, semantic, target);
            w.Notified += msg => Log($"[持久] {Semantics.Display(semantic)}：{msg}");
            // 监控静默死亡是挂机场景最疼的失败：日志区收起时用户不知道盾没了。
            // 停止走系统托盘气泡，绕开日志可见性。
            w.Stopped += reason =>
            {
                try
                {
                    BeginInvoke(() => _tray.ShowBalloonTip(3000, "持久监控停止",
                        $"{Semantics.Display(semantic)}：{reason}", ToolTipIcon.Warning));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            };
            w.Start();
            _watchers[semantic] = w;
            Log($"[持久] {Semantics.Display(semantic)} 监控已启动（每 1 秒检查，退出对局自动停止）");
        }
        else if (on)
        {
            Log($"{Semantics.Display(semantic)} 已开启（非持久，被打掉后不会自动补回）");
        }
    }

    /// <summary>停止并移除某个语义的持久监控。</summary>
    private void StopWatcher(string semantic)
    {
        if (_watchers.TryGetValue(semantic, out var w))
        {
            w.Stop();
            w.Dispose();
            _watchers.Remove(semantic);
        }
    }

    /// <summary>停止全部持久监控（切换游戏/退出程序）。停止原因写进日志，不让监控静默消失。</summary>
    private void StopAllWatchers(string reason)
    {
        foreach (var w in _watchers.Values)
        {
            w.Stop();
            w.Dispose();
        }
        if (_watchers.Count > 0)
            Log($"[持久] 已停止 {_watchers.Count} 个持久监控（{reason}）");
        _watchers.Clear();
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

    // DoAttach（旧 profile 附加）已于 2026-10-21 删除：
    // 该方法零调用点，且代码自承「旧 profile 附加模式当前没有 UI 入口」。
    // 适配包体系（sem 命令）已完全覆盖其功能。
    // 若将来恢复旧 profile UI 入口，参考 git 历史重建。

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
        // 关窗竞态兜底：watcher/写入的回调可能在 handle 销毁后仍在飞行，
        // 此时 BeginInvoke 或 Append 会抛异常炸掉后台线程（进程崩溃）。静默放弃日志。
        try
        {
            if (_logBox.InvokeRequired) { _logBox.BeginInvoke(() => Log(msg)); return; }
            _logBox.Append($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
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
        StopAllWatchers("退出程序");
        if (_engine != null)
        {
            _engine.StopAllFreezes();
            _engine.RevertAllPending();
        }
        _hotkeys?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        // _mem 已随 DoAttach 删除
    }

    /// <summary>供自检/诊断读取当前材质结果，UI 测试也用它断言回退行为。</summary>
    public string MaterialDetail => _material.Detail;
    public bool MaterialApplied => _material.Applied;
}
