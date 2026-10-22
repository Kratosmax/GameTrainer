using GameTrainer.Core.Net;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 线路表的校验与归一化契约。
///
/// 这些用例来自真机验证时发现的问题：
///   - 空地址被静默跳过，用户以为加成功了，实际那条线路永远失败
///   - 只填 "https://" 也算通过，拼接后是 https://https://...
///   - 保存后界面不重建，用户看到的还是保存前的列表
///
/// 前两条在这里锁住。第三条属于 UI 时序，靠 UiShot 截图验证。
/// </summary>
public class RouteValidationTests
{
    [Fact]
    public void 空地址前缀线路被归一化丢弃()
    {
        var routes = new GithubRoutes
        {
            Items =
            {
                new GithubRoute("", 10),          // 直连，保留
                new GithubRoute("   ", 5),        // 空地址，应丢弃
                new GithubRoute("https://ok.example", 3)
            }
        }.Normalize();

        // 空字符串会被当作直连（IsDirect 判定为空白），所以这里应该
        // 只剩 1 条直连 + 1 条有效前缀
        Assert.Equal(2, routes.Items.Count);
        Assert.Contains(routes.Items, r => r.IsDirect);
        Assert.Contains(routes.Items, r => r.BaseUrl == "https://ok.example");
    }

    [Fact]
    public void 只填协议的地址被判定为无效()
    {
        // 拼接后会是 https://https://github.com/... 这种畸形地址
        Assert.False(GithubRoutes.IsValidPrefix("https://"));
        Assert.False(GithubRoutes.IsValidPrefix("http://"));
        Assert.False(GithubRoutes.IsValidPrefix("https:/"));
    }

    [Fact]
    public void 带查询串的前缀被拒绝()
    {
        Assert.False(GithubRoutes.IsValidPrefix("https://x.example/?token=1"));
    }

    [Fact]
    public void 重复前缀只保留第一条()
    {
        var routes = new GithubRoutes
        {
            Items =
            {
                new GithubRoute("", 10),
                new GithubRoute("https://dup.example", 8),
                new GithubRoute("https://dup.example", 3)
            }
        }.Normalize();

        int dupCount = routes.Items.Count(r =>
            string.Equals(r.BaseUrl, "https://dup.example", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, dupCount);
    }

    [Fact]
    public void 优先级超出范围被夹住()
    {
        var routes = new GithubRoutes
        {
            Items =
            {
                new GithubRoute("", 10),
                new GithubRoute("https://high.example", 99),
                new GithubRoute("https://neg.example", -5)
            }
        }.Normalize();

        Assert.All(routes.Items, r =>
            Assert.InRange(r.Priority, 0, GithubRoutes.MaxPriority));

        // 99 被夹到 10，与直连同级
        var high = routes.Items.First(r => r.BaseUrl == "https://high.example");
        Assert.Equal(GithubRoutes.MaxPriority, high.Priority);

        // -5 被夹到 0，即禁用
        var neg = routes.Items.First(r => r.BaseUrl == "https://neg.example");
        Assert.Equal(0, neg.Priority);
        Assert.DoesNotContain(neg, routes.Active);
    }

    [Fact]
    public void 全部线路被禁用时归一化会补一条直连()
    {
        var routes = new GithubRoutes
        {
            Items = { new GithubRoute("https://only.example", 0) }
        }.Normalize();

        // 前缀线路优先级 0 = 禁用，但仍保留在列表里（用户可能只是临时关掉）
        // 同时必须补一条直连兜底
        Assert.Contains(routes.Items, r => r.IsDirect);
        Assert.True(routes.HasUsableRoute);
    }

    [Fact]
    public void 拼接时前缀结尾斜杠不重复()
    {
        var a = new GithubRoute("https://p.example", 5);
        var b = new GithubRoute("https://p.example/", 5);

        string target = "https://github.com/o/r";

        Assert.Equal("https://p.example/" + target, a.Apply(target));
        Assert.Equal("https://p.example/" + target, b.Apply(target));
    }
}
