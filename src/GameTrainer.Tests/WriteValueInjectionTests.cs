using System.Globalization;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 写入值的注入防护（回归测试）。
///
/// 修复前：ValueType 不是 "string" 时，命令行参数被**原样拼进 JS**：
///     sem &lt;游戏&gt; write health "1); evil(("
/// 会直接在游戏页面里执行任意脚本。
///
/// 修复后：非 string 类型必须能解析成有限数字，否则拒绝。
/// 这里锁住判定规则本身（数值可插入 / 非数值必须被拒）。
/// </summary>
public class WriteValueInjectionTests
{
    /// <summary>复刻 AdapterRunner 里修复后的判定逻辑。</summary>
    private static (bool Ok, string JsVal) Prepare(string rawValue, string valueType)
    {
        if (valueType == "string")
            return (true, System.Text.Json.JsonSerializer.Serialize(rawValue));

        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
            && !double.IsNaN(num) && !double.IsInfinity(num))
            return (true, num.ToString("R", CultureInfo.InvariantCulture));

        return (false, null);
    }

    [Theory]
    [InlineData("20")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("9999.5")]
    [InlineData("1e3")]
    [InlineData("-0.125")]
    public void 合法数字_可以插入(string raw)
    {
        var (ok, jsVal) = Prepare(raw, "number");
        Assert.True(ok);
        // 插入的必须是纯数字字面量，不能带括号/引号/分号
        Assert.DoesNotContain("(", jsVal);
        Assert.DoesNotContain(")", jsVal);
        Assert.DoesNotContain(";", jsVal);
        Assert.DoesNotContain("'", jsVal);
    }

    [Theory]
    [InlineData("1); evil((")]
    [InlineData("1;process.exit()")]
    [InlineData("alert(1)")]
    [InlineData("1\\u0029")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("20;")]
    [InlineData("1 2")]
    public void 非数字_必须拒绝(string raw)
    {
        var (ok, _) = Prepare(raw, "number");
        Assert.False(ok, $"「{raw}」被当成合法数字放行，存在注入风险");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void 非有限浮点_必须拒绝(string raw)
    {
        // double.TryParse 对这三个返回 true，但它们不是合法数值状态
        var (ok, _) = Prepare(raw, "number");
        Assert.False(ok);
    }

    [Fact]
    public void 字符串类型_走JSON序列化带引号()
    {
        var (ok, jsVal) = Prepare("potion", "string");
        Assert.True(ok);
        Assert.StartsWith("\"", jsVal);
        Assert.EndsWith("\"", jsVal);
    }

    [Fact]
    public void 字符串类型_引号被转义不能逃逸()
    {
        var (ok, jsVal) = Prepare("a\"); evil(); (\"b", "string");
        Assert.True(ok);

        // 注意：System.Text.Json 默认把内层引号转义成 \u0022 而不是 \"。
        // 两种形式在 JS 里都合法且都无法提前闭合字符串，所以不能断言 \"
        // ——必须按"未转义引号只有首尾两个"来判定。
        Assert.Contains("\\u0022", jsVal);

        // 统计未转义的引号数量：应当正好 2 个（首尾）
        int unescaped = 0;
        for (int i = 0; i < jsVal.Length; i++)
        {
            if (jsVal[i] != '"') continue;
            if (i > 0 && jsVal[i - 1] == '\\') continue;
            unescaped++;
        }
        Assert.Equal(2, unescaped);
    }

    [Fact]
    public void 数字解析使用不变文化()
    {
        // 某些区域用逗号作小数点，"1,5" 不能当成 1.5
        var (ok, _) = Prepare("1,5", "number");
        Assert.False(ok);
    }
}
