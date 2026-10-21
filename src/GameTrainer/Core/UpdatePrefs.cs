using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.Core;

/// <summary>
/// 更新相关的用户偏好。
///
/// 存到 %LOCALAPPDATA%\GameTrainer\update-prefs.json，不放在程序目录：
/// - 程序可能装在 Program Files（无写权限）
/// - 便携版用户可能整个目录搬走，偏好不该跟着走
/// </summary>
internal sealed class UpdatePrefs
{
    [JsonPropertyName("autoCheck")]
    public bool AutoCheck { get; set; } = true;

    /// <summary>
    /// 用户点过「跳过此版本」的版本号。
    /// 记录后同版本不再提示；出现更新的版本会覆盖它。
    /// </summary>
    [JsonPropertyName("skippedVersion")]
    public string SkippedVersion { get; set; }

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameTrainer");
            return Path.Combine(dir, "update-prefs.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static UpdatePrefs Load()
    {
        try
        {
            string p = FilePath;
            if (!File.Exists(p)) return new UpdatePrefs();
            return JsonSerializer.Deserialize<UpdatePrefs>(File.ReadAllText(p), JsonOpts)
                   ?? new UpdatePrefs();
        }
        catch
        {
            // 配置文件损坏不该拦住启动，用默认值继续
            return new UpdatePrefs();
        }
    }

    public void Save()
    {
        try
        {
            string p = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 存不下就算了，不影响当前会话 */ }
    }

    /// <summary>该版本是否被用户跳过。</summary>
    public bool IsSkipped(Version v)
        => v != null &&
           System.Version.TryParse(SkippedVersion, out Version skipped) &&
           skipped >= v;

    public void Skip(Version v)
    {
        if (v == null) return;
        SkippedVersion = v.ToString(3);
        Save();
    }
}
