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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

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
