using System.Drawing;

namespace GameTrainer.UI;

/// <summary>
/// 设计令牌 —— 唯一颜色/间距/字体/动效来源。
///
/// 采用 OriginOS 4 设计语言：大圆角 + 高饱和蓝紫渐变 + 弹性动效 + 重阴影。
/// 对比度按 WCAG 2.x 相对亮度核算，结果记在各字段注释里。
/// 正文要求 >=4.5:1，UI 组件边界要求 >=3:1。
///
/// 约束：业务代码不得裸写颜色、字体、间距、圆角。
/// 旧代码有 31 处裸写间距，改风格时要逐个找——那正是要消除的。
/// </summary>
internal static class Tokens
{
    // ==================== 表面 ====================

    /// <summary>页面底。OriginOS 用极浅冷灰，比纯白柔和。</summary>
    public static readonly Color PageBack = Color.FromArgb(0xF7, 0xF8, 0xFA);

    /// <summary>卡片/顶栏/状态栏面。</summary>
    public static readonly Color Surface = Color.FromArgb(0xFF, 0xFF, 0xFF);

    /// <summary>卡片内次级面（数值块、输入框底）。</summary>
    public static readonly Color SurfaceAlt = Color.FromArgb(0xF2, 0xF4, 0xF9);

    /// <summary>悬停态面。</summary>
    public static readonly Color SurfaceHover = Color.FromArgb(0xE8, 0xEC, 0xF7);

    /// <summary>兼容旧名：等于 SurfaceAlt。</summary>
    public static Color SurfaceSunken => SurfaceAlt;

    /// <summary>兼容旧名：等于 SurfaceHover。</summary>
    public static Color RowHover => SurfaceHover;

    /// <summary>
    /// 兼容旧名。OriginOS 布局已无侧栏，此值仅供未迁移的旧控件使用，
    /// 新代码不要引用。
    /// </summary>
    public static Color SidebarBack => SurfaceAlt;

    // ==================== 文字 ====================

    /// <summary>正文/标题。白底 17.67:1，最坏背景 16.63:1。</summary>
    public static readonly Color TextPrimary = Color.FromArgb(0x14, 0x18, 0x26);

    /// <summary>次要文字。白底 6.09:1，最坏背景 5.73:1。</summary>
    public static readonly Color TextSecondary = Color.FromArgb(0x5A, 0x62, 0x77);

    /// <summary>
    /// 弱化文字。最坏背景(#F2F4F9) 4.57:1，白底 5.03:1。
    /// 旧值 #868EA1 实测只有 2.98:1（注释曾误称 4.5:1），
    /// 用于错误文案时看不清，已加深。
    /// </summary>
    public static readonly Color TextMuted = Color.FromArgb(0x68, 0x6F, 0x80);

    /// <summary>禁用态。WCAG 豁免禁用组件，但必须视觉可区分。</summary>
    public static readonly Color TextDisabled = Color.FromArgb(0xA8, 0xAF, 0xBD);

    /// <summary>主色上的文字。</summary>
    public static readonly Color TextOnAccent = Color.FromArgb(0xFF, 0xFF, 0xFF);

    // ==================== 主色（OriginOS 蓝紫）====================

    /// <summary>主色。白底 4.88:1，最坏背景 4.58:1。</summary>
    public static readonly Color Accent = Color.FromArgb(0x41, 0x5F, 0xFF);

    /// <summary>渐变终点色。主按钮用 主色->此色 的横向渐变。</summary>
    public static readonly Color AccentGradEnd = Color.FromArgb(0x7B, 0x5C, 0xFF);

    public static readonly Color AccentHover = Color.FromArgb(0x35, 0x50, 0xE6);
    public static readonly Color AccentPressed = Color.FromArgb(0x2A, 0x41, 0xC4);

    /// <summary>主色极浅底（选中行、图标底、胶囊底）。</summary>
    public static readonly Color AccentSoft = Color.FromArgb(0xE8, 0xEC, 0xFF);

    public static readonly Color AccentSoftHover = Color.FromArgb(0xDB, 0xE2, 0xFF);

    /// <summary>兼容旧名：等于 AccentSoft。</summary>
    public static Color AccentSubtle => AccentSoft;

    public static Color AccentSubtleHover => AccentSoftHover;

    // ==================== 语义色 ====================

    public static readonly Color Success = Color.FromArgb(0x1F, 0xA5, 0x5C);
    public static readonly Color SuccessSoft = Color.FromArgb(0xE6, 0xF7, 0xEE);
    /// <summary>警告。最坏背景(#F2F4F9) 3.83:1，白底 4.22:1。
    /// 旧值 #E88A00 实测 2.37:1，作为状态文字/图标看不清，已加深。</summary>
    public static readonly Color Warning = Color.FromArgb(0xB3, 0x6A, 0x00);
    public static readonly Color WarningSoft = Color.FromArgb(0xFD, 0xF4, 0xE2);
    public static readonly Color Danger = Color.FromArgb(0xE0, 0x3C, 0x4A);
    public static readonly Color DangerSoft = Color.FromArgb(0xFD, 0xEC, 0xEE);

