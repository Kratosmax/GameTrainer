using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.Core.Net;

/// <summary>
/// GitHub 访问线路表 —— 域名拼接模块的核心。
///
/// 为什么需要它：
/// 国内直连 github.com 常被 RST 或超时（实测 15s 无响应）。
/// 常见做法是用前缀代理（gh-proxy 类服务）把原始地址拼在前面转发。
/// 但单一代理随时可能挂掉，所以要允许多条线路按优先级依次尝试。
///
/// 与「HTTP 网络代理」是两个独立模块：
///   - 线路拼接：改的是 URL 本身，不涉及 TCP 层
///   - 网络代理：改的是出口 IP/端口（HTTP 代理）
/// 两者可以叠加使用，也可以各自单独用。
/// </summary>
internal sealed class GithubRoutes
{
    /// <summary>优先级上限。10 到 1 依次尝试。</summary>
    public const int MaxPriority = 10;

    [JsonPropertyName("routes")]
    public List<GithubRoute> Items { get; set; } = new();

    /// <summary>默认配置：直连优先（直连能通时不绕代理，省时间也更安全）。</summary>
    public static GithubRoutes Default() => new()
    {
        Items =
        {
            new GithubRoute("", 10)
        }
    };

    /// <summary>
    /// 归一化：去掉无效项、夹住优先级、保证至少有一条直连。
    ///
    /// 至少保留直连的原因：所有前缀代理都挂了的时候，
    /// 直连是最后的兜底。哪怕它慢，也比完全没有出口强。
    /// </summary>
    public GithubRoutes Normalize()
    {
        var result = new GithubRoutes();

        foreach (var r in Items)
        {
            if (r == null) continue;

            var copy = r.Clone();
            copy.Priority = Math.Clamp(copy.Priority, 0, MaxPriority);

            if (copy.IsDirect)
            {
                // 空白地址会被 IsDirect 判为直连。
                // 但"地址是几个空格"和"地址真的是空"是两回事：
                // 前者是用户误操作留下的残行，不该变成第二条直连。
                // 归一化只保留一条直连，其余空白项直接丢弃。
                if (result.Items.Any(x => x.IsDirect)) continue;
            }
            else
            {
                // 前缀线路必须能解析成合法 http/https
                if (!IsValidPrefix(copy.BaseUrl)) continue;

                // 重复前缀只留第一条
                if (result.Items.Any(x =>
                        !x.IsDirect && string.Equals(x.BaseUrl.Trim(), copy.BaseUrl.Trim(),
                            StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            result.Items.Add(copy);
        }

        if (!result.Items.Any(x => x.IsDirect))
            result.Items.Add(new GithubRoute("", 1));

        // 按优先级降序排（10 先试），同优先级保持原顺序
        result.Items = result.Items
            .Select((r, i) => (r, i))
            .OrderByDescending(x => x.r.Priority)
            .ThenBy(x => x.i)
            .Select(x => x.r)
            .ToList();

        return result;
    }

    /// <summary>参与尝试的线路（优先级 > 0），按优先级降序。</summary>
    [JsonIgnore]
    public IEnumerable<GithubRoute> Active =>
        Items.Where(r => r.Enabled).OrderByDescending(r => r.Priority);

    /// <summary>
    /// 校验前缀地址：必须是完整 http/https，且不能带查询串。
    ///
    /// 不允许查询串的原因：拼接后查询串会跑到原地址后面，
    /// 语义完全错乱（?a=1https://github.com/...）。
    /// </summary>
    public static bool IsValidPrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.Query)) return false;
        return true;
    }

    /// <summary>至少有一条可用线路（优先级 > 0）。</summary>
    [JsonIgnore]
    public bool HasUsableRoute => Items.Any(r => r.Enabled);

    // ---------- 持久化 ----------

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameTrainer", "routes.json");

    public static GithubRoutes Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Default();

            string json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<GithubRoutes>(json);
            if (parsed == null || parsed.Items.Count == 0) return Default();

            return parsed.Normalize();
        }
        catch
        {
            // 配置坏了就回默认：线路是网络路径，不是业务数据，
            // 丢了大不了重新配，不能因为它让程序起不来。
            return Default();
        }
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true });

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 写失败不影响当前会话使用 */ }
    }
}
