using GameTrainer.Core;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 更新偏好与「跳过此版本」的语义。
///
/// 这块容易错的地方：跳过是按版本比较，不是按字符串相等。
/// 用户跳过了 1.0.1，之后出了 1.0.2 必须重新提示。
/// </summary>
public class UpdatePrefsTests
{
    [Fact]
    public void 默认自动检查开启()
    {
        var p = new UpdatePrefs();
        Assert.True(p.AutoCheck);
        Assert.Null(p.SkippedVersion);
    }

    [Fact]
    public void 跳过后同版本不再提示()
    {
        var p = new UpdatePrefs();
        p.Skip(new Version(1, 0, 1));

        Assert.True(p.IsSkipped(new Version(1, 0, 1)));
        Assert.Equal("1.0.1", p.SkippedVersion);
    }

    [Fact]
    public void 跳过后更高版本仍提示()
    {
        var p = new UpdatePrefs();
        p.Skip(new Version(1, 0, 1));

        // 跳过 1.0.1 不该顺带屏蔽 1.0.2
        Assert.False(p.IsSkipped(new Version(1, 0, 2)));
        Assert.False(p.IsSkipped(new Version(1, 1, 0)));
    }

    [Fact]
    public void 跳过后更低版本不提示()
    {
        var p = new UpdatePrefs();
        p.Skip(new Version(1, 5, 0));

        // 跳过的是 1.5.0，遇到 1.3.0 不该提示（已经比跳过的还旧）
        Assert.True(p.IsSkipped(new Version(1, 3, 0)));
    }

    [Fact]
    public void 未跳过时不拦截()
    {
        var p = new UpdatePrefs();
        Assert.False(p.IsSkipped(new Version(1, 0, 1)));
        Assert.False(p.IsSkipped(null));
    }

    [Fact]
    public void 损坏的跳过版本不影响判断()
    {
        var p = new UpdatePrefs { SkippedVersion = "not-a-version" };
        Assert.False(p.IsSkipped(new Version(1, 0, 1)));
    }
}
