using GameTrainer.UI;

namespace GameTrainer.Shell;

/// <summary>
/// 设置页的一个分组卡片 —— 圆角白底 + 组标题 + 若干行。
///
/// 抽出来的原因：设置页原来是一条条 Label 直接堆在滚动区里，
/// 没有分组边界，视觉上像一篇文章而不是设置项。
/// OriginOS 的设置页是"分组卡片 + 组内行"，这里对齐那个结构。
///
/// 用法：new SettingsCard("启动")，然后 AddRow(控件)。
/// 高度由内容自动决定，外部只读 RequiredHeight。
/// </summary>
internal sealed class SettingsCard : Control
{
    private readonly List<Control> _rows = new();
    private readonly string _title;

    /// <summary>组标题与第一行之间的间距。</summary>
    private const int TitleGap = 44;

    /// <summary>行与行之间的间距。</summary>
    private const int RowGap = 14;

    /// <summary>卡片之间留出的呼吸空间由外部 SettingsForm 控制。</summary>

    /// <summary>左右内边距（比全局卡片略小，设置项文字多）。</summary>
    private const int PadX = 22;

    /// <summary>上下内边距。</summary>
    private const int PadY = 18;

    public SettingsCard(string title)
    {
        _title = title ?? "";

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Tokens.PageBack;
    }

    /// <summary>追加一行内容。行高由控件自身 PreferredHeight 决定。</summary>
    public void AddRow(Control c)
    {
        if (c == null) return;
        _rows.Add(c);
        Controls.Add(c);
        PerformLayout();
        Invalidate();
    }

    /// <summary>
    /// 按内容算出的总高度。
    ///
    /// 注意：子控件的 Height 在 AddRow 时可能还是默认值（比如 Label 的 23），
    /// 之后调用方才设置真实高度。所以这里每次现算，不能缓存。
    /// </summary>
    public int RequiredHeight
    {
        get
        {
            int h = PadY + TitleGap;
            foreach (var r in _rows) h += RowHeight(r) + RowGap;
            return h + PadY - RowGap;
        }
    }

    private int RowHeight(Control r)
    {
        if (r.Tag is int fixedH && fixedH > 0) return fixedH;

        // 多行文字要按宽度实测，不能只看 Height——
        // Label.AutoSize=false 时 Height 可能被设成 23，实际换行后需要 40+。
        if (r is Label l && !l.AutoSize && l.Text.Length > 0)
        {
            int w = Math.Max(80, Width - PadX * 2);
            int measured = MeasureLabel(l.Text, l.Font, w);
            if (measured > l.Height)
            {
                l.Height = measured;   // 顺手纠正，避免文字被裁
                return measured;
            }
        }

        return r.Height > 0 ? r.Height : Tokens.ControlHeight;
    }

    private static int MeasureLabel(string text, Font font, int width)
    {
        using var probe = new Label { Text = text, Font = font, AutoSize = true };
        int h = probe.GetPreferredSize(new Size(width, 0)).Height;
        return Math.Max(20, h + 4);
    }

    /// <summary>
    /// 按当前宽度重排所有行。
    ///
    /// 两遍：第一遍按宽度实测每行真实高度并写回子控件，
    /// 第二遍才定位。一遍做不完——行高依赖宽度，而宽度刚变时
    /// 子控件的 Height 还是旧值，直接累加会把卡片算矮，内容溢出到卡片外。
    /// </summary>
    public void RelayoutRows()
    {
        int w = Math.Max(120, Width - PadX * 2);

        // 第一遍：定高
        foreach (var r in _rows)
        {
            r.Width = w;
            NormalizeHeight(r, w);
        }

        // 第二遍：定位
        int y = PadY + TitleGap;
        foreach (var r in _rows)
        {
            r.Left = PadX;
            r.Top = y;
            y += r.Height + RowGap;
        }

        Height = y - RowGap + PadY;
    }

    /// <summary>把某行的高度纠正为当前宽度下的真实高度。</summary>
    private static void NormalizeHeight(Control r, int width)
    {
        if (r.Tag is int fixedH && fixedH > 0)
        {
            r.Height = fixedH;
            return;
        }

        // 多行文字按宽度实测；单行控件保持原高
        if (r is Label l && l.Text.Length > 0)
        {
            int measured = MeasureLabel(l.Text, l.Font, width);
            if (measured > l.Height) l.Height = measured;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var bg = new SolidBrush(Tokens.PageBack))
            g.FillRectangle(bg, ClientRectangle);

        // 卡片：OriginOS 大圆角 + 柔和阴影
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.Shadow(g, rc, Tokens.RadiusCard);
        Draw.Fill(g, rc, Tokens.RadiusCard, Tokens.Surface);

        // 组标题
        Draw.TextLeft(g, _title, Tokens.FontSection, Tokens.TextPrimary,
            new Rectangle(PadX, PadY, Width - PadX * 2, 28));
    }

    /// <summary>行内容自己画在卡片之上，所以要让子控件透明背景。</summary>
    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control != null) e.Control.BackColor = Tokens.Surface;
    }
}
