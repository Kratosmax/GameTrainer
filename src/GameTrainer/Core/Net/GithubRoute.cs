using System.Text.Json.Serialization;

namespace GameTrainer.Core.Net;

/// <summary>
/// 一条 GitHub 访问线路。
///
/// 两种形态：
///   直连 —— BaseUrl 为空，直接用原始 github.com 地址
///   前缀 —— BaseUrl 非空，拼在原始地址前面，例如
///           https://gh-proxy.org/ + https://github.com/owner/repo/...
///
/// Priority 语义：10 到 1 按顺序尝试，0 表示禁用。
/// 同优先级按列表顺序。
/// </summary>
internal sealed class GithubRoute
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 5;

    /// <summary>是否为直连（不拼接任何前缀）。</summary>
    [JsonIgnore]
    public bool IsDirect => string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>该线路是否参与尝试。</summary>
    [JsonIgnore]
    public bool Enabled => Priority > 0;

    public GithubRoute() { }

    public GithubRoute(string baseUrl, int priority)
    {
        BaseUrl = baseUrl ?? "";
        Priority = priority;
    }

    /// <summary>
    /// 把前缀拼到目标地址上。
    ///
    /// 直连返回原地址。前缀线路按 ghproxy 惯例拼接：前缀 + 原地址，
    /// 中间不加斜杠——多数代理服务要求前缀以 / 结尾、原地址完整跟随。
    /// </summary>
    public string Apply(string url)
    {
        if (IsDirect) return url;
        if (string.IsNullOrWhiteSpace(url)) return url;

        string prefix = BaseUrl.Trim();
        if (!prefix.EndsWith('/')) prefix += "/";

        // 原地址若带协议头，直接拼；否则补 https://
        string target = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://" + url;

        return prefix + target;
    }

    /// <summary>显示名：直连显示说明文字，前缀显示地址。</summary>
    [JsonIgnore]
    public string Display => IsDirect ? "GitHub 直连（不拼接前缀）" : BaseUrl;

    public GithubRoute Clone() => new(BaseUrl, Priority);
}
