using GameTrainer.Adapter;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 语义匹配。
///
/// 这是自动探测的准确度来源：变量名认错，绑定就会指向错误的字段，
/// 后续读写全错。所以精确名、大小写、包含关系、误匹配都要锁住。
/// </summary>
public class SemanticsTests
{
    [Theory]
    [InlineData("vie", Semantics.Health)]
    [InlineData("hp", Semantics.Health)]
    [InlineData("health", Semantics.Health)]
    [InlineData("viemax", Semantics.HealthMax)]
    [InlineData("maxhp", Semantics.HealthMax)]
    [InlineData("or", Semantics.Gold)]
    [InlineData("gold", Semantics.Gold)]
    [InlineData("capacite", Semantics.Energy)]
    [InlineData("pouvoir_force", Semantics.Attack)]
    [InlineData("pouvoir_magie", Semantics.Magic)]
    [InlineData("bouclieractif", Semantics.Defense)]
    [InlineData("inventairemax", Semantics.Capacity)]
    public void 精确别名_置信度为1(string varName, string expected)
    {
        var (semantic, score) = Semantics.Guess(varName);
        Assert.Equal(expected, semantic);
        Assert.Equal(1.0, score);
    }

    [Theory]
    [InlineData("VIE")]
    [InlineData("Vie")]
    [InlineData("HP")]
    [InlineData("Gold")]
    public void 大小写不敏感(string varName)
    {
        var (semantic, score) = Semantics.Guess(varName);
        Assert.NotNull(semantic);
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void 包含关系_置信度0点7()
    {
        // "player_health" 包含 "health"
        var (semantic, score) = Semantics.Guess("player_health");
        Assert.Equal(Semantics.Health, semantic);
        Assert.Equal(0.7, score);
    }

    [Fact]
    public void 无匹配_返回null与0()
    {
        var (semantic, score) = Semantics.Guess("xyzzy_unknown_var");
        Assert.Null(semantic);
        Assert.Equal(0.0, score);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空输入_返回null与0(string input)
    {
        var (semantic, score) = Semantics.Guess(input);
        Assert.Null(semantic);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void 未知语义_Display原样返回()
    {
        Assert.Equal("my_custom", Semantics.Display("my_custom"));
    }

    [Theory]
    [InlineData(Semantics.Health, "生命值")]
    [InlineData(Semantics.Gold, "金币")]
    [InlineData(Semantics.Attack, "攻击")]
    [InlineData(Semantics.Capacity, "容量")]
    public void 已知语义_Display返回中文名(string semantic, string expected)
    {
        Assert.Equal(expected, Semantics.Display(semantic));
    }

    [Fact]
    public void 全部内置语义都有中文名与别名()
    {
        Assert.Equal(12, Semantics.All.Count);
        foreach (var (key, info) in Semantics.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(info.DisplayName));
            Assert.NotEmpty(info.Aliases);
            Assert.True(Semantics.IsKnown(key));
        }
    }

    [Fact]
    public void 别名不重复_避免匹配歧义()
    {
        // 同一个别名不能属于两个语义，否则 Guess 的结果取决于字典遍历顺序
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (semantic, info) in Semantics.All)
        {
            foreach (string alias in info.Aliases)
            {
                if (seen.TryGetValue(alias, out string existing))
                    Assert.Fail($"别名 \"{alias}\" 同时属于 {existing} 和 {semantic}");
                seen[alias] = semantic;
            }
        }
    }

    [Fact]
    public void 长变量名优先精确匹配而非包含()
    {
        // "viemax" 精确等于 HealthMax 的别名，不该因为包含 "vie" 被判成 Health
        var (semantic, score) = Semantics.Guess("viemax");
        Assert.Equal(Semantics.HealthMax, semantic);
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void 短于三字符不做反向包含判断()
    {
        // 反向包含（alias 包含 v）要求 v.Length >= 3，
        // 否则 "or" 这类短名会误匹配到 "armor" 等别名
        var (semantic, _) = Semantics.Guess("ar");
        Assert.Null(semantic);
    }
}