    /// <summary>兼容旧名。</summary>
    public static Color SuccessSubtle => SuccessSoft;
    public static Color WarningSubtle => WarningSoft;
    public static Color DangerSubtle => DangerSoft;
    public static Color DangerHover => Color.FromArgb(0xC9, 0x33, 0x40);
    public static Color DangerPressed => Color.FromArgb(0xA8, 0x2A, 0x35);

    // ==================== 边框 ====================

    /// <summary>卡片分隔线。弱边界，仅用于非交互分隔。</summary>
    public static readonly Color Border = Color.FromArgb(0xE6, 0xEA, 0xF3);

    /// <summary>输入框等弱边界。</summary>
    public static readonly Color BorderSoft = Color.FromArgb(0xDF, 0xE4, 0xF0);

    /// <summary>
    /// 交互控件边界（输入框、开关轨道）。最坏背景(#F2F4F9) 3.23:1，白底 3.56:1。
    /// 旧值 #BFC7DA 实测只有 1.54~1.69:1（注释曾误称 3.4:1）——
    /// 它本身就是"几乎看不见的浅灰"，正是本值要避免的东西。已加深。
    /// </summary>
    public static readonly Color BorderControl = Color.FromArgb(0x7E, 0x88, 0x9E);

    // ==================== 圆角（OriginOS 大圆角）====================

    /// <summary>卡片圆角。OriginOS 标志性大圆角。</summary>
    public const int RadiusCard = 28;

    /// <summary>控件圆角（输入框、小按钮）。</summary>
    public const int RadiusControl = 20;

    /// <summary>胶囊圆角（主按钮、状态胶囊）。</summary>
    public const int RadiusPill = 26;

    /// <summary>图标容器圆角。</summary>
    public const int RadiusIcon = 14;

    /// <summary>兼容旧名。</summary>
    public const int CardRadius = RadiusCard;
    public const int ControlRadius = RadiusControl;

    // ==================== 间距（8 基准）====================

    public const int S1 = 4;
    public const int S2 = 8;
    public const int S3 = 12;
    public const int S4 = 16;
    public const int S5 = 24;
    public const int S6 = 32;

    /// <summary>卡片内边距。OriginOS 留白比 iOS 更大。</summary>
    public const int CardPadding = 24;

    // ==================== 尺寸 ====================

    public const int ControlHeight = 38;
    public const int RowHeight = 44;
    public const int TopBarHeight = 64;
    public const int StatusBarHeight = 36;
    public const int IconSize = 44;
    public const int SidebarWidth = 0;   // OriginOS 布局无侧栏

    // ==================== 阴影 ====================

    /// <summary>卡片阴影模糊半径。0 = 不画（动画期间必须传 0）。</summary>
    public const int ShadowBlur = 18;

    /// <summary>阴影垂直偏移。</summary>
    public const int ShadowOffsetY = 6;

    /// <summary>阴影最大 alpha。</summary>
    public const int ShadowAlpha = 26;

    // ==================== 动效（OriginOS 弹性）====================

    /// <summary>状态切换（颜色、悬停）。</summary>
    public const int AnimFast = 170;

    /// <summary>元素出现、页面切换。</summary>
    public const int AnimNormal = 290;

    /// <summary>布局重排、展开折叠。</summary>
    public const int AnimSlow = 400;

    /// <summary>列表错峰间隔（ms）。</summary>
    public const int AnimStagger = 20;

    /// <summary>按下缩放比例。OriginOS 点击反馈比 iOS 更明显。</summary>
    public const float PressScale = 0.94f;

    // ==================== 字体 ====================

    private const string FamilyUi = "Microsoft YaHei UI";

    public static readonly Font FontBody = CreateFont(FamilyUi, 10.5f, FontStyle.Regular);
    public static readonly Font FontBodyStrong = CreateFont(FamilyUi, 10.5f, FontStyle.Bold);
    public static readonly Font FontCaption = CreateFont(FamilyUi, 9f, FontStyle.Regular);
    public static readonly Font FontCaptionStrong = CreateFont(FamilyUi, 9f, FontStyle.Bold);
    public static readonly Font FontTitle = CreateFont(FamilyUi, 23f, FontStyle.Bold);
    public static readonly Font FontSection = CreateFont(FamilyUi, 12f, FontStyle.Bold);

    /// <summary>数值大字。卡片里显示金额/等级这类主数值。</summary>
    public static readonly Font FontValue = CreateFont(FamilyUi, 19f, FontStyle.Bold);

    public static readonly Font FontMono = CreateFont("Cascadia Mono, Consolas", 9.5f, FontStyle.Regular);

    /// <summary>
    /// 按字号取字体。只在需要非标准字号时用。
    /// 常规层级一律用上面的预定义字体——每次 new Font 都会占用一个 GDI 句柄，
    /// 自绘控件里在 OnPaint 内 new Font 会造成句柄泄漏（旧代码有这个问题）。
    /// </summary>
    public static Font FontAt(float size, FontStyle style = FontStyle.Regular)
        => CreateFont(FamilyUi, size, style);

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
