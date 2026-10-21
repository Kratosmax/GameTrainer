using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.ReleaseTool;

/// <summary>
/// 发布工具：生成并签名更新清单。
///
/// 只在发布者本机跑，不进发行包。私钥通过参数传入文件路径，
/// 绝不写进仓库或日志。
/// </summary>
internal static class Program
{
    private sealed class Manifest
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("channel")] public string Channel { get; set; } = "";
        [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
        [JsonPropertyName("releaseNotes")] public string ReleaseNotes { get; set; } = "";
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("""
用法:
  ReleaseTool sign   --channel <lite|full> --version <x.y.z> --package <zip路径> --output <json路径> --key <私钥pem> [--notes <说明文件>]
  ReleaseTool verify --channel <lite|full> --manifest <json路径> --package <zip路径> --key <公钥pem>
""");
            return 2;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "sign" => Sign(Parse(args)),
                "verify" => Verify(Parse(args)),
                _ => Fail($"未知动作 {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[失败] " + ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length - 1; i += 2)
            if (args[i].StartsWith("--")) d[args[i][2..]] = args[i + 1];
        return d;
    }

    private static int Sign(Dictionary<string, string> a)
    {
        string channel = Require(a, "channel");
        string version = Require(a, "version");
        string packagePath = Require(a, "package");
        string output = Require(a, "output");
        string keyPath = Require(a, "key");

        if (channel is not ("lite" or "full")) return Fail("channel 必须是 lite 或 full");
        if (!File.Exists(packagePath)) return Fail($"找不到安装包 {packagePath}");
        if (!File.Exists(keyPath)) return Fail($"找不到私钥 {keyPath}");

        var fi = new FileInfo(packagePath);
        string sha = ComputeSha256(packagePath);
        string fileName = Path.GetFileName(packagePath);

        // 下载地址必须落在本仓库 Release 下，程序侧会校验这个前缀
        string url = $"https://github.com/Kratosmax/GameTrainer/releases/download/v{version}/{fileName}";

        string notes = "";
        if (a.TryGetValue("notes", out string notesPath) && File.Exists(notesPath))
            notes = File.ReadAllText(notesPath).Trim();

        var m = new Manifest
        {
            Version = version,
            Channel = channel,
            DownloadUrl = url,
            Size = fi.Length,
            Sha256 = sha,
            ReleaseNotes = notes
        };

        // 规范化载荷签名：字段顺序固定，SHA 大写
        string payload = string.Join('\n',
            m.SchemaVersion.ToString(),
            m.Version,
            m.Channel,
            m.DownloadUrl,
            m.Size.ToString(),
            m.Sha256.ToUpperInvariant());

        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(File.ReadAllText(keyPath));
            m.Signature = Convert.ToBase64String(
                rsa.SignData(System.Text.Encoding.UTF8.GetBytes(payload),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(m,
            new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"已生成 {output}");
        Console.WriteLine($"  channel = {channel}");
        Console.WriteLine($"  version = {version}");
        Console.WriteLine($"  size    = {fi.Length:N0}");
        Console.WriteLine($"  sha256  = {sha}");
        Console.WriteLine($"  url     = {url}");
        return 0;
    }

    private static int Verify(Dictionary<string, string> a)
    {
        string channel = Require(a, "channel");
        string manifestPath = Require(a, "manifest");
        string packagePath = Require(a, "package");
        string keyPath = Require(a, "key");

        if (!File.Exists(manifestPath)) return Fail($"找不到清单 {manifestPath}");
        if (!File.Exists(packagePath)) return Fail($"找不到安装包 {packagePath}");
        if (!File.Exists(keyPath)) return Fail($"找不到公钥 {keyPath}");

        var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
        if (m == null) return Fail("清单为空");

        if (m.Channel != channel) return Fail($"通道不匹配：清单 {m.Channel}，期望 {channel}");

        string payload = string.Join('\n',
            m.SchemaVersion.ToString(), m.Version, m.Channel,
            m.DownloadUrl, m.Size.ToString(), m.Sha256.ToUpperInvariant());

        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(File.ReadAllText(keyPath));
            if (!rsa.VerifyData(System.Text.Encoding.UTF8.GetBytes(payload),
                    Convert.FromBase64String(m.Signature),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return Fail("签名验证失败");
        }

        var fi = new FileInfo(packagePath);
        if (fi.Length != m.Size) return Fail($"大小不符：清单 {m.Size}，实际 {fi.Length}");

        string actual = ComputeSha256(packagePath);
        if (!string.Equals(actual, m.Sha256, StringComparison.OrdinalIgnoreCase))
            return Fail($"哈希不符：清单 {m.Sha256}，实际 {actual}");

        Console.WriteLine($"[通过] {channel} {m.Version}  签名/大小/哈希 全部一致");
        return 0;
    }

    private static string Require(Dictionary<string, string> a, string key)
        => a.TryGetValue(key, out string v) && !string.IsNullOrWhiteSpace(v)
            ? v : throw new ArgumentException($"缺少参数 --{key}");

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine("[失败] " + msg);
        return 1;
    }
}
