using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameTrainer.UI;

/// <summary>
/// 侧栏导航。自绘 默认/悬停/选中 三态，选中态用浅蓝底 + 主色文字 + 左侧强调条，
/// 完全按设计令牌绘制，不退回系统 ListBox 样式。
///
/// 关键：SetItems 内部会把选中项置 0，若只依赖 SelectedIndexChanged，
/// 首屏永远不触发（表现为首页空白）。调用方必须在窗口 Shown 后主动渲染一次。
/// </summary>
internal sealed class NavList : Control
{
    private readonly List<NavItem> _items = new();
    private int _selected = -1;
    private int _hover = -1;

    public event EventHandler SelectedIndexChanged;

    public NavList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Tokens.SidebarBack;
        Font = Tokens.FontBody;
        Cursor = Cursors.Hand;
        ItemHeight = 38;
        SectionTop = Tokens.S2;
    }

    public int ItemHeight { get; set; }

    /// <summary>分组标题占用的顶部高度。设为 0 则不显示分组标题。</summary>
    public int SectionTop { get; set; }

    public string SectionTitle { get; set; } = "";

    public void SetItems(params string[] items)
    {
        _items.Clear();
        foreach (string s in items) _items.Add(new NavItem(s));
        if (_selected < 0 && _items.Count > 0) _selected = 0;
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int ItemCount => _items.Count;

    /// <summary>导航项的实际纵向起点（跳过分组标题）。</summary>
    private int ItemsTop => SectionTop;

    private int IndexAt(int y)
    {
        int rel = y - ItemsTop;
        if (rel < 0) return -1;
        int idx = rel / ItemHeight;
        return idx < _items.Count ? idx : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = IndexAt(e.Y);
        if (idx != _hover) { _hover = idx; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int idx = IndexAt(e.Y);
        if (idx >= 0) SelectedIndex = idx;
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        if (SectionTop > 0 && !string.IsNullOrEmpty(SectionTitle))
        {
            var secRect = new Rectangle(Tokens.S4, 0, Width - Tokens.S4 * 2, SectionTop);
            Draw.Text(g, SectionTitle, Tokens.FontCaptionStrong, Tokens.TextSecondary, secRect);
        }

        for (int i = 0; i < _items.Count; i++)
        {
            var row = new Rectangle(Tokens.S2, ItemsTop + i * ItemHeight,
                                    Width - Tokens.S2 * 2, ItemHeight);
            bool selected = i == _selected;
            bool hovered = i == _hover && !selected;

            if (selected || hovered)
            {
                using var b = new SolidBrush(selected ? Tokens.AccentSubtle : Tokens.RowHover);
                using var path = Draw.RoundedRect(row, Tokens.ControlRadius);
                g.FillPath(b, path);
            }

            var inner = new Rectangle(row.X + Tokens.S3, row.Y,
                                      row.Width - Tokens.S3 * 2, row.Height);

            if (selected)
            {
                // 左侧强调条：固定 3px 宽，圆角，贴在行左内缘
                var bar = new Rectangle(row.X, row.Y + (row.Height - 18) / 2, 3, 18);
                using var accent = new SolidBrush(Tokens.Accent);
                using var barPath = Draw.RoundedRect(bar, 2);
                g.FillPath(accent, barPath);
            }

            Draw.Text(g, _items[i].Text,
                selected ? Tokens.FontBodyStrong : Tokens.FontBody,
                selected ? Tokens.Accent : Tokens.TextPrimary,
                inner);
        }
    }

    private sealed record NavItem(string Text);
}

/// <summary>
/// 分段标签条（沿用 DisplayControl 顶部 tab 的形态：
/// 选中项浅色胶囊 + 主色文字）。用于主窗口的视图切换。
/// </summary>
internal sealed class SegmentedTabs : Control
{
    private readonly List<string> _items = new();
    private int _selected;
    private int _hover = -1;

    public event EventHandler SelectedIndexChanged;

    public SegmentedTabs()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Tokens.Surface;
        Font = Tokens.FontBody;
        Cursor = Cursors.Hand;
        Height = 38;
    }

    public void SetItems(params string[] items)
    {
        _items.Clear();
        _items.AddRange(items);
        if (_selected >= _items.Count) _selected = 0;
        Invalidate();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>每项宽度。宽度固定，避免文字长短导致标签跳动。</summary>
    private int ItemWidth => _items.Count == 0 ? Width : Width / _items.Count;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int idx = Math.Min(e.X / Math.Max(1, ItemWidth), _items.Count - 1);
        if (idx != _hover) { _hover = idx; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int idx = Math.Min(e.X / Math.Max(1, ItemWidth), _items.Count - 1);
        if (idx >= 0) SelectedIndex = idx;
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        for (int i = 0; i < _items.Count; i++)
        {
            var cell = new Rectangle(i * ItemWidth, 0, ItemWidth, Height);
            var pill = Rectangle.Inflate(cell, -2, -5);
            bool sel = i == _selected;

            if (sel || i == _hover)
            {
                using var b = new SolidBrush(sel ? Tokens.AccentSubtle : Tokens.RowHover);
                using var path = Draw.RoundedRect(pill, Tokens.ControlRadius);
                g.FillPath(b, path);
            }

            Draw.Text(g, _items[i],
                sel ? Tokens.FontBodyStrong : Tokens.FontBody,
                sel ? Tokens.Accent : Tokens.TextSecondary,
                cell, HorizontalAlignment.Center);
        }
    }
}

/// <summary>
/// 卡片容器。白底 + 圆角 + 弱边界，可选标题与右上角动作文字。
/// 所有页面块都用它，保证圆角/内边距/标题基线统一。
/// </summary>
internal sealed class CardPanel : Panel
{
    private string _title = "";
    private string _actionText = "";

    public string Title
    {
        get => _title;
        set { _title = value ?? ""; Invalidate(); }
    }

    public string ActionText
    {
        get => _actionText;
        set { _actionText = value ?? ""; Invalidate(); }
    }

    /// <summary>标题区高度。Title 为空时为 0，不占空间。</summary>
    public int HeaderHeight => string.IsNullOrEmpty(Title) ? 0 : 46;

    /// <summary>
    /// 内容区必须让出标题高度。Dock=Fill 的子控件会落在 DisplayRectangle 内，
    /// 不重写这里的话正文会直接盖住卡片标题（实测踩过）。
    /// </summary>
    public override Rectangle DisplayRectangle
    {
        get
        {
            Rectangle r = base.DisplayRectangle;
            int h = HeaderHeight;
            if (h <= 0) return r;
            return new Rectangle(r.X, r.Y + h, r.Width, Math.Max(0, r.Height - h));
        }
    }

    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.PageBack);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.Card(g, rect, Tokens.Surface, Tokens.Border);

        if (HeaderHeight > 0)
        {
            var titleRect = new Rectangle(Tokens.S5, 0, Width - Tokens.S5 * 2, HeaderHeight);
            Draw.Text(g, Title, Tokens.FontSection, Tokens.TextPrimary, titleRect);

            if (!string.IsNullOrEmpty(ActionText))
            {
                var actRect = new Rectangle(Width - Tokens.S5 - 160, 0, 160, HeaderHeight);
                Draw.Text(g, ActionText, Tokens.FontCaption, Tokens.Accent, actRect, HorizontalAlignment.Right);
            }
        }
    }
}

/// <summary>
/// 统计徽标：小圆角胶囊 + 文字，用于状态标签（已连接/未附加/已阻断）。
/// 前景与底色成对给出，保证对比度。
/// </summary>
internal sealed class Badge : Control
{
    private Color _back = Tokens.AccentSubtle;
    private Color _fore = Tokens.Accent;

    public Badge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        Font = Tokens.FontCaption;
        Height = 24;
        AutoSize = false;
    }

    public void SetStyle(Color back, Color fore, string text)
    {
        _back = back;
        _fore = fore;
        Text = text;
        int w = Draw.Measure(text, Font).Width + Tokens.S3 * 2;
        Width = Math.Max(48, w);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.Surface);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Draw.RoundedRect(rect, Height / 2);
        using var b = new SolidBrush(_back);
        g.FillPath(b, path);

        Draw.Text(g, Text, Font, _fore, rect, HorizontalAlignment.Center);
    }
}
