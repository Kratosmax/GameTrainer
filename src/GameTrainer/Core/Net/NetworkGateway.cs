using System.Net;
using System.Text.Json;

namespace GameTrainer.Core.Net;

/// <summary>
/// 统一网络出口 —— 把「GitHub 线路拼接」与「HTTP 网络代理」两个独立模块组合起来。
///
/// 为什么分开：
///   线路拼接改的是 URL（应用层），HTTP 代理改的是出口端口（传输层）。
///   两者的失效原因完全不同——代理挂了要换端口，线路挂了要换前缀，
///   合成一个"出网方式"下拉框会让用户无从排查到底是哪一层坏了。
///
/// 但下载时要两者同时生效：先按优先级选线路拼 URL，再经代理发出。
/// 这个类就是那个组合点。
/// </summary>
internal sealed class NetworkGateway
{
    private readonly GithubRoutes _routes;
    private readonly string _httpProxy;

    /// <summary>单次尝试的超时。线路挂了要快速失败切下一条，不能干等。</summary>
    public static TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(12);

    public NetworkGateway(GithubRoutes routes, string httpProxy)
    {
        _routes = (routes ?? GithubRoutes.Default()).Normalize();
        _httpProxy = httpProxy ?? "auto";
    }

    /// <summary>当前线路表（只读用途）。</summary>
    public GithubRoutes Routes => _routes;

    /// <summary>
    /// 下载一个 GitHub 资源。
    ///
    /// 按线路优先级依次尝试：直连能通就不绕前缀，直连不通才走代理前缀。
    /// 全部失败时返回最后一次的错误，并把每条线路的失败原因串起来——
    /// 只说"下载失败"没法排查是哪条线路的问题。
    /// </summary>
    public async Task<(byte[] Data, string Error)> GetBytesAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, "地址为空");

        var attempts = new List<string>();

        foreach (var route in _routes.Active)
        {
            string target = route.Apply(url);
            string label = route.IsDirect ? "直连" : route.BaseUrl;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(AttemptTimeout);

                using var handler = BuildHandler();
                using var http = new HttpClient(handler) { Timeout = AttemptTimeout };

                var resp = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    attempts.Add($"{label}: HTTP {(int)resp.StatusCode}");
                    continue;
                }

                byte[] data = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                if (data.Length == 0)
                {
                    attempts.Add($"{label}: 空响应");
                    continue;
                }

                return (data, null);
            }
            catch (OperationCanceledException)
            {
                attempts.Add($"{label}: 超时");
            }
            catch (Exception ex)
            {
                // 只留首行：完整异常里全是堆栈，对用户没意义
                string msg = ex.Message.Split('\n')[0].Trim();
                if (msg.Length > 60) msg = msg[..60] + "…";
                attempts.Add($"{label}: {msg}");
            }
        }

        if (attempts.Count == 0)
            return (null, "没有启用的线路。请在设置 → 网络里至少启用一条。");

        return (null, "全部线路失败 —— " + string.Join("；", attempts));
    }

    /// <summary>下载并解析 JSON。</summary>
    public async Task<(T Value, string Error)> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var (data, error) = await GetBytesAsync(url, ct);
        if (data == null) return (default, error);

        try
        {
            return (JsonSerializer.Deserialize<T>(data), null);
        }
        catch (Exception ex)
        {
            return (default, "JSON 解析失败：" + ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>
    /// 构造 HttpClientHandler，套上 HTTP 代理设置。
    ///
    /// auto 模式的解析顺序：环境变量 → 系统代理 → 探测常见本地端口。
    /// 这个顺序有实测依据：环境变量最明确（用户显式设的），
    /// 系统代理次之，本地端口是猜测所以放最后。
    /// </summary>
    private HttpClientHandler BuildHandler()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        var proxy = ResolveProxy(_httpProxy);
        if (proxy != null) handler.Proxy = proxy;
        else handler.UseProxy = false;

        return handler;
    }

    private static IWebProxy ResolveProxy(string setting)
    {
        setting = (setting ?? "auto").Trim();

        if (setting.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!setting.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(setting, UriKind.Absolute, out var uri)
                ? new WebProxy(uri)
                : null;
        }

        // auto：环境变量优先
        foreach (string name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            string v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v) && Uri.TryCreate(v.Trim(), UriKind.Absolute, out var uri))
                return new WebProxy(uri);
        }

        // 系统代理
        try
        {
            var sys = WebRequest.DefaultWebProxy;
            if (sys != null)
            {
                var test = sys.GetProxy(new Uri("https://github.com"));
                if (test != null && test != new Uri("https://github.com")) return sys;
            }
        }
        catch { }

        // 本地常见端口探测：只探 TCP 是否能连上，不发 HTTP 请求
        foreach (int port in new[] { 10808, 7890, 10809, 2080, 8889 })
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var task = client.ConnectAsync("127.0.0.1", port);
                if (task.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected)
                    return new WebProxy($"http://127.0.0.1:{port}");
            }
            catch { }
        }

        return null;
    }
}
