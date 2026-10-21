using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// V3 悬停提示的真实验证。
///
/// 这类"控件行为"不能靠读源码断言——必须真的构造控件、真的走到布局，
/// 再看 ToolTip 是否按预期挂载。之前的反复失误都出在"看着像对的"。
/// </summary>
public class ToolTipAttachmentTests
{
    private static Assembly LoadGameTrainer() => typeof(GameTrainer.UI.Tokens).Assembly;

    /// <summary>用反射创建 ValueCard（构造函数是 internal 的）。</summary>
    private static Control CreateCard(string semantic, string warning)
    {
        Type t = LoadGameTrainer().GetType("GameTrainer.UI.ValueCard", throwOnError: true);
        var ctor = t.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .OrderByDescending(c => c.GetParameters().Length)
                    .First();

        object[] args = ctor.GetParameters().Select(p =>
        {
            if (p.Name.Contains("semantic", StringComparison.OrdinalIgnoreCase)) return (object)semantic;
            if (p.Name.Contains("display", StringComparison.OrdinalIgnoreCase)) return semantic;
            if (p.ParameterType == typeof(string)) return warning;
            if (p.ParameterType == typeof(double[])) return new[] { 0.0, 9999.0 };
            if (p.ParameterType == typeof(double)) return 1.0;
            if (p.ParameterType == typeof(bool)) return false;
            return null;
        }).ToArray();

        return (Control)ctor.Invoke(args);
    }

    private static ToolTip GetTip(Control card)
        => (ToolTip)card.GetType()
            .GetField("_tip", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(card);

    private static Label GetLabel(Control card, string field)
        => (Label)card.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(card);

    [Fact]
    public void 长warning会被挂ToolTip()
    {
        // 用一条超过卡片宽度的 warning —— 这正是 GLM 指出我漏掉的场景
        string longWarning = "测试项：范围 1-6，改大可能界面错位，请谨慎使用并观察是否异常";
        Control card = CreateCard("capacity", longWarning);
        card.Size = new Size(292, 168);
        Application.DoEvents();

        ToolTip tip = GetTip(card);
        Label warning = GetLabel(card, "_warning");

        // 布局完成后 warning 应带文字
        Assert.False(string.IsNullOrEmpty(warning.Text));

        // 关键：ToolTip 必须挂上（截断时才挂）
        string tipText = tip.GetToolTip(warning);
        Assert.False(string.IsNullOrEmpty(tipText),
            $"长 warning 未挂 ToolTip。文本=\"{warning.Text}\" 尺寸={warning.Size}");
        Assert.Equal(longWarning, tipText);
    }

    [Fact]
    public void 短hint不挂ToolTip避免打扰()
    {
        // 短文本不该挂——否则每张卡片悬停都弹提示
        Control card = CreateCard("gold", null);
        card.Size = new Size(292, 168);
        Application.DoEvents();

        ToolTip tip = GetTip(card);
        Label hint = GetLabel(card, "_hint");
        hint.Text = "范围 0–999";

        // 手动触发一次布局让 RefreshToolTips 生效
        card.GetType().GetMethod("Relayout", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(card, null);
        Application.DoEvents();

        string tipText = tip.GetToolTip(hint);
        Assert.True(string.IsNullOrEmpty(tipText),
            $"短文本不该挂 ToolTip，但挂上了：\"{tipText}\"");
    }

    [Fact]
    public void 空warning不挂ToolTip()
    {
        Control card = CreateCard("gold", null);
        card.Size = new Size(292, 168);
        Application.DoEvents();

        ToolTip tip = GetTip(card);
        Label warning = GetLabel(card, "_warning");

        Assert.True(string.IsNullOrEmpty(tip.GetToolTip(warning)));
    }
}
