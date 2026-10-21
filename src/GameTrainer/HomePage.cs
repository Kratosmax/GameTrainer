using GameTrainer.Adapter;
using GameTrainer.UI;

namespace GameTrainer;

/// <summary>
/// 首页：游戏网格。参考 DisplayControlLab v3 的布局语言——
/// 顶部标题 + 副标题 + 右侧主操作，主体是等宽卡片网格。
///
/// 与旧版左侧栏列表的区别：
///   旧版启动时把所有适配包都加载进侧栏，没有"安装/未安装"概念。
///   现在分两段：已安装（本地 adapters/）+ 可下载（远端清单），
///   点未安装的卡片才触发下载，下载完成后才加载该适配包。
///
/// 图标来源：适配包声明的 exe 在磁盘上能找到时，取该 exe 的关联图标；
/// 找不到（游戏没装在这台机器上）就用名字首字生成占位块。
/// </summary>
internal sealed class HomePage : Panel
{
    /// <summary>用户选中某个已安装的游戏，请求进入其修改页。</summary>
    public event Action<GameAdapter> GameSelected;

    /// <summary>用户请求下载某个未安装的适配包。</summary>
    public event Action<AdapterRepository.RemoteAdapter> DownloadRequested;

    /// <summary>用户请求卸载某个已安装的适配包。</summary>
    public event Action<string> UninstallRequested;

    private readonly FlowLayoutPanel _grid = new();
    private readonly Label _installedTitle = new();
    private readonly Label _remoteTitle = new();
    private readonly Label _remoteHint = new();
    private readonly Panel _emptyState = new();

