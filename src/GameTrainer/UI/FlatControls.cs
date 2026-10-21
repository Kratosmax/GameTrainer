using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameTrainer.UI;

/// <summary>按钮语义。主操作/次要/危险/文字链，四类各有完整五态。</summary>
internal enum ButtonKind
{
    Secondary,
    Primary,
    Danger,
    Ghost
}

/// <summary>
/// 统一按钮。完整实现 默认/悬停/按下/焦点/禁用 五态，
/// 固定高度、文字垂直居中、圆角稳定，带焦点虚线框（键盘可达）。
/// </summary>
internal sealed class FlatButton : Button
{
    private bool _hover, _pressed;

    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;

    /// <summary>
    /// 截图工具专用：强制绘制焦点框。
    /// ShowFocusCues 依赖窗口真实激活状态，DrawToBitmap 离线渲染时恒为 false，
    /// 没有这个开关就无法在无焦点窗口里验证焦点框是否可见。
    /// 生产代码不应设置它。
    /// </summary>
    public bool ForceFocusCue { get; set; }

    /// <summary>兼容旧调用点。</summary>
    public bool IsPrimary
    {
        get => Kind == ButtonKind.Primary;
        set => Kind = value ? ButtonKind.Primary : ButtonKind.Secondary;
    }

    public bool IsDanger
    {
        get => Kind == ButtonKind.Danger;
        set => Kind = value ? ButtonKind.Danger : ButtonKind.Secondary;
    }

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Tokens.FontBody;
        Height = Tokens.ControlHeight;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        Padding = new Padding(Tokens.S4, 0, Tokens.S4, 0);
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = _pressed = false;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    private Color BackColorFor()
    {
        if (!Enabled) return Tokens.SurfaceSunken;
        return Kind switch
        {
            ButtonKind.Primary => _pressed ? Tokens.AccentPressed : _hover ? Tokens.AccentHover : Tokens.Accent,
            ButtonKind.Danger => _pressed ? Tokens.DangerPressed : _hover ? Tokens.DangerHover : Tokens.Danger,
            ButtonKind.Ghost => _pressed ? Tokens.AccentSubtleHover : _hover ? Tokens.AccentSubtle : Tokens.Surface,
            // 次要按钮：淡底色 + 明确边界，不做纯白（白卡片上会把按钮吃掉）
            _ => _pressed ? Tokens.SurfaceSunken : _hover ? Tokens.RowHover : Tokens.Surface
        };
    }

    private Color ForeColorFor()
    {
        if (!Enabled) return Tokens.TextDisabled;
        return Kind switch
        {
            ButtonKind.Primary or ButtonKind.Danger => Tokens.TextOnAccent,
            ButtonKind.Ghost => _hover || _pressed ? Tokens.AccentPressed : Tokens.Accent,
            _ => Tokens.TextPrimary
        };
    }

    private Color BorderColorFor()
    {
        if (!Enabled) return Tokens.Border;
        return Kind switch
        {
            ButtonKind.Primary => BackColorFor(),
            ButtonKind.Danger => BackColorFor(),
            ButtonKind.Ghost => Tokens.BorderSoft,
            _ => _hover ? Tokens.BorderControl : Tokens.BorderSoft
        };
    }

    /// <summary>
    /// 自绘按钮的 UIA 拿不到 Text，必须显式设置 AccessibleName，
    /// 否则读屏软件与自动化工具看到的都是无名按钮（实测无障碍树 name 为空）。
    /// </summary>
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.PageBack);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color back = BackColorFor();
        Color border = BorderColorFor();

        using var path = Draw.RoundedRect(rect, Tokens.ControlRadius);
        using (var brush = new SolidBrush(back))
            g.FillPath(brush, path);

        using (var pen = new Pen(border, 1f))
            g.DrawPath(pen, path);

        // 焦点框：虚线内缩，不改变外框尺寸，避免焦点态导致布局跳动。
        //
        // ForceFocusCue 是截图工具专用开关：ShowFocusCues 依赖窗口真实激活状态，
        // DrawToBitmap 在后台渲染时恒为 false，否则无法离线验证焦点框是否可见。
        if (Enabled && (ForceFocusCue || (Focused && ShowFocusCues)))
        {
            var focusRect = Rectangle.Inflate(rect, -3, -3);
            using var focusPen = new Pen(Tokens.Accent, 1f) { DashStyle = DashStyle.Dot };
            using var fp = Draw.RoundedRect(focusRect, Math.Max(2, Tokens.ControlRadius - 2));
            g.DrawPath(focusPen, fp);
        }

        Draw.Text(g, Text, Font, ForeColorFor(), rect, HorizontalAlignment.Center);
    }
}

/// <summary>
/// 统一样式开关。CheckBox 语义、滑块外观，三态对比度明确，不随系统主题变色。
/// 轨道关闭态用 BorderControl 保证 ≥3:1，不用几乎看不见的浅灰。
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    private bool _hover;

    /// <summary>截图工具专用：强制绘制焦点框（原因同 FlatButton.ForceFocusCue）。</summary>
    public bool ForceFocusCue { get; set; }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Height = 24;
        Width = 46;
        Cursor = Cursors.Hand;
        Font = Tokens.FontBody;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { _hover = false; Invalidate(); base.OnEnabledChanged(e); }

    /// <summary>焦点变化必须重绘，否则 Tab 移入/移出时焦点框不出现也不消失。</summary>
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.Surface);

        const int trackH = 20, trackW = 40;
        int y = (Height - trackH) / 2;
        var track = new Rectangle(0, y, trackW, trackH);

        Color trackColor =
            !Enabled ? Tokens.TextDisabled :
            Checked ? (_hover ? Tokens.AccentHover : Tokens.Accent) :
            _hover ? Tokens.TextMuted : Tokens.BorderControl;

        using (var path = Draw.RoundedRect(track, trackH / 2))
        using (var b = new SolidBrush(trackColor))
            g.FillPath(b, path);

        int knobD = trackH - 4;
        int knobX = Checked ? trackW - knobD - 2 : 2;
        var knob = new Rectangle(knobX, y + 2, knobD, knobD);

        using (var kb = new SolidBrush(Enabled ? Tokens.Surface : Tokens.PageBack))
            g.FillEllipse(kb, knob);
        using (var kp = new Pen(Enabled ? Tokens.TextDisabled : Tokens.Border, 1f))
            g.DrawEllipse(kp, knob);

        // 焦点框：与 FlatButton 同款虚线内缩，不改变控件外框，避免焦点态引起布局跳动。
        // 没有这段时 Tab 到开关上看不出焦点在哪——而开关控制的是「套盾」这类会改游戏数据的动作。
        if (Enabled && (ForceFocusCue || (Focused && ShowFocusCues)))
        {
            // 用控件自身尺寸而不是 inflate(track)：track 是 40x20 居中放在 46x24 的控件里，
            // 向外扩 3px 会变成 46x26，高度超出控件底部被裁掉。
            // 改为按整个控件内缩 1px 画，保证框完整可见且不越界。
            var focusRect = Rectangle.Inflate(new Rectangle(0, 0, Width - 1, Height - 1), -1, -1);
            using var focusPen = new Pen(Tokens.Accent, 1f) { DashStyle = DashStyle.Dot };
            using var fp = Draw.RoundedRect(focusRect, (trackH / 2) + 1);
            g.DrawPath(focusPen, fp);
        }
    }
}
