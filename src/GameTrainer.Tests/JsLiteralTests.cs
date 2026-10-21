using GameTrainer;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// JsLiteral 共享判定规则的回归测试。
///
/// 背景：jsVal 裸插值的修复曾只落在 AdapterRunner（sem 路径），
/// Program.cs 的老 profile 命令（ApplyInstanceVar / jsapply storage 分支 / jsset）
/// 漏了同样的修复——jsset 9222 db key "1); evil((" 依然能注入任意 JS。
/// 提取共享方法后所有插值点走同一条判定，这里锁住判定规则本身。
/// </summary>
public class JsLiteralTests
{
    [Theory]
    [InlineData("20")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("9999.5")]
    [InlineData("1e3")]
    [InlineData("-0.125")]
    public void 数字类型_合法数字可插入(string raw)
    {
        Assert.True(JsLiteral.TryPrepare(raw, "number", out string jsVal));
        Assert.DoesNotContain("(", jsVal);
        Assert.DoesNotContain(")", jsVal);
        Assert.DoesNotContain(";", jsVal);
        Assert.DoesNotContain("'", jsVal);
        Assert.DoesNotContain("\"", jsVal);
    }

    [Theory]
    [InlineData("1); evil((")]
    [InlineData("1;process.exit()")]
    [InlineData("alert(1)")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("20;")]
    [InlineData("1 2")]
    public void 数字类型_非数字必须拒绝(string raw)
    {
        Assert.False(JsLiteral.TryPrepare(raw, "number", out _),
            $"「{raw}」被当成合法数字放行，存在注入风险");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void 数字类型_非有限浮点必须拒绝(string raw)
    {
        // double.TryParse 对这三个返回 true，但它们不是合法数值状态
        Assert.False(JsLiteral.TryPrepare(raw, "number", out _));
    }

    [Fact]
    public void 字符串类型_走JSON序列化带引号()
    {
        Assert.True(JsLiteral.TryPrepare("potion", "string", out string jsVal));
        Assert.Equal("\"potion\"", jsVal);
    }

    [Fact]
    public void 字符串类型_引号无法逃逸()
    {
        string payload = "a\"; evil(); \"b";
        Assert.True(JsLiteral.TryPrepare(payload, "string", out string jsVal));

        // 安全性判据：产物必须是良构 JSON 字符串字面量——能被解析回原值。
        // 只要往返成立，jsVal 在 JS 里就是一个完整闭合的字符串常量，
        // payload 里的引号全部处于转义态，不可能拼出可执行结构。
        using var doc = System.Text.Json.JsonDocument.Parse(jsVal);
        Assert.Equal(System.Text.Json.JsonValueKind.String, doc.RootElement.ValueKind);
        Assert.Equal(payload, doc.RootElement.GetString());

        // 首尾是字符串定界符，且内部任何裸引号都已被转义掉
        Assert.StartsWith("\"", jsVal);
        Assert.EndsWith("\"", jsVal);
    }

    [Fact]
    public void 字符串类型_null值转空串()
    {
        Assert.True(JsLiteral.TryPrepare(null, "string", out string jsVal));
        Assert.Equal("\"\"", jsVal);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]   // 溢出成正无穷
    public void 非有限浮点_拒绝(string raw)
    {
        // 这类值插进脚本不构成注入（是合法 JS 字面量），
        // 但会写坏游戏变量——本会话早期实测过 orEver 变成 NaN，
        // 界面显示异常且难以恢复。
        Assert.False(JsLiteral.TryPrepare(raw, "number", out _),
            $"「{raw}」被当成合法数值放行，会污染游戏变量");
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("1.5.5")]
    [InlineData("")]
    public void 区域相关的畸形数字_拒绝(string raw)
    {
        // 必须用 InvariantCulture 判定：某些区域把逗号当小数点
        Assert.False(JsLiteral.TryPrepare(raw, "number", out _));
    }
}