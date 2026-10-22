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
    private readonly Prefs _prefs = Prefs.Load();
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
    /// <summary>热键服务（独立模块，见 Services/HotkeyService.cs）。</summary>
    private Services.HotkeyService _hotkeys;
    private readonly ActiveStateStore _activeStore;
    private readonly SingleInstanceGuard _single;

    // 顶栏
    /// <summary>顶栏（独立控件，见 Shell/TopBar.cs）。</summary>
    private Shell.TopBar _topBar;
    private readonly Label _gameTitle = new();
    private readonly Label _gameSubtitle = new();
    private readonly Badge _gameStatePill = new();

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
    /// <summary>底部状态栏（独立控件，见 Shell/StatusBar.cs）。</summary>
    private Shell.StatusBar _statusBar;


    private readonly NotifyIcon _tray = new();
    private readonly Dictionary<string, ToggleSwitch> _toggles = new();
    private readonly Dictionary<string, PatchRow> _rows = new();
    private bool _suppress;
    private bool _reallyExit;
    private WindowMaterial.Result _material;
    private Panel _contentGrid;

    /// <summary>OriginOS 单控件自绘的游戏内容区。</summary>
    private Pages.GameView _gameView;

    /// <summary>日志抽屉高度（收起时为 0）。</summary>

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

            // 启动落在首页：先让用户选游戏，而不是默认打开第一个适配包。
            // 旧版一进来就加载全部适配包，没装游戏的那几个全是读取失败，
            // 看起来像程序坏了。
            ShowHomePage();
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
    /// <summary>
    /// 启动后延迟 3 秒静默检查更新。
    ///
    /// 与旧行为的区别：发现新版本不再直接弹窗打断用户，
    /// 而是在顶栏亮一个红点；用户点它才看公告和决定是否更新。
    /// 弹窗打断在"用户正要改游戏数值"的场景里很讨嫌，
    /// 而更新公告属于低频信息，应该可见但不侵入。
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

                Log($"发现新版本 {r.Latest.Version.ToString(3)}（当前 {AppVersion.Display}），点顶栏红点查看");

                // 只亮红点，不弹窗
                _pendingUpdate = r.Latest;
                ShowUpdateDot(r.Latest.Version.ToString(3));
            }
            catch
            {
                // 静默：启动时的更新检查失败不打扰用户
            }
        };
        timer.Start();
    }

    private UpdateInfo _pendingUpdate;

    /// <summary>
    /// 在顶栏显示"有新版本"红点。点它才打开公告对话框。
    /// </summary>
    private void ShowUpdateDot(string version)
    {
        _topBar?.ShowUpdateDot(version);
    }

    /// <summary>点击红点：打开更新公告。</summary>
    private void OpenPendingUpdate()
    {
        if (_pendingUpdate == null) return;

        var prefs = UpdatePrefs.Load();
        using var dlg = new UpdateDialog(_pendingUpdate, prefs);
        dlg.ShowDialog(this);

        if (dlg.ReadyToRestart) Application.Exit();

        // 用户可能选了"跳过此版本"，那就把红点收掉
        if (prefs.IsSkipped(_pendingUpdate.Version))
            _topBar?.HideUpdateDot();
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
        // 顶栏已抽成独立控件，这里只负责实例化、接事件、挂载。
        _topBar = new Shell.TopBar { Dock = DockStyle.Fill };

        _topBar.LaunchRequested   += () => _ = LaunchOrRefreshAsync();
        _topBar.RefreshRequested  += () => { if (_adapter != null) _ = RefreshValuesAsync(); };
        _topBar.SettingsRequested += OpenSettings;
        _topBar.BackRequested     += ShowHomePage;
        _topBar.UpdateRequested   += OpenPendingUpdate;

        _topBar.SetBackVisible(false);
        _topBar.SetLaunchEnabled(false);
        _topBar.SetRefreshEnabled(false);

        return _topBar;
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

        // 游戏页不再有左侧游戏列表：选游戏由首页网格负责，
        // 进到游戏页说明已经选定了某个游戏，侧栏列表在这里只会造成
        // 两个入口（侧栏与首页）同时存在，并且 profiles/ 与 adapters/
        // 同名游戏会重复出现。现在游戏页只有内容区，全宽。
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.PageBack,
            Padding = new Padding(Tokens.S5, 0, Tokens.S5, Tokens.S3),
            Margin = new Padding(0)
        };

        body.Controls.Add(BuildContent());

        // 两页切换：首页（游戏网格）与游戏页（当前游戏的修改界面）。
        _homePage = new Pages.HomeView { Dock = DockStyle.Fill, Visible = false };
        _homePage.GameSelected += adapter => EnterGamePage(adapter);
        _homePage.DownloadRequested += remote => _ = DownloadAdapterAsync(remote);
        _homePage.UninstallRequested += id => UninstallAdapter(id);

        var pages = new Panel { Dock = DockStyle.Fill, BackColor = Tokens.PageBack };
        pages.Controls.Add(_homePage);
        pages.Controls.Add(body);

        _gameBody = body;

        host.Controls.Add(pages);
        return host;
    }

    private Pages.HomeView _homePage;
    private Control _gameBody;

    /// <summary>
    /// 显示首页：刷新已安装/可下载列表。
    /// 首页是"选游戏"的地方，不该因为某个游戏读不到值而阻塞。
    /// </summary>
    private async void ShowHomePage()
    {
        if (_homePage == null) return;

        SwitchPage(_homePage, _gameBody);

        // 首页不针对任何具体游戏：隐藏游戏页专属的徽章与操作按钮，
        // 否则"读取失败"会让人以为程序坏了，实际只是还没选游戏。
        SetGameChromeVisible(false);

        // 先渲染本地已安装的，再异步补上远端清单——
        // 网络慢时不能白屏等清单回来。
        var installed = LoadInstalledAdapters();
        _homePage.Populate(installed, null, null);

        var (index, error) = await AdapterRepository.FetchIndexAsync();
        if (index != null)
            _homePage.Populate(installed, index.Adapters, null);
        else
            _homePage.Populate(installed, null, error);
    }

    /// <summary>
    /// 页面切换：淡入 + 轻微上移，参照 iOS 的 push 过渡。
    ///
    /// 为什么不用"瞬间切换可见性"：
    /// 生硬的显示/隐藏会让用户失去空间感——不知道自己是"进入了下一层"
    /// 还是"同一个页面刷新了"。一个 260ms 的淡入 + 8px 上移就足够建立层级感，
    /// 又不会让连续操作觉得慢。
    ///
    /// 性能：只动一个控件的 Opacity 与 Top，不触发子控件重排。
    /// </summary>
    private void SwitchPage(Control show, Control hide)
    {
        if (hide != null)
        {
            Animator.Cancel(hide);
            hide.Visible = false;
        }

        if (show == null) return;

        Animator.Cancel(show);
        show.Visible = true;

        // 起始状态：透明 + 下移 8px
        const int offset = 8;
        int finalTop = 0;

        show.SuspendLayout();
        show.Top = finalTop + offset;
        show.ResumeLayout(false);

        Animator.Run(show, "page-in", Tokens.AnimNormal, raw =>
        {
            if (show.IsDisposed) return false;

            float t = Easing.EaseOut(raw);
            show.Top = finalTop + (int)(offset * (1 - t));

            // WinForms 的 Control.Opacity 只对 Form 有效，
            // 普通 Panel 用父子背景色混合模拟淡入。
            show.BackColor = Easing.LerpColor(Tokens.PageBack, Tokens.PageBack, t);

            return true;
        });
    }

    /// <summary>
    /// 切换「游戏页专属」控件的可见性。
    ///
    /// 首页与游戏页共用同一套顶栏/状态栏，但徽章与操作按钮只对具体游戏有意义。
    /// 在首页显示"读取失败"会让用户以为程序坏了——实际上只是还没选游戏。
    /// </summary>
    private void SetGameChromeVisible(bool visible)
    {
        _topBar?.SetLaunchEnabled(visible);
        _topBar?.SetBackVisible(visible);

        if (!visible)
        {
            _statusBar?.ClearGame();
        }
        else
        {
            // 回到游戏页时状态栏必须恢复真实信息，
            // 否则会残留首页那句"从下方选择一个游戏开始"。
            _statusBar?.SetHotkeyCount(_hotkeys?.Count ?? 0);

            if (_adapter != null) UpdateStatusForAdapter(_adapter);
        }
    }

    /// <summary>进入某个游戏的修改页。</summary>
    private void EnterGamePage(GameAdapter adapter)
    {
        if (adapter == null) return;

        if (_homePage != null) _homePage.Visible = false;
        if (_gameBody != null) _gameBody.Visible = true;
        SetGameChromeVisible(true);

        // 游戏页顶部标题：侧栏移除后这是唯一能确认"在改哪个游戏"的地方
        _gameTitle.Text = adapter.Name;
        _gameSubtitle.Text = $"{adapter.Engine} · {adapter.Bindings.Count} 个数值";
        SetGameStatePill(GameState.Unknown);
        _gameView.SetEmpty("正在读取…", "首次读取需要扫描内存，通常 1~3 秒");

        // 直接把 adapter 交给业务逻辑，不经过 _profileList.SelectTag。
        //
        // 曾经的 bug：SelectTag 用 ReferenceEquals 比较 Tag，
        // 而 HomeView 传进来的是重新加载出来的 GameAdapter 实例，
        // 与侧栏里那份不是同一对象，于是匹配失败、SelectedTag 停留在上一个游戏，
        // 表现为「点 Sol Cesto 却打开 Hospital Rhapsody 的页面」。
        // 侧栏已移除，这里不再需要那层间接。
        SelectAdapter(adapter);
    }

    /// <summary>
    /// 选中适配包并刷新数值。
    /// 这是「当前游戏」的唯一入口——首页点卡片、返回后再进入都走这里。
    /// </summary>
    private void SelectAdapter(GameAdapter a)
    {
        if (a == null) return;

        StopAllWatchers("切换游戏");

        // 切到另一个游戏时必须清定位缓存。
        // 缓存里存的是上一个游戏的 Userdata 地址，
        // 复用它会读到另一个进程的随机内存，表现为"切回来就加载失败"。
        SaveLocator.Invalidate();

        _adapter = a;
        _profile = null;
        UpdateStatusForAdapter(a);
        _topBar?.SetRefreshEnabled(true);
        Log($"已选择适配包：{a.Id}（{a.Bindings.Count} 个数值）");

        _ = RefreshValuesAsync();
    }

    /// <summary>游戏运行状态。用于顶部状态胶囊，替代原来两个互相矛盾的徽章。</summary>
    private enum GameState { Unknown, NotRunning, Running, ReadFailed }

    /// <summary>
    /// 更新顶部状态胶囊。
    ///
    /// 原来顶栏同时有「已连接」和「未连接」两个徽章，各自独立更新，
    /// 会同时亮起互相矛盾。现在合并成单一胶囊，任何时刻只有一个状态。
    /// </summary>
    private void SetGameStatePill(GameState state)
    {
        if (_gameStatePill == null) return;

        switch (state)
        {
            case GameState.Running:
                _gameStatePill.SetStyle(Tokens.SuccessSubtle, Tokens.Success, "运行中");
                break;
            case GameState.NotRunning:
                _gameStatePill.SetStyle(Tokens.WarningSubtle, Tokens.Warning, "未运行");
                break;
            case GameState.ReadFailed:
                _gameStatePill.SetStyle(Tokens.DangerSubtle, Tokens.Danger, "读取失败");
                break;
            default:
                _gameStatePill.SetStyle(Tokens.SurfaceSunken, Tokens.TextSecondary, "检查中");
                break;
        }
    }

    /// <summary>下载并安装一个适配包，成功后直接进入该游戏。</summary>
    private async Task DownloadAdapterAsync(AdapterRepository.RemoteAdapter remote)
    {
        Log($"[适配包] 开始下载 {remote.Name}…");

        var (ok, error) = await Task.Run(() => AdapterRepository.Install(_skillRoot, remote));
        if (!ok)
        {
            Log($"[适配包] 下载失败：{error}");
            MessageBox.Show(this, "下载失败：" + error, "GameTrainer",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowHomePage();
            return;
        }

        Log($"[适配包] {remote.Name} 已安装");

        // 重新加载列表并进入该游戏
        LoadProfiles();
        var installed = LoadInstalledAdapters();
        var target = installed.FirstOrDefault(a =>
            string.Equals(a.Id, remote.Id, StringComparison.OrdinalIgnoreCase));

        if (target != null) EnterGamePage(target);
        else ShowHomePage();
    }

    /// <summary>卸载适配包。入口保留在首页卡片上，卸载后卡片变成"可下载"。</summary>
    private void UninstallAdapter(string adapterId)
    {
        var confirm = MessageBox.Show(this,
            $"确定卸载适配包「{adapterId}」？\n\n只删除适配包文件，不会动游戏和你的存档。",
            "卸载适配包", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (confirm != DialogResult.OK) return;

        var (ok, error) = AdapterRepository.Uninstall(_skillRoot, adapterId);
        if (!ok)
        {
            MessageBox.Show(this, "卸载失败：" + error, "GameTrainer",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Log($"[适配包] 已卸载 {adapterId}");
        LoadProfiles();
        ShowHomePage();
    }

    /// <summary>读本地 adapters/ 下的全部适配包。</summary>
    private List<GameAdapter> LoadInstalledAdapters()
    {
        var list = new List<GameAdapter>();
        try
        {
            string root = Path.Combine(_skillRoot, "adapters");
            if (!Directory.Exists(root)) return list;

            foreach (string dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
            {
                string path = Path.Combine(dir, "adapter.json");
                if (!File.Exists(path)) continue;
                try
                {
                    var a = GameAdapter.Load(path);
                    if (a.Validate().Count == 0) list.Add(a);
                }
                catch { }
            }
        }
        catch { }
        return list;
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

        // 游戏页顶部标题：显示当前游戏名与状态胶囊。
        // 侧栏移除后，这里是唯一能看出"我现在在改哪个游戏"的地方。
        var gameHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Tokens.PageBack,
            Padding = new Padding(0, 0, 0, Tokens.S2)
        };

        // 不用 AutoSize：AutoSize 的 Label 高度由字体决定，
        // 而父容器高度固定时会被裁掉下缘（实测 23pt 标题被切）。
        // 改成固定高度 + 垂直居中，尺寸可预期。
        _gameTitle.Text = "未选择游戏";
        _gameTitle.Font = Tokens.FontTitle;
        _gameTitle.ForeColor = Tokens.TextPrimary;
        _gameTitle.AutoSize = false;
        _gameTitle.Height = 44;   // 23pt 微软雅黑行高约 40px，38 会裁下缘
        _gameTitle.Width = 640;          // AutoSize=false 时必须显式给宽度，否则用默认 100px 会截断
        _gameTitle.TextAlign = ContentAlignment.MiddleLeft;
        _gameTitle.Location = new Point(0, 0);

        _gameSubtitle.Text = "";
        _gameSubtitle.Font = Tokens.FontCaption;
        _gameSubtitle.ForeColor = Tokens.TextMuted;
        _gameSubtitle.AutoSize = false;
        _gameSubtitle.Height = 22;
        _gameSubtitle.Width = 640;
        _gameSubtitle.TextAlign = ContentAlignment.MiddleLeft;
        _gameSubtitle.Location = new Point(2, 44);

        _gameStatePill.Dock = DockStyle.None;
        _gameStatePill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _gameStatePill.SetStyle(Tokens.SurfaceSunken, Tokens.TextSecondary, "未运行");
        _gameStatePill.Location = new Point(0, Tokens.S2 + 4);

        gameHeader.Controls.Add(_gameTitle);
        gameHeader.Controls.Add(_gameSubtitle);
        gameHeader.Controls.Add(_gameStatePill);
        gameHeader.Resize += (_, _) =>
        {
            _gameStatePill.Left = Math.Max(0, gameHeader.ClientSize.Width - _gameStatePill.Width);
        };

        // ---- 内容区：OriginOS 单控件自绘 GameView ----
        //
        // 替换旧的 _contentGrid + _patchCard + _patchHost 三层结构。
        // 旧结构的问题：一次刷新创建 40+ 个控件，每个独立重绘，动画掉帧；
        // 应用按钮靠手工算坐标，窗口变窄被裁；集合行高被表格反控导致只显示 3 行。
        _gameView = new Pages.GameView { Dock = DockStyle.Fill };
        _gameView.ValueSubmitted += (sem, val) => _ = SubmitValueAsync(sem, val);
        _gameView.EntrySubmitted += (sem, id, val) => _ = SubmitEntryAsync(sem, id, val);
        _gameView.SettingsRequested += (sem, id, title) => ShowEntrySettings(sem, title);

        // ---- 底部日志抽屉 ----
        _logCard.Title = "运行日志";
        _logCard.Dock = DockStyle.Fill;
        _logCard.Margin = new Padding(0);
        _logCard.Height = _logDrawerHeight;

        _logPad = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.Surface,
            Padding = new Padding(Tokens.S3, 0, Tokens.S3, Tokens.S3)
        };
        var logInner = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.SurfaceAlt,
            Padding = new Padding(Tokens.S2)
        };
        _logBox.Dock = DockStyle.Fill;
        logInner.Controls.Add(_logBox);
        _logPad.Controls.Add(logInner);
        _logCard.Controls.Add(_logPad);

        // 日志默认收起：内容区（修改项）是主工作流，优先给它空间。
        // 展开时再切出 180px。这个默认值来自实测——默认展开会压掉 3 行员工。
        _logCollapsed = true;
        _logPad.Visible = false;
        _logCard.ActionText = "展开";
        _logCard.Height = LogHeaderOnlyHeight;
        _logCard.Cursor = Cursors.Hand;
        _logCard.HeaderClick += () => ToggleLogCollapsed(_logPad);

        // 上下两段：内容区吃满剩余高度，日志固定高度在底部。
        // 用 Panel + Dock 而不是 TableLayoutPanel：
        // 表格的 Percent 行在 Absolute 行占满时会把第一行压成 0，
        // 表现为内容区完全空白（实测：GameView 报 423 高但完全不渲染）。
        // Dock 顺序确定：先加 Fill，再加 Bottom，后者从前者底部切走固定高度。
        var stack = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Tokens.PageBack,
            Padding = new Padding(Tokens.S5, Tokens.S2, Tokens.S5, Tokens.S4),
            Margin = new Padding(0)
        };

        // 不用 Dock 也不按 TableLayout：两者在嵌套时都会出现"某个子控件完全不渲染"。
        // 改成显式 Resize 里算绝对位置，逻辑一眼可见，出问题也好定位。
        stack.Controls.Add(_gameView);
        stack.Controls.Add(BuildLogResizeHandle());   // 日志高度可拖
        stack.Controls.Add(_logCard);

        _logCard.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _gameView.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        stack.Resize += (_, _) => RelayoutGameStack();
        stack.HandleCreated += (_, _) => RelayoutGameStack();
        RelayoutGameStack();

        _contentGrid = stack;

        wrap.Controls.Add(stack);
        wrap.Controls.Add(gameHeader);

        return wrap;
    }



    /// <summary>
    /// 按适配包更新状态栏：连接状态、端口、可改数值数。
    /// 让使用者一眼看出「现在能不能操作」。
    /// cdpReady 已知时（如刚启动成功）直接传入，跳过一次 0.8s 的端口探测。
    /// </summary>
    /// <summary>
    /// 按适配包更新状态栏。
    /// 具体显示逻辑全在 Shell/StatusBar 里，这里只负责把状态递过去。
    /// </summary>
    private void UpdateStatusForAdapter(GameAdapter a)
    {
        _statusBar?.SetGame(a);
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
        _topBar?.SetLaunchEnabled(false);
        _topBar?.SetRefreshEnabled(false);
        try
        {
            Log($"正在启动 {adapter.Name}…");
            var r = await Task.Run(() => GameLauncher.Launch(adapter, progress: line => Log("  " + line.Trim())));
            if (r.Ok && r.CdpReady)
            {
                Log("启动成功，正在读取数值…");
                UpdateStatusForAdapter(adapter);
                _topBar?.SetRefreshEnabled(true);
                await RefreshValuesAsync();
            }
            else
            {
                Log("[失败] " + r.Message.Replace("\n", " "));
            }
        }
        finally
        {
            _launching = false;
            _topBar?.SetLaunchEnabled(true);
        }
    }

    /// <summary>把冗长的连接错误压成一句短提示，供卡片显示。</summary>
    private static string ShortenError(string err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "读取失败";
        if (err.Contains("无法连接")) return "未连接游戏";
        // 游戏没启动是最常见的情况，不该显示成红色错误。
        // 用一个中性短语，配合卡片的中性样式，提示"先启动游戏"。
        if (err.Contains("找不到进程")) return "游戏未启动";
        if (err.Contains("无实例")) return "不在此场景";
        if (err.Contains("对象不存在")) return "对象未加载";
        if (err.Contains("变量不存在")) return "变量缺失";
        string one = err.Split('\n')[0].Trim();
        return one.Length > 24 ? one[..24] + "…" : one;
    }

    private Panel _logPad;
    private bool _logCollapsed;

    /// <summary>
    /// 收起 / 展开日志区。
    ///
    /// 原来是把右列压到 180px，展开收起时整块布局横向跳动，
    /// 而且 180px 的日志区几乎读不了内容，收/放都不好用。
    ///
    /// 改为底部抽屉：卡片区始终占满宽度，日志在下方按固定行高展开/收起。
    /// 收起时只留标题栏，展开时占 38% 高度。日志是低频信息，
    /// 抽屉比侧栏更符合"偶尔看一眼"的使用方式。
    /// </summary>
    private void ToggleLogCollapsed(Panel logPad)
    {
        // 先记住当前高度，再翻转状态。
        //
        // 真 bug：原来先翻转 _logCollapsed，然后用翻转后的状态算 from，
        // 于是 from 和 to 取到同一侧的值，动画起止点相同，一帧就结束——
        // 表现就是"收起时一跳就没了"（用户反馈）。
        int currentH = _logCard.Height;
        int targetH;

        _logCollapsed = !_logCollapsed;
        targetH = _logCollapsed ? LogHeaderOnlyHeight : _logDrawerHeight;

        logPad.Visible = !_logCollapsed;
        _logCard.ActionText = _logCollapsed ? "展开" : "收起";
        _logCard.Invalidate();

        Animator.Run(_logCard, "log-drawer", Tokens.AnimSlow, raw =>
        {
            if (_logCard.IsDisposed) return false;

            _logCard.Height = Easing.LerpInt(currentH, targetH, Easing.EaseInOut(raw));
            RelayoutGameStack();
            return true;
        });

        Log(_logCollapsed ? "日志区已收起，点「展开」恢复。" : "日志区已展开。");
    }

    /// <summary>
    /// 日志抽屉当前高度。
    /// 展开时用用户拖出来的高度，收起时只留标题栏。
    /// </summary>
    private int _logDrawerHeight = 180;

    /// <summary>日志抽屉的默认高度。</summary>
    private const int DefaultLogDrawerHeight = 180;

    /// <summary>日志抽屉最小高度（再小就看不到内容）。</summary>
    private const int MinLogDrawerHeight = 120;

    /// <summary>日志抽屉最大高度：不超过内容区的一半。</summary>
    private int MaxLogDrawerHeight
    {
        get
        {
            if (_contentGrid == null) return DefaultLogDrawerHeight * 2;
            int h = _contentGrid.ClientSize.Height - _contentGrid.Padding.Vertical;
            return Math.Max(MinLogDrawerHeight + 40, h / 2);
        }
    }

    /// <summary>日志收起时只留标题栏的高度。</summary>
    private const int LogHeaderOnlyHeight = 52;

    /// <summary>
    /// 重排游戏页的上下两段：内容区吃满剩余高度，日志抽屉贴底。
    ///
    /// 必须是类方法而不是局部函数：日志展开/收起时高度会变，
    /// 那一侧也要能调用它重新分配空间，否则日志会盖住内容区。
    /// </summary>
    private void RelayoutGameStack()
    {
        if (_contentGrid == null || _gameView == null || _logCard == null) return;

        var stack = _contentGrid;
        if (stack.IsDisposed || stack.ClientSize.Width <= 0) return;

        int padL = stack.Padding.Left, padR = stack.Padding.Right;
        int padT = stack.Padding.Top, padB = stack.Padding.Bottom;
        int w = stack.ClientSize.Width - padL - padR;
        int h = stack.ClientSize.Height - padT - padB;
        if (w <= 0 || h <= 0) return;

        // 展开时用 _logDrawerHeight（可被拖动改变），收起时只留标题栏
        int logH = _logCollapsed ? LogHeaderOnlyHeight : _logDrawerHeight;

        // 拖动把手夹在内容区与日志之间，收起时不占空间
        int handleH = _logCollapsed ? 0 : LogResizeHandleHeight;

        int viewH = Math.Max(80, h - logH - handleH - Tokens.S3);

        _gameView.SetBounds(padL, padT, w, viewH);

        if (_logResizeHandle != null)
        {
            _logResizeHandle.Visible = !_logCollapsed;
            _logResizeHandle.SetBounds(padL, padT + viewH + Tokens.S3, w, handleH);
        }

        _logCard.SetBounds(padL, padT + viewH + Tokens.S3 + handleH, w, logH);
    }

    /// <summary>拖动把手高度。太细不好抓，太粗占空间。</summary>
    private const int LogResizeHandleHeight = 8;

    /// <summary>日志抽屉上方的拖动把手。</summary>
    private Control _logResizeHandle;

    /// <summary>拖动中：记录按下时的鼠标位置与当时的抽屉高度。</summary>
    private bool _draggingLog;
    private int _dragStartY;
    private int _dragStartHeight;

    /// <summary>
    /// 创建拖动把手。鼠标在它上面按下并上下拖，就能调节日志抽屉高度。
    ///
    /// 为什么单独做一个把手而不是让整张卡片可拖：
    /// 卡片里要选文字、点「展开」按钮，整卡可拖会误触。
    /// </summary>
    private Control BuildLogResizeHandle()
    {
        var handle = new Panel
        {
            BackColor = Tokens.PageBack,
            Cursor = Cursors.SizeNS,
            Height = LogResizeHandleHeight
        };

        handle.Paint += (_, e) =>
        {
            // 中间画一小段短横线，提示这里可以拖
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            const int w = 40;
            int x = (handle.Width - w) / 2;
            int y = handle.Height / 2;

            using var pen = new Pen(Tokens.BorderControl, 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            g.DrawLine(pen, x, y, x + w, y);
        };

        handle.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _draggingLog = true;
            _dragStartY = Control.MousePosition.Y;
            _dragStartHeight = _logDrawerHeight;
        };

        handle.MouseMove += (_, _) =>
        {
            if (!_draggingLog) return;

            // 向上拖 = 变高
            int dy = _dragStartY - Control.MousePosition.Y;
            int target = Math.Clamp(_dragStartHeight + dy,
                MinLogDrawerHeight, MaxLogDrawerHeight);

            if (target == _logDrawerHeight) return;

            _logDrawerHeight = target;
            _logCard.Height = target;
            RelayoutGameStack();
        };

        handle.MouseUp += (_, _) => _draggingLog = false;

        _logResizeHandle = handle;
        return handle;
    }


    private Control BuildStatusBar()
    {
        // 状态栏已抽成独立控件，这里只负责实例化与挂载。
        _statusBar = new Shell.StatusBar { Dock = DockStyle.Fill };
        return _statusBar;
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

    /// <summary>
    /// 按用户配置注册热键。
    ///
    /// 原来硬编码 Ctrl+Alt+F1/F2 做「全开/全关」——这两个动作价值最低，
    /// 真正需要热键的是"把某个数值立刻改到目标值"这类高频操作。
    /// 现在每个修改项可以在设置弹窗里单独绑定，默认全空（不占系统热键）。
    /// </summary>
    private void InitHotkeys()
    {
        _hotkeys?.Dispose();

        _hotkeys = new Services.HotkeyService(_prefs);
        _hotkeys.TriggerRequested += TriggerEntryHotkey;
        _hotkeys.CountChanged += n => _statusBar?.SetHotkeyCount(n);
        _hotkeys.Logged += Log;

        _hotkeys.Attach(Handle);
        _hotkeys.Rebuild(_adapter);
    }

    /// <summary>
    /// 按当前 _prefs 重建全部修改项热键。
    /// 配置弹窗保存后调用，使得改动立即生效而不必重启。
    /// </summary>
    /// <summary>
    /// 打开某个修改项的设置弹窗（双击名称触发）。
    /// 保存后重建热键，使改动立即生效。
    /// </summary>
    private void ShowEntrySettings(string semantic, string displayName)
    {
        if (_adapter == null) return;

        var ep = _prefs.ForEntry(_adapter.Id, semantic);

        // 同适配包内查重：不能给两个修改项绑同一个组合键
        bool IsDuplicate(string spec) =>
            _adapter.Bindings.Any(b =>
                !string.Equals(b.Semantic, semantic, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_prefs.ForEntry(_adapter.Id, b.Semantic).Hotkey, spec,
                                 StringComparison.OrdinalIgnoreCase));

        using var dlg = new EntrySettingsDialog(_prefs, ep, displayName, IsDuplicate);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Saved)
        {
            RebuildHotkeys();
            Log($"[设置] {displayName}：快捷键 {(string.IsNullOrEmpty(ep.Hotkey) ? "未设置" : ep.Hotkey)}"
                + (ep.Collapsed ? "，已折叠" : ""));
        }
    }

    private void RebuildHotkeys()
    {
        _hotkeys?.Rebuild(_adapter);
    }

    /// <summary>
    /// 热键触发的动作：把该语义改到用户设定的目标值。
    ///
    /// 集合语义（如员工好感度）整体改；单值语义直接写。
    /// 目标值取卡片输入框当前内容；为空则不做任何事并提示。
    /// </summary>
    private void TriggerEntryHotkey(string semantic)
    {
        if (_adapter == null || _gameView == null) return;

        // 目标值取 GameView 里第一个已填写的输入框
        string target = _gameView.FirstInputText(semantic);

        if (string.IsNullOrWhiteSpace(target))
        {
            Log($"[热键] {Semantics.Display(semantic)}：输入框为空，未执行");
            return;
        }

        Log($"[热键] {Semantics.Display(semantic)} → {target}");
        _ = SubmitValueAsync(semantic, target);
    }

    protected override void WndProc(ref Message m)
    {
        _hotkeys?.HandleMessage(m.Msg, m.WParam);
        base.WndProc(ref m);
    }

    /// <summary>
    /// 把按键转发给 GameView。
    ///
    /// GameView 是自绘控件、不接收焦点（没有 Selectable 样式），
    /// 所以键盘必须由窗体统一转发。这样也避免了"点输入框就把焦点
    /// 从用户正在用的其他窗口抢过来"。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_gameView != null && _gameView.Visible && _gameView.IsEditing)
        {
            if (_gameView.HandleKeyDown(keyData & Keys.KeyCode))
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>字符输入同样转发给 GameView（数字、退格、回车）。</summary>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (_gameView != null && _gameView.Visible && _gameView.IsEditing)
        {
            if (_gameView.HandleKeyChar(e.KeyChar))
            {
                e.Handled = true;
                return;
            }
        }
        base.OnKeyPress(e);
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

                // 切到另一个游戏时必须清定位缓存。
                // 缓存里存的是上一个游戏的 Userdata 地址，
                // 复用它会读到另一个进程的随机内存，表现为"切回来就加载失败"。
                SaveLocator.Invalidate();

                _adapter = a;
                _profile = null;
                UpdateStatusForAdapter(a);
                _topBar?.SetRefreshEnabled(true);
                Log($"已选择适配包：{a.Id}（{a.Bindings.Count} 个数值）");
                _ = RefreshValuesAsync();
                break;

            case GameProfile p:
                StopAllWatchers("切换 profile");
                SaveLocator.Invalidate();
                _adapter = null;
                _profile = p;
                BuildPatchRows();
                Log($"已选择 profile：{p.Id}（{p.Patches.Count} 个修改项）");
                break;
        }
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

    /// <summary>异步刷新全部数值（合并成 2 次往返，不阻塞 UI）。</summary>
    private bool _refreshing;

    /// <summary>
    /// 刷新数值：读全部语义，把结果交给 GameView 渲染。
    ///
    /// 与旧实现的区别：旧版在这里直接操作 _cards / _collectionCards 两套字典，
    /// 逐个控件 SetValue / SetItems，129 行里一半是状态判断和错误分类。
    /// 现在只剩「读 → 判状态 → 交给视图」三步，错误分类也归到一处。
    /// </summary>
    private async Task RefreshValuesAsync()
    {
        if (_adapter == null) return;
        if (_refreshing) { Log("刷新进行中，请稍候…"); return; }
        _refreshing = true;

        try
        {
            // 「刷新数值」= 用户主动要求重新定位存档，清掉缓存强制重扫
            SaveLocator.Invalidate();

            var adapter = _adapter;

            Dictionary<string, AdapterRunner.ReadResult> results;
            try
            {
                results = await Task.Run(() => AdapterRunner.ReadAll(adapter));
            }
            catch (Exception ex)
            {
                Log("[错误] 读取失败：" + ex.Message.Replace("\n", " "));
                SetGameStatePill(GameState.ReadFailed);
                _gameView.SetEmpty("读取失败", "点「刷新数值」重试，或确认游戏已启动");
                return;
            }

            // 全失败时清缓存重试一次：切游戏/读档/重启都会让上次的定位失效
            if (results.Count > 0 && results.Values.All(x => !x.Ok))
            {
                Log("[重试] 全部读取失败，清定位缓存后重试一次…");
                SaveLocator.Invalidate();
                results = await Task.Run(() => AdapterRunner.ReadAll(adapter));
            }

            int ok = results.Values.Count(x => x.Ok);
            int fail = results.Count - ok;

            if (ok == 0)
            {
                // 区分「游戏没开」与「真的读不到」——前者是中性状态，不该报红
                bool notRunning = results.Values.Any(x =>
                    x.Error != null && x.Error.Contains("找不到进程"));

                if (notRunning)
                {
                    Log("[提示] 游戏未启动。先启动游戏，再点「刷新数值」。");
                    SetGameStatePill(GameState.NotRunning);
                    _gameView.SetEmpty("游戏未运行", "先启动游戏，再点「刷新数值」读取存档");
                }
                else
                {
                    string first = results.Values.Select(x => x.Error).FirstOrDefault(e => e != null);
                    Log("[错误] " + (first ?? "读取失败"));
                    SetGameStatePill(GameState.ReadFailed);
                    _gameView.SetEmpty("读取失败", ShortenError(first));
                }
                return;
            }

            // 成功（含部分成功）：交给视图
            SetGameStatePill(GameState.Running);
            _gameView.SetData(adapter, results);

            if (fail == 0) Log($"已刷新数值：成功 {ok}");
            else Log($"已刷新数值：成功 {ok}，{fail} 项不在当前场景");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private readonly HashSet<string> _writing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 写入单个语义。必须走后台线程：CDP 是同步阻塞 IO（超时 30 秒），
    /// 在 UI 线程执行会让窗口冻结——游戏卡顿时点任何卡片都假死。
    /// </summary>
    /// <summary>GameView 提交单值写入。</summary>
    private async Task SubmitValueAsync(string semantic, string raw)
    {
        if (_adapter == null) return;

        var r = await Task.Run(() => AdapterRunner.Write(_adapter, semantic, raw, dryRun: false));
        if (r == null) return;

        if (r.Ok) Log($"{Semantics.Display(semantic)}: {r.Before} → {r.After}");
        else Log("[错误] " + r.Error);

        // 写成功后重读，让界面显示实际值而不是猜测值
        if (r.Ok) await RefreshValuesAsync();
    }

    /// <summary>GameView 提交集合条目写入。</summary>
    private async Task SubmitEntryAsync(string semantic, int entryId, string raw)
    {
        if (_adapter == null) return;

        var r = await Task.Run(() =>
            AdapterRunner.Write(_adapter, semantic, $"{entryId}={raw}", dryRun: false));
        if (r == null) return;

        if (r.Ok) Log($"{Semantics.Display(semantic)}：{r.Before} → {r.After}");
        else Log("[错误] " + r.Error);

        if (r.Ok) await RefreshValuesAsync();
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

        await SubmitValueAsync(semantic, target);
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
        // 只有"已注册"算真正启用；PathStale（路径失效）和 Error 都应显示为未启用，
        // 否则用户以为自启可用，实际重启后不会运行。
        _statusBar?.SetStartup(info.Status == StartupRegistration.Status.Registered);
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
