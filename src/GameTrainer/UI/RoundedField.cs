using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameTrainer.UI;

/// <summary>
/// 圆角字段容器。用于承载无边框的 TextBox 等原生控件，
/// 让输入区与整体圆角风格一致，不退回系统直角边框。
///
/// 不用 BorderStyle.FixedSingle 的 TextBox：它的边框无法改成圆角，
/// 也和卡片的柔和边界冲突。
/// </summary>
internal sealed class RoundedField : Panel
{
    private bool _focused;

    public RoundedField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // 输入框用白色，不是下沉灰。
        //
        // 参照 PinNote 的 SettingsPanel：输入框 Background="White"，
        // 而卡片面是 #FAFCFC（近白）、页面底是 #F4F7F8。
        // 也就是说输入框比容器"更亮"，靠亮度差表达"这里可以输入"。
        //
        // 我原来用 SurfaceSunken（比卡片更暗），视觉上是个凹坑，
        // 看着像禁用或占位符。方向做反了。
        BackColor = Tokens.Surface;
        Padding = new Padding(Tokens.S3, 0, Tokens.S3, 0);
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        // 子控件获得焦点时加强边框，给出可见的焦点反馈
        e.Control.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        e.Control.LostFocus += (_, _) => { _focused = false; Invalidate(); };
    }

    /// <summary>
    /// 子控件由容器负责垂直居中。
    ///
    /// 原因：单行 TextBox 默认 AutoSize = true，高度被字体锁死在 ~23px。
    /// 调用方写 Dock = Fill 只能拉宽、拉不高，于是它贴在 38px 高容器的顶部——
    /// 实测输入框内文字上留白 3px、下留白 18px，视觉上"往上偏 15px"。
    ///
    /// 坐标注意：子控件的 (0,0) 是**内边距之后**的客户区原点，
    /// 所以要减掉 Padding.Top 才是视觉居中；否则调用方已有的上下 Padding
    /// 会把居中量再推一次（旧代码正是用 Padding 硬凑，见 PathBox）。
    ///
    /// 子控件自己的 Dock 会被忽略，这是本容器的既定行为。
    /// </summary>
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        foreach (Control c in Controls)
        {
            c.Dock = DockStyle.None;
            c.Left = 0;
            c.Width = Math.Max(0, ClientSize.Width);
            c.Top = Math.Max(0, (Height - c.Height) / 2 - Padding.Top);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.Surface);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Draw.RoundedRect(rect, Tokens.ControlRadius);
        using (var b = new SolidBrush(BackColor))
            g.FillPath(b, path);

        // 焦点态用主色，常态用 BorderControl 保证 ≥3:1 的可见边界
        using (var pen = new Pen(_focused ? Tokens.Accent : Tokens.BorderControl, 1f))
            g.DrawPath(pen, path);
    }
}
