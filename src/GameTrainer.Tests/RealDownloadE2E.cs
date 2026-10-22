using GameTrainer.Core.Net;
using GameTrainer.Adapter;
using Xunit;

namespace GameTrainer.Tests;

public class RealDownloadE2E
{
    /// <summary>
    /// 真实网络下载。默认跳过——CI 环境不一定能连 GitHub。
    /// 本地要跑时设 GAMETRAINER_E2E=1。
    /// </summary>
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("GAMETRAINER_E2E") == "1";

    [Fact]
    public async Task 真实下载适配包并校验哈希()
    {
        // 不引入 SkippableFact 包：直接用条件早退，测试仍然算通过。
        if (!Enabled) return;

        var routes = new GithubRoutes { Items = { new GithubRoute("", 10) } };
        var gateway = new NetworkGateway(routes, "auto");

        var (index, error) = await AdapterRepository.FetchIndexAsync(gateway);
        Assert.NotNull(index);
        Assert.True(index.Adapters.Count > 0, "索引为空: " + error);

        var a = index.Adapters[0];
        Assert.False(string.IsNullOrWhiteSpace(a.Sha256), "索引缺少 sha256");

        string root = Path.Combine(Path.GetTempPath(), "gt-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var (ok, err) = await AdapterRepository.InstallAsync(root, a, gateway);
            Assert.True(ok, "安装失败: " + err);

            string f = Path.Combine(root, "adapters", a.Id, "adapter.json");
            Assert.True(File.Exists(f));
            Assert.True(new FileInfo(f).Length > 1000, "文件太小，疑似内容不对");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}