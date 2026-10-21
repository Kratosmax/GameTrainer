using System.IO.Compression;
using System.Security.Cryptography;
using GameTrainer.Core.Updates;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 更新清单的签发与校验。
///
/// 这是更新链路的安全根基：攻击者能改 Release 上的文件（账号泄露），
/// 但拿不到私钥。没有签名，改了包再改清单照样能过。
/// </summary>
public class UpdateManifestTests
{
    /// <summary>测试用临时 RSA 密钥（每次运行现生成，不依赖发布私钥）。</summary>
    private static (string PrivatePem, string PublicPem) NewKey()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static UpdateManifest MakeManifest(string channel = "lite", string version = "1.0.1") => new()
    {
        SchemaVersion = 1,
        Version = version,
        Channel = channel,
        DownloadUrl = $"https://github.com/Kratosmax/GameTrainer/releases/download/v{version}/GameTrainer-{version}-Lite.zip",
        Size = 1024,
        Sha256 = new string('A', 64),
        ReleaseNotes = "test"
    };

    private static string SignAndSerialize(UpdateManifest m, string privatePem)
    {
        // Sign 只返回签名，需要回填后序列化
        var signed = new UpdateManifest
        {
            SchemaVersion = m.SchemaVersion,
            Version = m.Version,
            Channel = m.Channel,
            DownloadUrl = m.DownloadUrl,
            Size = m.Size,
            Sha256 = m.Sha256,
            ReleaseNotes = m.ReleaseNotes
        };
        string sig = UpdateManifestCodec.Sign(signed, privatePem);
        var withSig = new UpdateManifest
        {
            SchemaVersion = m.SchemaVersion,
            Version = m.Version,
            Channel = m.Channel,
            DownloadUrl = m.DownloadUrl,
            Size = m.Size,
            Sha256 = m.Sha256,
            ReleaseNotes = m.ReleaseNotes,
            Signature = sig
        };
        return UpdateManifestCodec.Serialize(withSig);
    }

    [Fact]
    public void 正确签名_验签通过()
    {
        var (priv, pub) = NewKey();
        string json = SignAndSerialize(MakeManifest(), priv);

        var info = UpdateManifestCodec.ParseAndVerify(json, pub, "lite");
        Assert.Equal("1.0.1", info.Version.ToString(3));
        Assert.Equal("lite", info.Channel);
    }

    [Fact]
    public void 篡改版本号_验签失败()
    {
        var (priv, pub) = NewKey();
        string json = SignAndSerialize(MakeManifest(version: "1.0.1"), priv);

        // 改版本号但不重新签名 —— 典型投毒手法
        string tampered = json.Replace("\"version\": \"1.0.1\"", "\"version\": \"9.9.9\"");
        Assert.NotEqual(json, tampered);

        Assert.ThrowsAny<CryptographicException>(
            () => UpdateManifestCodec.ParseAndVerify(tampered, pub, "lite"));
    }

    [Fact]
    public void 篡改下载地址_验签失败()
    {
        var (priv, pub) = NewKey();
        string json = SignAndSerialize(MakeManifest(), priv);
        // 把仓库换成攻击者的（保持路径前缀形状，让它能过 URL 白名单检查，
        // 从而真正落到验签这一步）
        string tampered = json.Replace("Kratosmax/GameTrainer", "Kratosmax/GameTrainer/../../attacker");
        // 篡改后 URL 白名单可能先拦，也可能验签先失败——两者都算拒绝
        Assert.ThrowsAny<Exception>(
            () => UpdateManifestCodec.ParseAndVerify(tampered, pub, "lite"));
    }

    [Fact]
    public void 用别人的公钥_验签失败()
    {
        var (priv1, _) = NewKey();
        var (_, pub2) = NewKey();
        string json = SignAndSerialize(MakeManifest(), priv1);

        Assert.ThrowsAny<CryptographicException>(
            () => UpdateManifestCodec.ParseAndVerify(json, pub2, "lite"));
    }

