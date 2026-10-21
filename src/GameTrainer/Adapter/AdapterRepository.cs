using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.Adapter;

/// <summary>
/// 适配包仓库客户端：从 GitHub 仓库的 adapters/ 目录发现、下载、安装与卸载适配包。
///
/// 设计取舍：
///   - 适配包是纯 JSON，不含可执行代码，因此不做签名，只用 SHA256 防传输损坏
///   - 下载走与自动更新完全相同的代理解析（UpdateClient.ProxyHandler），
///     用户配一次代理，两条链路都生效
///   - 卸载只删 adapter.json 与同目录附带文件，不碰用户数据
///
/// 为什么用 GitHub Raw 而不是 Release 资产：
/// 适配包会被频繁新增/修改，走 raw 可以单独更新某个包而不必发版。
/// </summary>
internal static class AdapterRepository
{
    /// <summary>仓库 raw 根地址。可被配置文件覆盖以便换镜像。</summary>
    private static string RawRoot =>
        Environment.GetEnvironmentVariable("GAMETRAINER_ADAPTER_RAW")
        ?? "https://raw.githubusercontent.com/Kratosmax/GameTrainer/main/adapters";

    /// <summary>仓库清单地址：列出所有可用适配包及其哈希。</summary>
    private static string IndexUrl => $"{RawRoot}/index.json";

    /// <summary>远端清单里的一项。</summary>
    public sealed class RemoteAdapter
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>适配包内容（adapter.json 的原始 JSON 字符串）。</summary>
        [JsonPropertyName("json")]
        public string Json { get; set; } = "";

        /// <summary>该 JSON 的 SHA256（小写十六进制），用于防传输损坏。</summary>
        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = "";

        /// <summary>可选：README 文本，安装后一并写入。</summary>
        [JsonPropertyName("readme")]
        public string Readme { get; set; }
    }

    public sealed class RemoteIndex
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("adapters")]
        public List<RemoteAdapter> Adapters { get; set; } = new();
    }

    /// <summary>
    /// 拉取远端清单。
    ///
    /// 失败时不抛异常给 UI：返回 null 并给出可读原因，
    /// 让首页可以显示"离线，只能看已安装的游戏"而不是崩掉。
    /// </summary>
    public static async Task<(RemoteIndex Index, string Error)> FetchIndexAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient(UpdateClientProxyHandler())
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            string json = await http.GetStringAsync(IndexUrl, ct).ConfigureAwait(false);
            var index = JsonSerializer.Deserialize<RemoteIndex>(json, JsonOpts);
            if (index == null || index.Adapters.Count == 0)
                return (null, "远端清单为空");

            return (index, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "已取消");
        }
        catch (Exception ex)
        {
            return (null, DescribeFetchError(ex));
        }
    }

    private static string DescribeFetchError(Exception ex)
    {
        string m = ex.Message;
        if (m.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "连接超时。可在设置 → 网络里指定代理，或确认能访问 github.com";

        if (m.Contains("404"))
            return "远端还没有适配包清单（adapters/index.json 不存在）";

        return m.Length > 120 ? m[..120] + "…" : m;
    }

    /// <summary>
    /// 安装一个适配包到 adapters/&lt;id&gt;/adapter.json。
    ///
    /// 先校验哈希再落盘：哈希不符直接失败，不留下半个文件。
    /// 写入用临时文件 + 替换，避免写一半断电留下坏 JSON。
    /// </summary>
    public static (bool Ok, string Error) Install(string skillRoot, RemoteAdapter remote)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(remote.Id))
                return (false, "清单项缺少 id");

            if (string.IsNullOrWhiteSpace(remote.Json))
                return (false, "清单项缺少内容");

            // 哈希校验：只防传输损坏，不是安全边界
            if (!string.IsNullOrWhiteSpace(remote.Sha256))
            {
                string actual = Sha256Hex(remote.Json);
                if (!actual.Equals(remote.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (false, $"校验失败：期望 {remote.Sha256[..8]}… 实际 {actual[..8]}…");
            }

            // 安装前先确认是合法 JSON 且能被适配包模型解析，
            // 否则装进去之后首页会多出一个永远打不开的条目
            GameAdapter parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<GameAdapter>(remote.Json, GameAdapter.JsonOpts)
                         ?? throw new InvalidOperationException("解析结果为空");
            }
            catch (Exception ex)
            {
                return (false, "适配包格式无效：" + ex.Message);
            }

            string dir = Path.Combine(skillRoot, "adapters", remote.Id);
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "adapter.json");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, remote.Json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            if (!string.IsNullOrWhiteSpace(remote.Readme))
            {
                try { File.WriteAllText(Path.Combine(dir, "README.md"), remote.Readme); }
                catch { /* README 写失败不影响适配包可用 */ }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 卸载适配包：删除目录下由安装器写入的文件。
    /// 只删 adapter.json 与 README.md，保留用户可能放进去的其它文件。
    /// </summary>
    public static (bool Ok, string Error) Uninstall(string skillRoot, string adapterId)
    {
        try
        {
            string dir = Path.Combine(skillRoot, "adapters", adapterId);
            if (!Directory.Exists(dir)) return (true, null);

            foreach (string name in new[] { "adapter.json", "README.md" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) File.Delete(p);
            }

            // 目录空了才删，避免误删用户自己放的东西
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>已安装的适配包 ID 集合。</summary>
    public static HashSet<string> InstalledIds(string skillRoot)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string root = Path.Combine(skillRoot, "adapters");
            if (!Directory.Exists(root)) return set;

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, "adapter.json")))
                    set.Add(Path.GetFileName(dir));
            }
        }
        catch { }
        return set;
    }

    public static string Sha256Hex(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 复用自动更新的代理解析，保证两条网络链路行为一致。
    /// </summary>
    private static HttpClientHandler UpdateClientProxyHandler()
        => GameTrainer.Core.Updates.UpdateClient.ProxyHandler;
}
