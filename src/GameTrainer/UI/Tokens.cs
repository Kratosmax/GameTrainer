using System.Drawing;

namespace GameTrainer.UI;

/// <summary>
/// 设计令牌 —— 唯一颜色/间距/字体来源。
/// 业务代码里不允许出现裸颜色常量，全部从这里取。
///
/// 色值来源：DisplayControlLab design-review/v3 截图的像素采样，
/// 不是凭肉眼估的。采样脚本见 temp/probe-colors.py。
///
/// 对比度全部按 WCAG 2.x 相对亮度公式核算，结果记在各字段注释里。
/// 正文要求 ≥4.5:1，UI 组件边界要求 ≥3:1。
/// </summary>
internal static class Tokens
{
    // ---------- 表面 ----------
    /// <summary>内容区底色。参考截图占 24.59%。</summary>
    public static readonly Color PageBack = Color.FromArgb(0xF5, 0xF6, 0xFA);
    /// <summary>侧栏底色。参考截图占 11.51%。</summary>
    public static readonly Color SidebarBack = Color.FromArgb(0xEE, 0xF1, 0xF6);
    /// <summary>卡片/顶栏/状态栏底色。</summary>
    public static readonly Color Surface = Color.FromArgb(0xFF, 0xFF, 0xFF);
    /// <summary>卡片内次级表面（如行内数值块）。</summary>
    public static readonly Color SurfaceSunken = Color.FromArgb(0xF5, 0xF6, 0xFA);
    /// <summary>悬停行的浅底。</summary>
    public static readonly Color RowHover = Color.FromArgb(0xF3, 0xF5, 0xF9);

    // ---------- 文字 ----------
    /// <summary>正文/标题。对白底 15.4:1。</summary>
    public static readonly Color TextPrimary = Color.FromArgb(0x16, 0x20, 0x3A);
    /// <summary>次要文字。对白底 5.79:1（实测采样色 #59667D）。</summary>
    public static readonly Color TextSecondary = Color.FromArgb(0x59, 0x66, 0x7D);
    /// <summary>弱化文字，仅用于可读性要求较低的场景。对白底 4.54:1。</summary>
    public static readonly Color TextMuted = Color.FromArgb(0x6B, 0x77, 0x89);
    /// <summary>
    /// 禁用态文字/轨道。对白底 2.50:1。
    /// WCAG 2.1 1.4.3 / 1.4.11 明确豁免禁用组件，因此不参与 3:1 门槛；
    /// 但它仍必须与正常态在视觉上可区分，且不承载任何必要信息。
    /// </summary>
    public static readonly Color TextDisabled = Color.FromArgb(0x9A, 0xA5, 0xB4);
    public static readonly Color TextOnAccent = Color.FromArgb(0xFF, 0xFF, 0xFF);

    // ---------- 边框 ----------
    /// <summary>卡片/分隔线。弱边界，仅用于非交互分隔。</summary>
    public static readonly Color Border = Color.FromArgb(0xE4, 0xE8, 0xF0);
    /// <summary>输入框等弱边界。</summary>
    public static readonly Color BorderSoft = Color.FromArgb(0xDD, 0xE4, 0xF0);
    /// <summary>
    /// 交互控件边界专用。实测对比度：
    ///   白底 3.58 / 浅灰底 3.31 / 侧栏底 3.16 —— 全部 ≥ 3:1。
    /// 更浅的 #8391A0 在侧栏底只有 2.84，#8A98A4 只有 2.95，都不达标。
    /// </summary>
    public static readonly Color BorderControl = Color.FromArgb(0x7B, 0x89, 0x98);

    // ---------- 主色 ----------
    /// <summary>主色。参考截图采样 #3D5DD8。对白底 5.58:1。</summary>
    public static readonly Color Accent = Color.FromArgb(0x3D, 0x5D, 0xD8);
    public static readonly Color AccentHover = Color.FromArgb(0x33, 0x52, 0xC4);
    public static readonly Color AccentPressed = Color.FromArgb(0x2A, 0x45, 0xAB);
    /// <summary>选中行/标签浅底。主色文字在其上 4.82:1。</summary>
    public static readonly Color AccentSubtle = Color.FromArgb(0xE9, 0xEE, 0xFF);
    public static readonly Color AccentSubtleHover = Color.FromArgb(0xDF, 0xE6, 0xFD);

    // ---------- 语义色 ----------
    public static readonly Color Success = Color.FromArgb(0x1F, 0x7A, 0x4D);
    public static readonly Color SuccessSubtle = Color.FromArgb(0xE8, 0xF4, 0xEE);
    public static readonly Color Warning = Color.FromArgb(0x9A, 0x60, 0x0A);
    public static readonly Color WarningSubtle = Color.FromArgb(0xFD, 0xF4, 0xE3);
    public static readonly Color Danger = Color.FromArgb(0xB3, 0x2D, 0x2D);
    public static readonly Color DangerHover = Color.FromArgb(0xC2, 0x3A, 0x3A);
    public static readonly Color DangerPressed = Color.FromArgb(0x8E, 0x24, 0x24);
    public static readonly Color DangerSubtle = Color.FromArgb(0xFC, 0xEC, 0xEC);

    // ---------- 间距尺度 4/8/12/16/24/32 ----------
    public const int S1 = 4;
    public const int S2 = 8;
    public const int S3 = 12;
    public const int S4 = 16;
    public const int S5 = 24;
    public const int S6 = 32;

    // ---------- 控件尺寸 ----------
    public const int ControlHeight = 34;
    public const int RowHeight = 56;
    public const int TopBarHeight = 56;
    public const int StatusBarHeight = 34;
    public const int SidebarWidth = 212;
    public const int CardRadius = 10;
    public const int ControlRadius = 6;

    // ---------- 字体 ----------
    private const string FamilyUi = "Microsoft YaHei UI";
    private const string FamilyMono = "Cascadia Mono, Consolas";

    public static readonly Font FontBody = CreateFont(FamilyUi, 10f, FontStyle.Regular);
    public static readonly Font FontBodyStrong = CreateFont(FamilyUi, 10f, FontStyle.Bold);
    public static readonly Font FontCaption = CreateFont(FamilyUi, 9f, FontStyle.Regular);
    public static readonly Font FontCaptionStrong = CreateFont(FamilyUi, 9f, FontStyle.Bold);
    /// <summary>页面大标题。</summary>
    public static readonly Font FontTitle = CreateFont(FamilyUi, 16f, FontStyle.Bold);
    /// <summary>卡片内小节标题。</summary>
    public static readonly Font FontSection = CreateFont(FamilyUi, 11.5f, FontStyle.Bold);
    public static readonly Font FontMono = CreateFont(FamilyMono, 9.5f, FontStyle.Regular);

    private static Font CreateFont(string family, float size, FontStyle style)
    {
        try { return new Font(family, size, style); }
        catch { return new Font(FontFamily.GenericSansSerif, size, style); }
    }

    // ---------- 对比度工具（供自检与测试复用） ----------

    /// <summary>WCAG 2.x 相对亮度。</summary>
    public static double RelativeLuminance(Color c)
    {
        static double Ch(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>两色的 WCAG 对比度。</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }
}
