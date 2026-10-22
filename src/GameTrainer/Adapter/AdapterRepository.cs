using System.Text.Json;
using System.Text.Json.Serialization;
using GameTrainer.Core.Net;

namespace GameTrainer.Adapter;

/// <summary>
/// 适配包仓库 —— 从 GitHub 下载并安装适配包。
///
/// 修正历史问题：早期实现把 adapter.json 的全文内嵌在 index.json 里，
/// 安装时只是把这个字符串写到磁盘，**根本没有下载**。
/// 后果是索引文件随适配包数量线性膨胀（2 个包已 29KB），
/// 而且新增/修改适配包必须重发索引，用户拿不到更新。
///
/// 现在的结构：
///   index.json      —— 只列元数据（id / name / version / sha256 / 文件路径）
///   adapters/&lt;id&gt;/adapter.json —— 真正的内容，安装时才按需下载
///
/// 下载走 NetworkGateway：按线路优先级拼 URL + 套 HTTP 代理。
/// </summary>
internal static class AdapterRepository
{
    /// <summary>仓库 raw 根地址。可用环境变量覆盖（本地测试用）。</summary>
    private static string RawRoot
    {
        get
        {
            string env = Environment.GetEnvironmentVariable("GAMETRAINER_ADAPTER_RAW");
            if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');

            return "https://raw.githubusercontent.com/Kratosmax/GameTrainer/main/adapters";
        }
    }

    private static string IndexUrl => $"{RawRoot}/index.json";

    // ---------- 数据模型 ----------

    /// <summary>索引里的一个适配包条目。只有元数据，没有内容。</summary>
    internal sealed class RemoteAdapter
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("engine")] public string Engine { get; set; } = "";
        [JsonPropertyName("summary")] public string Summary { get; set; } = "";

        /// <summary>adapter.json 的 SHA256，防传输损坏。</summary>
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

        /// <summary>adapter.json 在仓库里的相对路径。空则按 id 推导。</summary>
        [JsonPropertyName("path")] public string Path { get; set; } = "";
    }

    internal sealed class RemoteIndex
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("adapters")] public List<RemoteAdapter> Adapters { get; set; } = new();
    }

    // ---------- 索引 ----------

    /// <summary>
    /// 拉取索引。失败时返回可读的错误原因，不抛异常。
    /// </summary>
    public static async Task<(RemoteIndex Index, string Error)> FetchIndexAsync(
        NetworkGateway gateway, CancellationToken ct = default)
    {
        if (gateway == null) return (null, "网络未初始化");

        var (index, error) = await gateway.GetJsonAsync<RemoteIndex>(IndexUrl, ct);
        if (index == null) return (null, error ?? "索引解析失败");

        index.Adapters ??= new List<RemoteAdapter>();
        return (index, null);
    }

    // ---------- 安装 ----------

    /// <summary>
    /// 下载并安装一个适配包。
    ///
    /// 步骤：下载 adapter.json → 校验 SHA256 → 解析验证 → 原子写入。
    /// 任何一步失败都不落盘，避免首页出现一个打不开的条目。
    /// </summary>
    public static async Task<(bool Ok, string Error)> InstallAsync(
        string skillRoot, RemoteAdapter remote, NetworkGateway gateway, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remote?.Id))
            return (false, "清单项缺少 id");

        if (gateway == null)
            return (false, "网络未初始化");

        // 内容地址：索引给了 path 就用它，否则按约定推导
        string rel = string.IsNullOrWhiteSpace(remote.Path)
            ? $"{remote.Id}/adapter.json"
            : remote.Path.TrimStart('/');

        string url = $"{RawRoot}/{rel}";

        var (data, error) = await gateway.GetBytesAsync(url, ct);
        if (data == null)
            return (false, error);

        // 哈希校验：只防传输损坏，不是安全边界。
        //
        // 容忍换行差异：git 的 CRLF/LF 转换会改变文件字节（实测同一份 adapter.json
        // 本地 10127 字节、GitHub 上 9939 字节，差的全是换行）。
        // 严格比对会让所有下载都失败。做法是先按原样比，不一致再按
        // 归一化换行后比——仍然能挡住真正的传输损坏与内容篡改。
        if (!string.IsNullOrWhiteSpace(remote.Sha256))
        {
            string expected = remote.Sha256.Trim();

            string actual = Sha256Hex(data);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                // 退一步：换行差异不算传输损坏。
                //
                // 索引里的哈希可能按 CRLF 算（本地文件），而仓库里的文件是 LF
                // （.gitattributes 设了 eol=lf），两者字节不同但内容等价。
                // 所以把实际数据按两种换行形式各算一次，任一匹配即通过。
                string asLf = Sha256Hex(ToLf(data));
                string asCrlf = Sha256Hex(ToCrlf(data));

                if (!asLf.Equals(expected, StringComparison.OrdinalIgnoreCase)
                    && !asCrlf.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return (false, $"校验失败：期望 {expected[..Math.Min(8, expected.Length)]}… 实际 {actual[..8]}…");
            }
        }

        // 安装前先确认能被适配包模型解析，
        // 否则装进去之后首页会多出一个永远打不开的条目
        GameAdapter parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GameAdapter>(data, GameAdapter.JsonOpts)
                     ?? throw new InvalidOperationException("解析结果为空");
        }
        catch (Exception ex)
        {
            return (false, "适配包格式无效：" + ex.Message.Split('\n')[0]);
        }

        string dir = Path.Combine(skillRoot, "adapters", remote.Id);
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, "adapter.json");
        string tmp = path + ".tmp";

        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return (false, "写入失败：" + ex.Message.Split('\n')[0]);
        }

        return (true, null);
    }

    // ---------- 卸载 ----------

    /// <summary>
    /// 卸载适配包：只删安装器写入的文件，保留用户可能放进去的其它文件。
    /// 目录空了才删目录。
    /// </summary>
    public static (bool Ok, string Error) Uninstall(string skillRoot, string adapterId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(adapterId)) return (false, "id 为空");

            string dir = Path.Combine(skillRoot, "adapters", adapterId);
            if (!Directory.Exists(dir)) return (true, null);

            foreach (string f in new[] { "adapter.json", "README.md" })
            {
                string p = Path.Combine(dir, f);
                if (File.Exists(p)) File.Delete(p);
            }

            if (Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>已安装的适配包 id 集合。</summary>
    public static HashSet<string> InstalledIds(string skillRoot)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string dir = Path.Combine(skillRoot, "adapters");
        if (!Directory.Exists(dir)) return set;

        foreach (string sub in Directory.GetDirectories(dir))
        {
            if (File.Exists(Path.Combine(sub, "adapter.json")))
                set.Add(Path.GetFileName(sub));
        }

        return set;
    }

    /// <summary>
    /// 把 CRLF 归一成 LF。只用于哈希校验的退路，不写盘。
    /// </summary>
    private static byte[] ToLf(byte[] data)
    {
        if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();

        var output = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\r' && i + 1 < data.Length && data[i + 1] == (byte)'\n')
                continue;
            output.Add(data[i]);
        }
        return output.ToArray();
    }

    /// <summary>
    /// 把孤立的 LF 补成 CRLF。只用于哈希校验的退路，不写盘。
    /// </summary>
    private static byte[] ToCrlf(byte[] data)
    {
        if (data == null || data.Length == 0) return data ?? Array.Empty<byte>();

        var output = new List<byte>(data.Length + data.Length / 16);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\n' && (i == 0 || data[i - 1] != (byte)'\r'))
                output.Add((byte)'\r');
            output.Add(data[i]);
        }
        return output.ToArray();
    }

    private static string Sha256Hex(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }
}
