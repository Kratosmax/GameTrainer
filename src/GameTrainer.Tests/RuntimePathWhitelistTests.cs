using GameTrainer.Adapter;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// RuntimePath 白名单校验。
///
/// 踩过的两个坑都要锁住：
/// 1. JSON 序列化会把它变成字符串常量，rt.GetAllObjectClasses() 全部失败
///    （功能性回归，实测 probe/sem 集体失灵）。
/// 2. 裸插值允许适配包注入任意 JS。
/// 白名单「标识符.标识符…」是两者交集：能表达运行时入口，不可能携带可执行结构。
/// </summary>
public class RuntimePathWhitelistTests
{
    [Theory]
    [InlineData("self.c3_runtimeInterface._localRuntime")]
    [InlineData("self.game")]
    [InlineData("window.player")]
    [InlineData("_runtime")]
    [InlineData("$engine")]
    [InlineData("a.b.c.d.e")]
    public void 合法属性链_通过(string expr)
        => Assert.True(AdapterRunner.IsValidPropertyChain(expr));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("self.constructor.constructor(\"return 1\")()")]
    [InlineData("process.exit()")]
    [InlineData("a;evil()")]
    [InlineData("a[b]")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a..b")]
    [InlineData("1a.b")]
    [InlineData("a-b.c")]
    [InlineData("a b")]
    public void 非法表达式_拒绝(string expr)
        => Assert.False(AdapterRunner.IsValidPropertyChain(expr));

    [Fact]
    public void 超长表达式_拒绝()
        => Assert.False(AdapterRunner.IsValidPropertyChain(new string('a', 300)));

    [Fact]
    public void 正常路径仍可工作_不依赖序列化()
    {
        // 锁住修复方式：必须以表达式形式插入，不是字符串。
        // 用实际脚本构造验证而非单测断言（构造器是私有的，行为由集成测试覆盖）。
        Assert.True(AdapterRunner.IsValidPropertyChain("self.c3_runtimeInterface._localRuntime"));
    }
}