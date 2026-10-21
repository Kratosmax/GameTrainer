using GameTrainer.Adapter;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 通用 JS 对象探测的语义判定部分。
///
/// GenericProbe 的 JS 遍历逻辑无法在单元测试里跑（需要真实浏览器），
/// 但「候选名 → 语义」这一步是纯函数，且决定了自动生成的绑定对不对。
/// 这里锁定它对 Electron 游戏常见命名的判定。
/// </summary>
public class GenericProbeSemanticTests
{
    [Theory]
    [InlineData("health", Semantics.Health)]
    [InlineData("hp", Semantics.Health)]
    [InlineData("gold", Semantics.Gold)]
    [InlineData("coins", Semantics.Gold)]
    [InlineData("attack", Semantics.Attack)]
    [InlineData("magic", Semantics.Magic)]
    [InlineData("defense", Semantics.Defense)]
    [InlineData("shield", Semantics.Defense)]
    [InlineData("level", Semantics.Level)]
    [InlineData("energy", Semantics.Energy)]
    [InlineData("stamina", Semantics.Energy)]
    [InlineData("inventory", Semantics.Capacity)]
    public void Electron游戏常见命名_能匹配语义(string name, string expected)
    {
        var (semantic, score) = Semantics.Guess(name);
        Assert.Equal(expected, semantic);
        Assert.True(score >= 0.5, $"「{name}」的置信度 {score} 低于通用探测的收录门槛 0.5");
    }

    [Fact]
    public void 驼峰命名_大小写不敏感匹配()
    {
        // JS 游戏常用驼峰：maxHealth / maxGold
        var (s1, _) = Semantics.Guess("maxHealth");
        Assert.Equal(Semantics.HealthMax, s1);

        var (s2, _) = Semantics.Guess("maxGold");
        Assert.NotNull(s2);
    }

    [Fact]
    public void 带前缀的嵌套属性_靠包含关系匹配()
    {
        // 实际路径里属性名可能是 player_health / entity_gold
        var (s1, sc1) = Semantics.Guess("player_health");
        Assert.Equal(Semantics.Health, s1);
        Assert.Equal(0.7, sc1);

        var (s2, _) = Semantics.Guess("entity_gold");
        Assert.Equal(Semantics.Gold, s2);
    }

    [Theory]
    [InlineData("_debuggingDisabled")]
    [InlineData("_exportTimestamp")]
    [InlineData("_devicePixelRatio")]
    [InlineData("__proto__")]
    [InlineData("constructor")]
    [InlineData("onclick")]
    public void 引擎内部属性_不应被误判为语义(string name)
    {
        // GenericProbe 只收录 score >= 0.5 的候选。
        // 这些是引擎/浏览器内部字段，必须落在门槛之下，否则会污染绑定。
        var (semantic, score) = Semantics.Guess(name);
        Assert.True(score < 0.5, $"「{name}」被判成 {semantic}（score={score}），会污染自动生成的绑定");
    }

    [Fact]
    public void 通用探测的收录门槛_为0点5()
    {
        // 这个门槛值写在 GenericProbe.Probe 里，改动它会影响自动绑定的数量与质量。
        // 用两个边界样本确认门槛位置合理：
        // 精确命中（1.0）必须收录，无匹配（0.0）必须排除。
        var (_, exact) = Semantics.Guess("health");
        Assert.True(exact >= 0.5);

        var (_, none) = Semantics.Guess("xyzzy");
        Assert.True(none < 0.5);
    }
}
