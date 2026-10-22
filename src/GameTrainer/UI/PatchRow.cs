using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameTrainer.UI;

/// <summary>
/// 一个修改项行：左侧标题 + 类型说明，右侧开关与写入范围。
/// 行高固定，动态状态（如「已启用」）只改颜色与副标题，不改变行高。
/// </summary>
internal sealed class PatchRow : Control
{
    private bool _hover;
    private StateKind _state = StateKind.Idle;

    public enum StateKind
    {
        /// <summary>未附加进程 / 不可操作。</summary>
        Idle,
        /// <summary>已附加，可启用。</summary>
        Ready,
        /// <summary>修改已生效。</summary>
        Active,
        /// <summary>操作失败或被闸门阻断。</summary>
        Blocked
    }

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>右侧的写入范围/字节数等固定信息。</summary>
    public string Meta { get; set; } = "";

    public PatchRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        Height = Tokens.RowHeight;
        BackColor = Color.Transparent;
        Font = Tokens.FontBody;
    }

    public StateKind State
    {
        get => _state;
        set { if (_state != value) { _state = value; Invalidate(); } }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.Surface);

        if (_hover) 
        {
            using var hb = new SolidBrush(Tokens.RowHover);
            using var hp = Draw.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Tokens.ControlRadius);
            g.FillPath(hb, hp);
        }

        const int left = Tokens.S3;

        // 右侧开关占位按实际子控件算，不写死常量：
        // 开关宽度和右边距改了，文字区必须跟着让位，否则会压到开关下面。
        int switchSpace = 62;
        foreach (Control c in Controls)
        {
            if (c is ToggleSwitch)
            {
                switchSpace = c.Width + c.Margin.Horizontal + Tokens.S3;
                break;
            }
        }

        // 状态圆点：直接把「是否生效」编码为颜色，不靠文字推断
        Color dotColor = _state switch
        {
            StateKind.Active => Tokens.Success,
            StateKind.Blocked => Tokens.Danger,
            StateKind.Ready => Tokens.Accent,
            _ => Tokens.TextDisabled
        };
        var dot = new Rectangle(left, (Height - 8) / 2, 8, 8);
        using (var db = new SolidBrush(dotColor))
            g.FillEllipse(db, dot);

        int textLeft = dot.Right + Tokens.S3;
        int textWidth = Math.Max(60, Width - textLeft - switchSpace);

        var titleRect = new Rectangle(textLeft, 9, textWidth, 20);
        Draw.Text(g, Title, Tokens.FontBodyStrong, Tokens.TextPrimary, titleRect);

        var subRect = new Rectangle(textLeft, 29, textWidth, 18);
        Draw.Text(g, Subtitle, Tokens.FontCaption,
            _state == StateKind.Blocked ? Tokens.Danger : Tokens.TextSecondary, subRect);

        // 行底分隔线：只画到右边留白前，避免和开关挤在一起
        using (var pen = new Pen(Tokens.Border, 1f))
            g.DrawLine(pen, Tokens.S3, Height - 1, Width - Tokens.S3, Height - 1);
    }
}

/// <summary>
/// 运行日志视图。等宽字体、行号感、自动滚到底。
/// 用 RichTextBox 是为了保留可选择/复制能力（排障必须能复制报错原文）。
/// </summary>
internal sealed class LogView : RichTextBox
{
    public LogView()
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        BackColor = Tokens.SurfaceSunken;
        ForeColor = Tokens.TextPrimary;
        Font = Tokens.FontMono;
        WordWrap = false;
        DetectUrls = false;
        ScrollBars = RichTextBoxScrollBars.Both;
        ShortcutsEnabled = true;   // 允许 Ctrl+A / Ctrl+C
        HideSelection = false;
    }

    /// <summary>追加一行并滚动到底。超过 2000 行时裁剪前半，防止长期运行时吃内存。</summary>
    public void Append(string line)
    {
        AppendText(line + Environment.NewLine);
        if (Lines.Length > 2000)
        {
            int cut = Text.IndexOf('\n');
            if (cut > 0) Select(0, cut + 1);
            SelectedText = "";
            Select(TextLength, 0);
        }
        SelectionStart = TextLength;
        ScrollToCaret();
    }
}

/// <summary>空状态占位：居中图标感圆点 + 说明文字，不留纯空白。</summary>
internal sealed class EmptyState : Control
{
    public string Heading { get; set; } = "";
    public string Detail { get; set; } = "";

    public EmptyState()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        // 不能用 Transparent：父级 CardPanel 的 BackColor 也是 Transparent，
        // 透明链一路解析成黑色，实测会得到整块黑底。这里显式用卡片白底。
        BackColor = Tokens.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.Surface);

        int cy = Height / 2;

        // 一个克制的圆环，不做插画
        using (var pen = new Pen(Tokens.Border, 1.5f))
            g.DrawEllipse(pen, Width / 2 - 14, cy - 52, 28, 28);
        using (var b = new SolidBrush(Tokens.Border))
            g.FillEllipse(b, Width / 2 - 5, cy - 43, 10, 10);

        var headRect = new Rectangle(0, cy - 6, Width, 24);
        Draw.TextGdi(g, Heading, Tokens.FontBodyStrong, Tokens.TextSecondary, headRect, HorizontalAlignment.Center);

        if (!string.IsNullOrEmpty(Detail))
        {
            var detRect = new Rectangle(Tokens.S5, cy + 20, Width - Tokens.S5 * 2, 44);
            Draw.TextGdi(g, Detail, Tokens.FontCaption, Tokens.TextMuted, detRect, HorizontalAlignment.Center);
        }
    }
}
