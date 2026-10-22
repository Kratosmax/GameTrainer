using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace GameTrainer.UI;

/// <summary>
/// 绘制原语集合 —— 所有自绘控件共用。
///
/// 为什么要集中：旧代码里每个控件各自写圆角路径、各自算阴影，
/// 同一套圆角逻辑复制了 6 遍（CollectionCard / ValueCard / FlatControls /
/// ProfileList / CardPanel / Paint）。改圆角半径要改 6 个地方。
///
/// 这里只放"没有业务语义的绘制动作"：圆角、阴影、渐变、文字。
/// 带业务含义的（卡片、按钮、胶囊）由各自控件调用这些原语组合。
/// </summary>
internal static class Draw
{
    // ---------- 几何 ----------

    /// <summary>
    /// 圆角矩形路径。radius <= 0 时退化为直角矩形。
    /// 半径自动夹到短边一半，避免小控件上画出畸形圆角。
    /// </summary>
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
        {
            p.AddRectangle(r);
            return p;
        }

        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }

        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>胶囊路径：半径取高度一半，永远端头全圆。</summary>
    public static GraphicsPath Pill(Rectangle r) => Round(r, r.Height / 2);

    // ---------- 阴影 ----------

    /// <summary>
    /// 软阴影。WinForms 没有原生阴影，用多层递减 alpha 的圆角矩形叠加模拟。
    ///
    /// 性能警告：这是自绘界面最容易卡的地方。
    /// 动画进行中必须传 blur = 0 跳过阴影，只在静止终态画一次。
    /// 实测：4 张卡片每帧重画阴影会让帧时间从 16ms 涨到 40ms+。
    /// </summary>
    public static void Shadow(Graphics g, Rectangle r, int radius,
                              int blur = Tokens.ShadowBlur,
                              int offsetY = Tokens.ShadowOffsetY,
                              int maxAlpha = Tokens.ShadowAlpha)
    {
        if (blur <= 0 || maxAlpha <= 0) return;

        int layers = Math.Max(2, blur / 4);
        for (int i = layers; i >= 1; i--)
        {
            int spread = blur * i / layers;
            // alpha 随扩散递减：越外层越淡
            int a = maxAlpha * (layers - i + 1) / (layers * 3);
            if (a <= 0) continue;

            var sr = new Rectangle(
                r.X - spread / 2,
                r.Y + offsetY - spread / 2,
                r.Width + spread,
                r.Height + spread);

            using var sp = Round(sr, radius + spread / 2);
            using var sb = new SolidBrush(Color.FromArgb(Math.Min(a, 48), 0, 0, 0));
            g.FillPath(sb, sp);
        }
    }

    /// <summary>硬阴影（实心偏移块），用于高对比风格。</summary>
    public static void HardShadow(Graphics g, Rectangle r, int radius, int offset)
    {
        if (offset <= 0) return;
        var sr = new Rectangle(r.X + offset, r.Y + offset, r.Width, r.Height);
        using var sp = Round(sr, radius);
        using var sb = new SolidBrush(Color.Black);
        g.FillPath(sb, sp);
    }

    // ---------- 填充 ----------

    /// <summary>纯色填充圆角矩形。</summary>
    public static void Fill(Graphics g, Rectangle r, int radius, Color color)
    {
        using var path = Round(r, radius);
        using var b = new SolidBrush(color);
        g.FillPath(b, path);
    }

    /// <summary>
    /// 主色渐变填充。OriginOS 的按钮和强调块用 主色 → 渐变终点色。
    /// 角度 0 = 从左到右。
    /// </summary>
    public static void FillGradient(Graphics g, Rectangle r, int radius,
                                    Color from, Color to, float angle = 0f)
    {
        if (r.Width <= 0 || r.Height <= 0) return;

        using var path = Round(r, radius);
        using var brush = new LinearGradientBrush(
            new Rectangle(r.X, r.Y, Math.Max(r.Width, 1), Math.Max(r.Height, 1)),
            from, to, angle);
        g.FillPath(brush, path);
    }

    /// <summary>描边圆角矩形。</summary>
    public static void Stroke(Graphics g, Rectangle r, int radius, Color color, float width = 1f)
    {
        using var path = Round(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    // ---------- 文字 ----------

    /// <summary>左对齐、垂直居中、超长省略。</summary>
    public static void TextLeft(Graphics g, string text, Font font, Color color, Rectangle r)
        => Text(g, text, font, color, r, StringAlignment.Near);

    /// <summary>水平居中、垂直居中。</summary>
    public static void TextCenter(Graphics g, string text, Font font, Color color, Rectangle r)
        => Text(g, text, font, color, r, StringAlignment.Center);

    /// <summary>右对齐、垂直居中。</summary>
    public static void TextRight(Graphics g, string text, Font font, Color color, Rectangle r)
        => Text(g, text, font, color, r, StringAlignment.Far);

    /// <summary>
    /// 通用文字绘制。所有文字都走这里，保证：
    ///   - 垂直居中（旧代码有的居中有的偏上，视觉不一致）
    ///   - 超长自动省略号（旧代码有 3 处文字被裁成半个字）
    ///   - ClearType 渲染（默认抗锯齿在小字号下发虚）
    /// </summary>
    public static void Text(Graphics g, string text, Font font, Color color, Rectangle r,
                            StringAlignment align = StringAlignment.Near,
                            bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || r.Width <= 0 || r.Height <= 0) return;

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var brush = new SolidBrush(color);
        using var sf = new StringFormat
        {
            Alignment = align,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        if (wrap)
        {
            sf.FormatFlags = 0;   // 允许换行
            sf.LineAlignment = StringAlignment.Near;
        }
        else
        {
            sf.FormatFlags = StringFormatFlags.NoWrap;
        }

        g.DrawString(text, font, brush, r, sf);
    }

    /// <summary>多行文字，返回实际占用高度。</summary>
    public static int TextWrapped(Graphics g, string text, Font font, Color color, Rectangle r)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var brush = new SolidBrush(color);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisWord
        };

        var measured = g.MeasureString(text, font, r.Width, sf);
        g.DrawString(text, font, brush, r, sf);
        return (int)Math.Ceiling(measured.Height);
    }

    /// <summary>测量单行文字宽度。</summary>
    public static Size Measure(Graphics g, string text, Font font)
        => string.IsNullOrEmpty(text) ? Size.Empty : Size.Ceiling(g.MeasureString(text, font));

    // ---------- 状态 ----------

    /// <summary>
    /// 画一个状态点（圆点 + 可选外发光）。
    /// 用于"运行中/未运行"这类需要一眼看出的状态。
    /// </summary>
    public static void Dot(Graphics g, Point center, int radius, Color color, bool glow = false)
    {
        if (glow)
        {
            using var gb = new SolidBrush(Color.FromArgb(48, color.R, color.G, color.B));
            g.FillEllipse(gb, center.X - radius * 2, center.Y - radius * 2, radius * 4, radius * 4);
        }

        using var b = new SolidBrush(color);
        g.FillEllipse(b, center.X - radius, center.Y - radius, radius * 2, radius * 2);
    }

    // ==================== 兼容旧调用（TextRenderer 版）====================
    //
    // 旧 Paint.cs 用 TextRenderer（GDI），新代码用 DrawString（GDI+）。
    // 两者度量与换行行为不同，已经用 TextRenderer 的控件不能混用，
    // 否则文字会跳位。这里保留一套 TextRenderer 入口，签名与旧版一致，
    // 供尚未迁移的控件使用；新控件一律用上面的 DrawString 版本。

    /// <summary>旧签名：圆角路径。等于 Round。</summary>
    public static GraphicsPath RoundedRect(Rectangle r, int radius) => Round(r, radius);

    /// <summary>
    /// 单行文字（GDI TextRenderer）。强制垂直居中 + 尾部省略。
    ///
    /// 与 Text 分开命名而不是重载：两个重载只差枚举类型，
    /// 调用方传默认参数时编译器无法抉择，会产生二义性错误。
    /// </summary>
    public static void TextGdi(Graphics g, string text, Font font, Color color, Rectangle rect,
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

    /// <summary>
    /// 清除为父容器底色，避免自绘控件残留上一帧。
    ///
    /// 关键：父级 BackColor 可能是 Color.Transparent（自绘 CardPanel 就是），
    /// 直接 g.Clear(Transparent) 会按 ARGB(0,0,0,0) 清成黑色。
    /// 实测因此出现过整块黑底卡片和黑色开关列。
    /// </summary>
    public static void ClearToParent(Control c, Graphics g, Color fallback)
        => g.Clear(ResolveBackColor(c, fallback));

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

    /// <summary>
    /// 旧签名：卡片。保留以兼容现有调用点。
    /// 新代码请用 Fill + Stroke + Shadow 组合，可按状态分别控制。
    /// </summary>
    public static void Card(Graphics g, Rectangle r, Color back, Color? border = null,
                            int radius = Tokens.RadiusCard)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(r, radius);
        using var b = new SolidBrush(back);
        g.FillPath(b, path);
        if (border.HasValue)
        {
            using var pen = new Pen(border.Value, 1f);
            g.DrawPath(pen, path);
        }
    }
}