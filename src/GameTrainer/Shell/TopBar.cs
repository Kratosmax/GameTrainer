using GameTrainer.UI;

namespace GameTrainer.Shell;

/// <summary>
/// 顶栏 —— 产品标识 + 操作按钮组。
///
/// 从 GuiForm 抽出来（原 BuildTopBar，125 行 + 5 个按钮字段散在主窗体里被引用 61 次）。
/// 对外只暴露事件与几个状态开关，主窗体不再直接持有按钮控件。
///
/// 布局：品牌 | 弹性留白 | 操作组
/// 操作组顺序：更新红点 → 返回 → 启动 → 刷新 → 设置
/// </summary>
internal sealed class TopBar : Panel
{
    private readonly FlatButton _launch = new();
    private readonly FlatButton _refresh = new();
    private readonly FlatButton _settings = new();
    private readonly FlatButton _back = new();
    private readonly Label _updateDot = new();

    /// <summary>点「启动游戏」。</summary>
    public event Action LaunchRequested;

    /// <summary>点「刷新数值」。</summary>
    public event Action RefreshRequested;

    /// <summary>点「设置」。</summary>
    public event Action SettingsRequested;

    /// <summary>点「← 游戏库」。</summary>
    public event Action BackRequested;

    /// <summary>点更新提示。</summary>
    public event Action UpdateRequested;

    public TopBar()
    {
        Dock = DockStyle.Fill;
        BackColor = Tokens.Surface;
        Padding = new Padding(Tokens.S5, 0, Tokens.S5, 0);
        Margin = new Padding(0);

        // 底边分隔线：顶栏与内容区之间需要一条弱边界
        Paint += (_, e) =>
        {
            using var pen = new Pen(Tokens.Border, 1f);
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Tokens.Surface,
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 品牌
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // 弹性留白
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 操作组
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(BuildBrand(), 0, 0);
        layout.Controls.Add(BuildActions(), 2, 0);

        Controls.Add(layout);
    }

    private Control BuildBrand()
    {
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
            using (var path = Draw.Round(box, 7))
            using (var b = new SolidBrush(Tokens.Accent))
                g.FillPath(b, path);

            Draw.TextCenter(g, "G", Tokens.FontBodyStrong, Tokens.TextOnAccent, box);

            Draw.TextLeft(g, "GameTrainer", Tokens.FontSection, Tokens.TextPrimary,
                new Rectangle(38, 0, brand.Width - 38, brand.Height));
        };

        return brand;
    }

    private Control BuildActions()
    {
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

        // 更新提示：默认隐藏，发现新版本时才显示
        _updateDot.Text = "";
        _updateDot.Visible = false;
        _updateDot.AutoSize = true;
        _updateDot.Font = Tokens.FontCaption;
        _updateDot.ForeColor = Tokens.Danger;
        _updateDot.Cursor = Cursors.Hand;
        _updateDot.TextAlign = ContentAlignment.MiddleRight;
        _updateDot.Margin = new Padding(0, 0, Tokens.S2, 0);
        _updateDot.Padding = new Padding(0);
        _updateDot.Click += (_, _) => UpdateRequested?.Invoke();

        // 返回游戏库：游戏页是首页的下一级，必须有可见的返回入口
        _back.Text = "← 游戏库";
        _back.AccessibleName = "返回游戏库";
        _back.Kind = ButtonKind.Secondary;
        _back.Width = 96;
        _back.Margin = new Padding(0, 0, Tokens.S2, 0);
        _back.Click += (_, _) => BackRequested?.Invoke();

        // 适配包模式：不需要「附加」，改为「启动游戏 / 刷新数值」
        _launch.Text = "启动游戏";
        _launch.AccessibleName = _launch.Text;
        _launch.Kind = ButtonKind.Primary;
        _launch.Width = 116;
        _launch.Margin = new Padding(0, 0, Tokens.S2, 0);
        _launch.Click += (_, _) => LaunchRequested?.Invoke();

        // 次要样式而非危险红：红色留给破坏性操作，「刷新」是安全操作
        _refresh.Text = "刷新数值";
        _refresh.AccessibleName = _refresh.Text;
        _refresh.Kind = ButtonKind.Secondary;
        _refresh.Width = 104;
        _refresh.Margin = new Padding(0, 0, Tokens.S2, 0);
        _refresh.Enabled = false;
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();

        _settings.Text = "设置";
        _settings.AccessibleName = _settings.Text;
        _settings.Width = 76;
        _settings.Margin = new Padding(0);
        _settings.Click += (_, _) => SettingsRequested?.Invoke();

        actions.Controls.Add(_updateDot);
        actions.Controls.Add(_back);
        actions.Controls.Add(_launch);
        actions.Controls.Add(_refresh);
        actions.Controls.Add(_settings);

        return actions;
    }

    // ---------- 状态控制（主窗体只通过这些方法改顶栏） ----------

    /// <summary>首页隐藏返回按钮，游戏页显示。</summary>
    public void SetBackVisible(bool visible) => _back.Visible = visible;

    /// <summary>选中游戏后「刷新数值」才可用。</summary>
    public void SetRefreshEnabled(bool enabled) => _refresh.Enabled = enabled;

    /// <summary>游戏页「启动游戏」可用；首页禁用（没有游戏可启动）。</summary>
    public void SetLaunchEnabled(bool enabled) => _launch.Enabled = enabled;

    /// <summary>显示更新提示。</summary>
    public void ShowUpdateDot(string version)
    {
        _updateDot.Text = $"有新版本 {version}";
        _updateDot.Visible = true;
    }

    /// <summary>隐藏更新提示。</summary>
    public void HideUpdateDot()
    {
        _updateDot.Visible = false;
        _updateDot.Text = "";
    }
}
