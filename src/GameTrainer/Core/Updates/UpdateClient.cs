using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace GameTrainer.Core.Updates;

/// <summary>
/// 更新检查客户端。
///
/// 只做两件事：取清单、验签后判断有无新版本。
/// **不自带公钥来源的可信判断**——公钥编译进程序（见 <see cref="Trust"/>），
/// 不从网络获取，否则攻击者换清单的同时换公钥就全无效了。
/// </summary>
internal static class UpdateClient
{
    /// <summary>
    /// 走系统代理的 HttpClient。
    ///
    /// 为什么必须显式配：国内直连 github.com 常被 RST/超时（实测 15s 无响应），
    /// 而 .NET 默认虽然会读 HTTPS_PROXY 环境变量，但**不读 Windows 系统代理设置**。
    /// 用户开了 Clash/V2Ray 而没设环境变量时，更新会卡满超时才报错，
    /// 表现得像"程序坏了"。
    /// </summary>
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// 供 UpdateInstaller 复用的 handler。
    /// 必须共用同一套代理解析——检查走代理、下载走直连是最难排查的失败。
    /// </summary>
    internal static HttpClientHandler ProxyHandler => new()
    {
        UseProxy = true,
        Proxy = ResolveProxy(),
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    };

    private static HttpClient CreateHttpClient()
        => new(new HttpClientHandler
        {
            UseProxy = true,
            Proxy = ResolveProxy(),
            // 重定向跟随：releases/latest/download 会跳到 objects.githubusercontent.com
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        })
        { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// 解析代理，优先级：用户手动设置 → 环境变量 → 系统代理 → 探测常见本地端口。
    ///
    /// 手动设置入口在设置页「网络」区块。用户能显式指定
    /// http://host:port 或强制直连，不必依赖自动探测。
    /// 更新检查与适配包下载共用这一套解析（网络代理参考：两层线路必须共享同一策略）。
    /// </summary>
    private static IWebProxy ResolveProxy()
    {
        // 0) 用户手动配置
        string manual = ReadManualProxySetting();
        if (!string.IsNullOrWhiteSpace(manual))
        {
            if (manual.Equals("direct", StringComparison.OrdinalIgnoreCase))
                return null;   // 强制直连

            if (!manual.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(manual, UriKind.Absolute, out var manualUri))
                return new WebProxy(manualUri);
        }

        // 1) 环境变量（HTTPS_PROXY / HTTP_PROXY / ALL_PROXY），大小写都试
        foreach (string name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            string v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v) &&
                Uri.TryCreate(v, UriKind.Absolute, out var envUri))
                return new WebProxy(envUri);
        }

        // 2) 系统代理（代理客户端勾了"系统代理"时会写 WinINET 设置）
        var sys = System.Net.WebRequest.DefaultWebProxy;
        if (sys != null && !sys.IsBypassed(new Uri("https://github.com")))
            return sys;

        // 3) 探测常见本地代理端口。
        //
        // 这一步不是多余的：实测本机 Clash 跑在规则/TUN 模式，
        // ProxyEnable = 0（系统代理开关是关的），环境变量也没设。
        // 用户明明开着代理，程序却提示"超时"——看起来像程序坏了。
        // 只探测回环地址，不扫描网络。
        foreach (int port in new[] { 10808, 7890, 10809, 2080, 8889 })
        {
            if (IsLocalPortOpen(port))
                return new WebProxy(new Uri($"http://127.0.0.1:{port}"));
        }

        return null;
    }

    /// <summary>
    /// 读用户手动代理设置。读失败返回空（视为 auto），不影响更新流程。
    /// </summary>
    private static string ReadManualProxySetting()
    {
        try
        {
            return Prefs.Load().HttpProxy ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>本机回环端口是否在监听。超时必须短，不能让探测拖慢启动。</summary>
    private static bool IsLocalPortOpen(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected;
        }
        catch { return false; }
    }

    public sealed record CheckResult(
        bool HasUpdate,
        System.Version CurrentVersion,
        UpdateInfo Latest,
        string Message);

    /// <summary>
    /// 检查更新。
    /// </summary>
    /// <param name="channel">当前通道（lite / full），决定读哪个清单。</param>
    /// <param name="manifestUrl">清单地址。</param>
    public static async Task<CheckResult> CheckAsync(
        System.Version currentVersion, string channel, string manifestUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);

        string publicKey = Trust.PublicKeyPem;
        if (string.IsNullOrWhiteSpace(publicKey))
            throw new InvalidOperationException("程序未内置更新公钥，无法验证清单，已放弃检查。");

        string json;
        try
        {
            using var resp = await Http.GetAsync(manifestUrl, ct);
            resp.EnsureSuccessStatusCode();

            // 先看长度：Content-Length 超限直接拒绝，不读入内存
            long? len = resp.Content.Headers.ContentLength;
            if (len.HasValue && len.Value > UpdateManifestCodec.MaximumManifestSize)
                throw new InvalidDataException($"清单大小 {len.Value} 超过上限。");

            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (TaskCanceledException ex)
        {
            // 超时是最常见的失败：国内直连 GitHub 时几乎必然发生。
            // 光说"超时"用户不知道怎么办，必须给出可操作的建议。
            throw new InvalidOperationException(
                "获取更新清单超时。\n" +
                "常见原因：所在网络无法直连 GitHub。\n" +
                "可尝试：\n" +
                "  1. 开启代理软件（Clash / V2Ray 等）后重试\n" +
                "  2. 设置环境变量 HTTPS_PROXY=http://127.0.0.1:<端口>\n" +
                "  3. 手动到 https://github.com/Kratosmax/GameTrainer/releases 下载",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"获取更新清单失败：{ex.Message}\n" +
                "若为网络受限，请开启代理后重试，或手动到 Release 页面下载。", ex);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidOperationException($"获取更新清单失败：{ex.Message}", ex);
        }

        // 验签 + 通道 + URL + 大小 + 哈希格式，全在 ParseAndVerify 里
        UpdateInfo latest = UpdateManifestCodec.ParseAndVerify(json, publicKey, channel);

        bool newer = latest.Version > currentVersion;
        string msg = newer
            ? $"发现新版本 {latest.Version}（当前 {currentVersion}）"
            : $"已是最新版本（当前 {currentVersion}）";

        return new CheckResult(newer, currentVersion, latest, msg);
    }

    /// <summary>清单地址。通道决定文件名，与打包脚本产出一一对应。</summary>
    public static string ManifestUrlFor(string channel) => channel switch
    {
        "lite" => "https://github.com/Kratosmax/GameTrainer/releases/latest/download/update-lite.json",
        "full" => "https://github.com/Kratosmax/GameTrainer/releases/latest/download/update-full.json",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "未知通道")
    };
}