    public HomePage()
    {
        BackColor = Tokens.PageBack;
        Padding = new Padding(0);

        var header = BuildHeader();

        _grid.Dock = DockStyle.Fill;
        _grid.FlowDirection = FlowDirection.LeftToRight;
        _grid.WrapContents = true;
        _grid.AutoScroll = true;
        _grid.BackColor = Tokens.PageBack;
        _grid.Padding = new Padding(Tokens.S5, Tokens.S3, Tokens.S5, Tokens.S5);

        _emptyState.Dock = DockStyle.Fill;
        _emptyState.BackColor = Tokens.PageBack;
        _emptyState.Visible = false;
        var emptyLabel = new Label
        {
            Text = "还没有安装任何适配包。\n连上网络后这里会列出可下载的游戏。",
            Font = Tokens.FontBody,
            ForeColor = Tokens.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _emptyState.Controls.Add(emptyLabel);

        Controls.Add(_grid);
        Controls.Add(_emptyState);
        Controls.Add(header);

        _grid.SizeChanged += (_, _) => RearrangeCards();
    }

    private Control BuildHeader()
    {
        var head = new Panel
        {
            Dock = DockStyle.Top,
            Height = 96,
            BackColor = Tokens.PageBack,
            Padding = new Padding(Tokens.S5, Tokens.S4, Tokens.S5, 0)
        };

        var title = new Label
        {
            Text = "游戏库",
            Font = Tokens.FontTitle,
            ForeColor = Tokens.TextPrimary,
            AutoSize = true,
            Location = new Point(Tokens.S5, Tokens.S4)
        };

        var sub = new Label
        {
            Text = "选择一个游戏开始修改。未安装的适配包可以点卡片下载。",
            Font = Tokens.FontCaption,
            ForeColor = Tokens.TextMuted,
            AutoSize = true,
            Location = new Point(Tokens.S5, Tokens.S4 + 40)
        };

        head.Controls.Add(title);
        head.Controls.Add(sub);
        return head;
    }

    /// <summary>
    /// 填充首页内容。
    /// </summary>
    /// <param name="installed">本地已安装的适配包。</param>
    /// <param name="remote">远端可下载的适配包；为 null 表示拉取失败或离线。</param>
    /// <param name="remoteError">远端失败原因，用于在界面上说明。</param>
    public void Populate(
        List<GameAdapter> installed,
        List<AdapterRepository.RemoteAdapter> remote,
        string remoteError)
    {
        _grid.SuspendLayout();
        try
        {
            _grid.Controls.Clear();

            if (installed.Count > 0)
            {
                _grid.Controls.Add(BuildSectionLabel($"已安装（{installed.Count}）"));
                foreach (var a in installed)
                    _grid.Controls.Add(BuildInstalledCard(a));
            }

            var installedIds = installed.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var downloadable = remote?
                .Where(r => !installedIds.Contains(r.Id))
                .ToList() ?? new List<AdapterRepository.RemoteAdapter>();

            if (downloadable.Count > 0)
            {
                _grid.Controls.Add(BuildSectionLabel("可下载"));
                foreach (var r in downloadable)
                    _grid.Controls.Add(BuildRemoteCard(r));
            }
            else if (!string.IsNullOrEmpty(remoteError))
            {
                _grid.Controls.Add(BuildSectionLabel("可下载"));
                var hint = new Label
                {
                    Text = "无法获取下载列表：" + remoteError,
                    Font = Tokens.FontCaption,
                    ForeColor = Tokens.Warning,
                    AutoSize = false,
                    Width = 620,
                    Height = 32,
                    Margin = new Padding(Tokens.S2, 0, 0, Tokens.S3)
                };
                _grid.Controls.Add(hint);
            }

            _emptyState.Visible = installed.Count == 0 && downloadable.Count == 0;
        }
        finally
        {
            _grid.ResumeLayout(true);
        }

        // Populate 之后主动排一次：SizeChanged 在首次布局前可能已经触发过，
        // 那时 _grid 里还没有卡片，宽度调整会落空。
        BeginInvoke(new Action(RearrangeCards));
    }

    private static Label BuildSectionLabel(string text) => new()
    {
        Text = text,
        Font = Tokens.FontBodyStrong,
        ForeColor = Tokens.TextSecondary,
        AutoSize = false,
        Width = 1200,
        Height = 30,
        Margin = new Padding(Tokens.S2, Tokens.S3, 0, Tokens.S1),
        TextAlign = ContentAlignment.BottomLeft
    };

    /// <summary>已安装的卡片：显示图标 + 名称 + 引擎 + 数值数量，可点击进入。</summary>
    private Control BuildInstalledCard(GameAdapter a)
    {
        var card = new GameCard
        {
            AdapterName = a.Name,
            Subtitle = $"{a.Engine} · {a.Bindings.Count} 个数值",
            IconImage = TryLoadGameIcon(a),
            PlaceholderText = FirstChar(a.Name),
            Installed = true
        };
        card.Clicked += () => GameSelected?.Invoke(a);
        card.UninstallClicked += () => UninstallRequested?.Invoke(a.Id);
        return card;
    }

    /// <summary>未安装的卡片：只显示名称与下载提示，点击触发下载。</summary>
    private Control BuildRemoteCard(AdapterRepository.RemoteAdapter r)
    {
        var card = new GameCard
        {
            AdapterName = string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name,
            Subtitle = "未安装 · 点击下载",
            IconImage = null,
            PlaceholderText = FirstChar(string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name),
            Installed = false
        };
        card.Clicked += () => DownloadRequested?.Invoke(r);
        return card;
    }

    private static string FirstChar(string s)
        => string.IsNullOrWhiteSpace(s) ? "?" : s.Trim()[..1].ToUpperInvariant();

    /// <summary>
    /// 尝试取游戏 exe 的关联图标。
    ///
    /// 找不到时返回 null，由卡片用名字首字占位——
    /// 大多数情况下游戏没装在这台机器上，或者 installDir 还没配置。
    /// </summary>
    private static Image TryLoadGameIcon(GameAdapter a)
    {
        try
        {
            string exe = GameLauncher.ResolveExePath(a);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;

            using var ico = Icon.ExtractAssociatedIcon(exe);
            return ico?.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按容器宽度决定每行放几个卡片。</summary>
    private void RearrangeCards()
    {
        int available = _grid.ClientSize.Width - _grid.Padding.Horizontal;
        if (available < 200) return;

        // 卡片自己撑满所在列：按可用宽度均分，不留固定宽度造成的空隙。
        // 至少保证 GameCard.CardWidth 的可用宽度，避免文字被裁。
        int perRow = Math.Max(1, available / (GameCard.CardWidth + Tokens.S3));
        if (perRow > 6) perRow = 6;
        GameCard.CardsPerRow = perRow;

        int cardW = Math.Max(GameCard.CardWidth, (available / perRow) - Tokens.S4);
        foreach (Control c in _grid.Controls)
        {
            if (c is GameCard gc) gc.Width = cardW;
            else if (c is Label lbl && lbl.Height == 30) lbl.Width = available;
        }
    }
}
