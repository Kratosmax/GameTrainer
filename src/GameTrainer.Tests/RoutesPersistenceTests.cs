using GameTrainer.Core.Net;
using Xunit;

namespace GameTrainer.Tests;

public class RoutesPersistenceTests
{
    [Fact]
    public void 保存的线路能被读回并参与拼接()
    {
        // 用临时目录避免污染用户配置
        var saved = GithubRoutes.Load();

        // 造一份确定的配置，走 Save -> Load 往返
        var original = new GithubRoutes
        {
            Items =
            {
                new GithubRoute("", 10),
                new GithubRoute("https://gh-proxy.org", 5)
            }
        }.Normalize();

        original.Save();
        var loaded = GithubRoutes.Load();

        Assert.Equal(2, loaded.Items.Count);
        Assert.Contains(loaded.Items, r => r.IsDirect && r.Priority == 10);
        Assert.Contains(loaded.Items, r => !r.IsDirect
            && r.BaseUrl == "https://gh-proxy.org" && r.Priority == 5);

        // 关键：读回的配置能用于拼接
        var active = loaded.Active.ToList();
        Assert.Equal(2, active.Count);
        Assert.True(active[0].IsDirect);
        Assert.Equal("https://gh-proxy.org/https://github.com/a/b",
            active[1].Apply("https://github.com/a/b"));

        // 还原用户原配置
        saved.Save();
    }
}