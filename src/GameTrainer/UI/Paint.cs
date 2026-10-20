using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameTrainer.UI;

/// <summary>
/// 绘制基础工具。所有自绘控件共用同一套圆角/居中/文本绘制规则，
/// 避免每个控件各自实现一套导致圆角和内边距不一致。
/// </summary>
internal static class Draw
{
    /// <summary>构造圆角矩形路径。radius 为 0 时退化为直角。</summary>
    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }

        int d = radius * 2;
        // 极窄/极扁的矩形必须收缩半径，否则 AddArc 会画出畸形
        if (d > r.Width) d = Math.Max(2, r.Width);
        if (d > r.Height) d = Math.Max(2, r.Height);

        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// 单行文本统一绘制。强制垂直居中 + 尾部省略 + 不用 GDI 内边距，
    /// 这是「文字上下居中且尺寸稳定」的实现保证。
    /// </summary>
    public static void Text(Graphics g, string text, Font font, Color color, Rectangle rect,
        HorizontalAlignment align = HorizontalAlignment.Left, bool ellipsis = true)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                  | TextFormatFlags.SingleLine;
        flags |= align switch
        {
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            HorizontalAlignment.Right => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        if (ellipsis) flags |= TextFormatFlags.EndEllipsis;

        TextRenderer.DrawText(g, text ?? "", font, rect, color, flags);
    }

    /// <summary>多行文本，按给定宽度自动换行，返回实际占用高度。</summary>
    public static int TextWrapped(Graphics g, string text, Font font, Color color, Rectangle rect)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.Top;
        TextRenderer.DrawText(g, text ?? "", font, rect, color, flags);
        return TextRenderer.MeasureText(g, text ?? "", font,
            new Size(rect.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
    }

    /// <summary>测量单行文本宽度，用于弹性列与最小宽度计算。</summary>
    public static Size Measure(string text, Font font)
        => TextRenderer.MeasureText(text ?? "", font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

    /// <summary>卡片：白底 + 弱边界 + 圆角。统一用这个，不要各页自己画。</summary>
    public static void Card(Graphics g, Rectangle r, Color back, Color? border = null, int radius = Tokens.CardRadius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(r, radius);
        using var b = new SolidBrush(back);
        g.FillPath(b, path);
        if (border.HasValue)
        {
            using var pen = new Pen(border.Value, 1f);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// 清除为父容器底色，避免自绘控件残留上一帧。
    ///
    /// 关键：父级 BackColor 可能是 Color.Transparent（自绘 CardPanel 就是），
    /// 直接 g.Clear(Transparent) 会按 ARGB(0,0,0,0) 清成黑色。
    /// 实测因此出现过整块黑底卡片和黑色开关列。这里沿父链找第一个不透明色，
    /// 找不到才用 fallback。
    /// </summary>
    public static void ClearToParent(Control c, Graphics g, Color fallback)
    {
        g.Clear(ResolveBackColor(c, fallback));
    }

    /// <summary>沿父链解析第一个不透明背景色；整条链都透明时返回 fallback。</summary>
    public static Color ResolveBackColor(Control c, Color fallback)
    {
        for (Control p = c?.Parent; p != null; p = p.Parent)
        {
            Color back = p.BackColor;
            if (back.A == 255) return back;
        }
        return fallback;
    }
}
