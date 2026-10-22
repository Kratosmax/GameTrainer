using GameTrainer.Core.Net;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 网络层回归锁。
///
/// 历史 bug：适配包「下载」曾把 index.json 里内嵌的 json 字符串直接写盘，
/// 根本没有发网络请求。用户看到"秒下载 GitHub 资源"才起疑。
///
/// 这组测试锁住三件事：
///   1. 索引不再内嵌内容（体积与字段数）
///   2. 线路拼接按优先级工作，直连不拼接
///   3. 安装真的走网络（用本地 HTTP 服务验证，不依赖外网）
/// </summary>
public class NetworkGatewayTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameTrainer.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    // ---------- 索引格式 ----------

    [Fact]
    public void 索引只含元数据不含适配包内容()
    {
        string path = Path.Combine(RepoRoot(), "adapters", "index.json");
        Assert.True(File.Exists(path), $"找不到 {path}");

        long size = new FileInfo(path).Length;

        // 2 个适配包的纯元数据约 1KB。
        // 旧格式内嵌全文时是 29KB——这个阈值就是用来卡住回退的。
        Assert.True(size < 4 * 1024,
            $"index.json 有 {size} 字节，疑似又把适配包内容内嵌进去了");

        string json = File.ReadAllText(path);

        // 不应该出现内嵌内容字段
        Assert.DoesNotContain("\"json\"", json);
        Assert.DoesNotContain("\"readme\"", json);

        // 应该有按需下载所需的元数据
        Assert.Contains("\"sha256\"", json);
        Assert.Contains("\"path\"", json);
    }

    // ---------- 线路拼接 ----------

    [Fact]
    public void 直连线路不拼接任何前缀()
    {
        var direct = new GithubRoute("", 10);
        string url = "https://github.com/o/r/releases/download/v1/x.zip";

        Assert.True(direct.IsDirect);
        Assert.Equal(url, direct.Apply(url));
    }

    [Fact]
    public void 前缀线路把地址拼在前缀后面()
    {
        var route = new GithubRoute("https://gh-proxy.org", 5);
        string url = "https://github.com/o/r/releases/download/v1/x.zip";

        string result = route.Apply(url);

        Assert.Equal("https://gh-proxy.org/" + url, result);
    }

    [Fact]
    public void 前缀缺少结尾斜杠时自动补齐()
    {
        var route = new GithubRoute("https://gh-proxy.org/", 5);
        Assert.Equal("https://gh-proxy.org/https://github.com/a",
            route.Apply("https://github.com/a"));
    }

    [Fact]
    public void 线路按优先级降序排列且零优先级被禁用()
    {
        var routes = new GithubRoutes
        {
            Items =
            {
                new GithubRoute("https://low.example", 3),
                new GithubRoute("https://high.example", 9),
                new GithubRoute("https://off.example", 0),
                new GithubRoute("", 1)
            }
        }.Normalize();

        var active = routes.Active.ToList();

        Assert.Equal(9, active[0].Priority);
        Assert.Equal(3, active[1].Priority);
        Assert.Equal(1, active[2].Priority);

        // 0 优先级不参与尝试
        Assert.DoesNotContain(active, r => r.BaseUrl.Contains("off.example"));
    }

    [Fact]
    public void 归一化保证至少有一条直连兜底()
    {
        // 全部是前缀线路、没有直连
        var routes = new GithubRoutes
        {
            Items = { new GithubRoute("https://only-proxy.example", 5) }
        }.Normalize();

        Assert.Contains(routes.Items, r => r.IsDirect);
    }

    [Fact]
    public void 非法前缀被拒绝()
    {
        // 缺协议
        Assert.False(GithubRoutes.IsValidPrefix("gh-proxy.org"));
        // 带查询串（拼接后会语义错乱）
        Assert.False(GithubRoutes.IsValidPrefix("https://x.example/?a=1"));
        // 合法
        Assert.True(GithubRoutes.IsValidPrefix("https://gh-proxy.org"));
        Assert.True(GithubRoutes.IsValidPrefix("http://127.0.0.1:8080/proxy"));
    }

    // ---------- 真实网络 ----------

    [Fact]
    public async Task 安装真的发网络请求而不是内嵌写盘()
    {
        // 起一个只服务固定内容的本地 HTTP 服务，
        // 如果安装路径没有真的发请求，这里会失败。
        int port = 18771;
        string root = Path.Combine(Path.GetTempPath(), "gt-net-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var adapterJson = """
        {
          "id": "nettest",
          "name": "Net Test",
          "engine": "native_memory",
          "exe": "x.exe",
          "mode": "allow",
          "connection": { "debugPort": 0, "titleFilter": "" },
          "engineOptions": { "runtimePath": "", "localStorageDb": "", "objectStore": "" },
          "bindings": []
        }
        """;

        byte[] payload = System.Text.Encoding.UTF8.GetBytes(adapterJson);

        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serveTask = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    string rel = ctx.Request.Url!.LocalPath.TrimStart('/');

                    if (rel == "nettest/adapter.json")
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.OutputStream.WriteAsync(payload);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    ctx.Response.Close();
                }
            }
            catch { /* 服务停止 */ }
        });

        try
        {
            // 指向本地服务，直连优先（不需要代理）
            Environment.SetEnvironmentVariable("GAMETRAINER_ADAPTER_RAW", $"http://127.0.0.1:{port}");

            var routes = new GithubRoutes { Items = { new GithubRoute("", 10) } };
            var gateway = new NetworkGateway(routes, "direct");

            var remote = new GameTrainer.Adapter.AdapterRepository.RemoteAdapter
            {
                Id = "nettest",
                Name = "Net Test",
                Sha256 = Sha256Hex(payload),
                Path = "nettest/adapter.json"
            };

            var (ok, error) = await GameTrainer.Adapter.AdapterRepository
                .InstallAsync(root, remote, gateway);

            Assert.True(ok, $"安装失败：{error}");

            string installed = Path.Combine(root, "adapters", "nettest", "adapter.json");
            Assert.True(File.Exists(installed), "安装后文件不存在");

            string content = File.ReadAllText(installed);
            Assert.Contains("\"nettest\"", content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GAMETRAINER_ADAPTER_RAW", null);
            try { listener.Stop(); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 哈希不符时拒绝安装()
    {
        int port = 18772;
        string root = Path.Combine(Path.GetTempPath(), "gt-net-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"bad\"}");

        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.OutputStream.WriteAsync(payload);
                    ctx.Response.Close();
                }
            }
            catch { }
        });

        try
        {
            Environment.SetEnvironmentVariable("GAMETRAINER_ADAPTER_RAW", $"http://127.0.0.1:{port}");

            var gateway = new NetworkGateway(
                new GithubRoutes { Items = { new GithubRoute("", 10) } }, "direct");

            var remote = new GameTrainer.Adapter.AdapterRepository.RemoteAdapter
            {
                Id = "bad",
                // 故意给错哈希
                Sha256 = new string('0', 64),
                Path = "bad/adapter.json"
            };

            var (ok, error) = await GameTrainer.Adapter.AdapterRepository
                .InstallAsync(root, remote, gateway);

            Assert.False(ok);
            Assert.Contains("校验失败", error);

            // 失败时不能留下半成品
            Assert.False(File.Exists(Path.Combine(root, "adapters", "bad", "adapter.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GAMETRAINER_ADAPTER_RAW", null);
            try { listener.Stop(); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string Sha256Hex(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }
}
