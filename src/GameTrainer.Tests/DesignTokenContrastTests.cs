using System.Drawing;
using System.Reflection;
using GameTrainer.UI;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 设计令牌的对比度契约。
///
/// 为什么需要这组测试：
/// Tokens 里每个颜色都带注释声称“对白底 X.X:1”，并配了一个 Contrast() 计算函数，
/// 注释还写着“供自检与测试复用”。但**从来没有任何测试调用它**。
///
/// 结果是我手填的数字里有两个是错的，而且错了整整一倍：
///   TextMuted     #868EA1 注释称 4.5:1，实测 3.28:1
///   BorderControl #BFC7DA 注释称 3.4:1，实测 1.69:1
///
/// 更糟的是 BorderControl 正是开关“关闭”态轨道的颜色，代码注释写着
/// “保证 ≥3:1，不用几乎看不见的浅灰”——它本身就是那个几乎看不见的浅灰。
///
/// 所以这组测试的作用不是“验证一次”，而是让**手填假数字以后再也不可能提交**：
/// 数字由 Contrast() 现场计算，低于阈值直接红。
///
/// 边界：本测试只覆盖 WCAG 2.x 相对亮度对比度，
/// 不覆盖色盲可辨识性、不覆盖深色主题、不覆盖半透明叠加后的等效颜色。
/// 半透明场景（Easing.WithAlpha 后绘制）需要真实渲染取样，本测试无法替代。
/// </summary>
public class DesignTokenContrastTests
{
    // WCAG 阈值
    private const double TextMin = 4.5;      // 正文（AA）
    private const double UiComponentMin = 3.0; // 非文本 UI 组件边界（SC 1.4.11）

    /// <summary>所有可能出现的背景表面。取最坏情况判定。</summary>
    private static readonly (string Name, Color Color)[] Surfaces =
    {
        ("Surface", Tokens.Surface),
        ("PageBack", Tokens.PageBack),
        ("SurfaceAlt", Tokens.SurfaceAlt),
    };

    /// <summary>
    /// 取某色在所有背景上的**最低**对比度。
    /// 只测白底是不够的——卡片内的次级面是 SurfaceAlt(#F2F4F9)，
    /// 同一个前景色放在那里对比度会再低一点。
    /// </summary>
    private static double WorstContrast(Color fg)
    {
        double worst = double.MaxValue;
        foreach (var (_, bg) in Surfaces)
            worst = Math.Min(worst, Tokens.Contrast(fg, bg));
        return worst;
    }

    [Theory]
    [InlineData(nameof(Tokens.TextPrimary))]
    [InlineData(nameof(Tokens.TextSecondary))]
    [InlineData(nameof(Tokens.TextMuted))]
    public void 文字色在所有背景上达到正文阈值(string tokenName)
    {
        Color c = GetColor(tokenName);
        double actual = WorstContrast(c);

        Assert.True(actual >= TextMin,
            $"{tokenName} (#{Hex(c)}) 最低对比度 {actual:F2}:1，低于正文要求 {TextMin}:1。" +
            $"该值只适用于装饰或禁用态；用于可读文本必须加深。");
    }

    [Theory]
    [InlineData(nameof(Tokens.BorderControl))]
    public void 交互控件边界达到非文本阈值(string tokenName)
    {
        Color c = GetColor(tokenName);
        double actual = WorstContrast(c);

        Assert.True(actual >= UiComponentMin,
            $"{tokenName} (#{Hex(c)}) 最低对比度 {actual:F2}:1，低于 UI 组件要求 {UiComponentMin}:1。" +
            $"该颜色用于开关轨道、输入框边界等需要可辨识的控件，太浅等于看不见。");
    }

    [Fact]
    public void 文字层级必须保持可区分的梯度()
    {
        // 对比度只保证“看得清”，不保证“层级分明”。
        // 三个文字色如果挤在一起，界面上就分不出主次。
        double primary = Tokens.Contrast(Tokens.TextPrimary, Tokens.Surface);
        double secondary = Tokens.Contrast(Tokens.TextSecondary, Tokens.Surface);
        double muted = Tokens.Contrast(Tokens.TextMuted, Tokens.Surface);

        Assert.True(primary > secondary,
            $"TextPrimary({primary:F2}) 应比 TextSecondary({secondary:F2}) 更强");
        Assert.True(secondary > muted,
            $"TextSecondary({secondary:F2}) 应比 TextMuted({muted:F2}) 更强");
    }

    [Fact]
    public void 语义色在主色面上可辨认()
    {
        // 成功/警告/危险会用在浅色底上的图标与文字，必须能看清。
        foreach (var (name, color) in new[]
        {
            (nameof(Tokens.Success), Tokens.Success),
            (nameof(Tokens.Warning), Tokens.Warning),
            (nameof(Tokens.Danger), Tokens.Danger),
        })
        {
            double onWhite = Tokens.Contrast(color, Tokens.Surface);
            Assert.True(onWhite >= 3.0,
                $"{name} 对白底 {onWhite:F2}:1，低于 3:1，作为状态色会看不清");
        }
    }

    [Fact]
    public void 主色上的文字必须可读()
    {
        // 主按钮是 主色渐变底 + 白字，白字对 Accent 的对比度必须够。
        double onAccent = Tokens.Contrast(Tokens.TextOnAccent, Tokens.Accent);
        Assert.True(onAccent >= 4.5,
            $"TextOnAccent 对 Accent {onAccent:F2}:1，低于 4.5:1，主按钮文字会发虚");
    }

    private static Color GetColor(string fieldName)
    {
        FieldInfo f = typeof(Tokens).GetField(fieldName,
            BindingFlags.Public | BindingFlags.Static);
        Assert.True(f != null, $"Tokens 里找不到字段 {fieldName}");
        return (Color)f.GetValue(null);
    }

    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
}