    [Fact]
    public void 通道不匹配_拒绝()
    {
        var (priv, pub) = NewKey();
        string json = SignAndSerialize(MakeManifest(channel: "full"), priv);

        var ex = Assert.Throws<InvalidDataException>(
            () => UpdateManifestCodec.ParseAndVerify(json, pub, "lite"));
        Assert.Contains("通道不匹配", ex.Message);
    }

    [Fact]
    public void 下载地址不在本仓库_拒绝()
    {
        var (priv, pub) = NewKey();
        var m = MakeManifest();
        // 关键：Sign 内部会 Validate，非法 URL 根本签不出来。
        // 所以这里断言"签名阶段就拒绝"，而不是"验签阶段拒绝"。
        var evil = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = m.Version,
            Channel = m.Channel,
            DownloadUrl = "https://evil.com/GameTrainer-1.0.1.zip",
            Size = m.Size,
            Sha256 = m.Sha256
        };
        var ex = Assert.Throws<InvalidDataException>(
            () => SignAndSerialize(evil, priv));
        Assert.Contains("允许的 GitHub Release 范围", ex.Message);
    }

    [Fact]
    public void http而非https_拒绝()
    {
        var (priv, pub) = NewKey();
        var evil = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.1",
            Channel = "lite",
            DownloadUrl = "http://github.com/Kratosmax/GameTrainer/releases/download/v1.0.1/x.zip",
            Size = 1024,
            Sha256 = new string('A', 64)
        };
        // Sign 内部会 Validate，非法 URL 签不出来
        Assert.Throws<InvalidDataException>(() => SignAndSerialize(evil, priv));
    }

    [Fact]
    public void 缺少签名_拒绝()
    {
        var (_, pub) = NewKey();
        string json = UpdateManifestCodec.Serialize(MakeManifest());
        var ex = Assert.Throws<InvalidDataException>(
            () => UpdateManifestCodec.ParseAndVerify(json, pub, "lite"));
        Assert.Contains("缺少签名", ex.Message);
    }

    [Fact]
    public void 包大小超上限_拒绝()
    {
        var (priv, pub) = NewKey();
        var big = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.1",
            Channel = "lite",
            DownloadUrl = "https://github.com/Kratosmax/GameTrainer/releases/download/v1.0.1/x.zip",
            Size = UpdateManifestCodec.MaximumPackageSize + 1,
            Sha256 = new string('A', 64)
        };
        Assert.Throws<InvalidDataException>(() => SignAndSerialize(big, priv));
    }

    [Fact]
    public void 哈希格式非法_拒绝()
    {
        var (priv, pub) = NewKey();
        var bad = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.1",
            Channel = "lite",
            DownloadUrl = "https://github.com/Kratosmax/GameTrainer/releases/download/v1.0.1/x.zip",
            Size = 1024,
            Sha256 = "not-a-hash"
        };
        Assert.Throws<InvalidDataException>(() => SignAndSerialize(bad, priv));
    }

    [Fact]
    public void 未知字段_拒绝()
    {
        // UnmappedMemberHandling.Disallow：新增字段时老版本必须报错，
        // 不能静默忽略（可能正是新增的安全约束）
        var (priv, pub) = NewKey();
        string json = SignAndSerialize(MakeManifest(), priv);
        string withExtra = json.Replace("\"releaseNotes\"", "\"evilField\": 1,\n  \"releaseNotes\"");
        Assert.ThrowsAny<Exception>(() => UpdateManifestCodec.ParseAndVerify(withExtra, pub, "lite"));
    }

    [Fact]
    public void 四段版本号_拒绝()
    {
        var (priv, pub) = NewKey();
        Assert.Throws<InvalidDataException>(() => SignAndSerialize(MakeManifest(version: "1.0.1.5"), priv));
    }
}
