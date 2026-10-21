using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.Core.Updates;

/// <summary>
/// 更新清单。设计照搬 PinNote：签名防伪造、通道隔离、URL 白名单、大小上限。
///
/// 威胁模型：攻击者能改 GitHub Release 上的文件（账号泄露、供应链投毒），
/// 但**不能拿到签名私钥**。因此清单必须带 RSA 签名，程序内置公钥验签。
/// 只有 SHA256 而没有签名，等于"改了包再改清单"也能通过。
/// </summary>
internal sealed class UpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = "";

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = "";

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; init; } = "";
}

internal sealed record UpdateInfo(
    System.Version Version,
    string Channel,
    Uri DownloadUri,
    long Size,
    string Sha256,
    string ReleaseNotes,
    string RawManifest);

internal static class UpdateManifestCodec
{
    /// <summary>安装包大小上限。超过即拒绝下载，防磁盘打满。</summary>
    public const long MaximumPackageSize = 200L * 1024 * 1024;

    /// <summary>清单大小上限。正常清单约 1KB，64KB 足够容纳长更新说明。</summary>
    public const int MaximumManifestSize = 64 * 1024;

    /// <summary>允许的下载地址前缀。锁定到本仓库的 Release，防止清单被改成任意 URL。</summary>
    private const string ReleasePathPrefix = "/Kratosmax/GameTrainer/releases/download/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        // 出现未知字段即失败：防止将来加字段时老版本静默忽略安全相关配置
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>解析并验证清单。任一检查不通过都抛异常，调用方必须放弃更新。</summary>
    public static UpdateInfo ParseAndVerify(string json, string publicKeyPem, string expectedChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);

        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestSize)
            throw new InvalidDataException("更新清单超过允许大小。");

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("更新清单为空。");

        Validate(manifest, expectedChannel);

        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.Signature); }
        catch (FormatException ex) { throw new InvalidDataException("更新清单签名格式无效。", ex); }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (!rsa.VerifyData(
                Encoding.UTF8.GetBytes(GetCanonicalPayload(manifest)),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
            throw new CryptographicException("更新清单签名验证失败：清单可能被篡改。");

        return new UpdateInfo(
            System.Version.Parse(manifest.Version),
            manifest.Channel,
            new Uri(manifest.DownloadUrl),
            manifest.Size,
            manifest.Sha256.ToUpperInvariant(),
            manifest.ReleaseNotes,
            json);
    }

    /// <summary>用私钥签名清单。发布脚本用，不随程序分发。</summary>
    public static string Sign(UpdateManifest manifest, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        Validate(manifest, manifest.Channel, requireSignature: false);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(rsa.SignData(
            Encoding.UTF8.GetBytes(GetCanonicalPayload(manifest)),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    public static string Serialize(UpdateManifest manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

    private static void Validate(UpdateManifest m, string expectedChannel, bool requireSignature = true)
    {
        if (m.SchemaVersion != 1)
            throw new InvalidDataException("不支持的更新清单版本。");

        // 三段式且不带修订号：与标签 v1.0.0 一一对应
        if (!System.Version.TryParse(m.Version, out var v) ||
            v.Major < 0 || v.Build < 0 || v.Revision >= 0)
            throw new InvalidDataException("更新版本必须是三段 SemVer 数字版本。");

        if (!string.Equals(m.Channel, expectedChannel, StringComparison.Ordinal))
            throw new InvalidDataException($"更新通道不匹配：清单是 {m.Channel}，程序是 {expectedChannel}。");

        // URL 三重校验：https + 主机固定 + 路径前缀固定
        if (!Uri.TryCreate(m.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("更新下载地址不在允许的 GitHub Release 范围内。");

        if (m.Size <= 0 || m.Size > MaximumPackageSize)
            throw new InvalidDataException("更新包大小超出允许范围。");

        if (m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("更新包 SHA-256 格式无效。");

        if (requireSignature && string.IsNullOrWhiteSpace(m.Signature))
            throw new InvalidDataException("更新清单缺少签名。");

        if (Encoding.UTF8.GetByteCount(m.ReleaseNotes ?? "") > 16 * 1024)
            throw new InvalidDataException("更新说明超过允许大小。");
    }

    /// <summary>
    /// 签名的规范化载荷：字段顺序固定、用 \n 连接、SHA 统一大写。
    /// 不做规范化的话，同一份清单因序列化顺序不同会得到不同签名，验签随机失败。
    /// </summary>
    private static string GetCanonicalPayload(UpdateManifest m) => string.Join('\n',
        m.SchemaVersion.ToString(CultureInfo.InvariantCulture),
        m.Version,
        m.Channel,
        m.DownloadUrl,
        m.Size.ToString(CultureInfo.InvariantCulture),
        m.Sha256.ToUpperInvariant());
}
