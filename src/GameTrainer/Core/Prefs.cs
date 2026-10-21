using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.Core;

/// <summary>
/// 用户偏好：全局设置 + 每个修改项的独立配置。
///
/// 存到 %LOCALAPPDATA%\GameTrainer\prefs.json，不放在程序目录：
///   - 程序可能装在 Program Files（无写权限）
///   - 便携版用户可能整个目录搬走，偏好不该跟着走
///
/// 与 UpdatePrefs 分开是为了兼容旧版本：老配置里没有 prefs.json 时
/// 全部走默认值，不把旧文件当损坏。
/// </summary>
internal sealed class Prefs
{
    /// <summary>单个修改项的用户配置。</summary>
    public sealed class EntryPrefs
    {
        /// <summary>
        /// 全局快捷键，空字符串表示不注册。
        ///
        /// 默认空：不替用户占用系统热键。原来是硬编码 Ctrl+Alt+F1/F2
        /// 做「全开/全关」，既浪费又无法按需绑定。
        /// </summary>
        [JsonPropertyName("hotkey")]
        public string Hotkey { get; set; } = "";

        /// <summary>该修改项在界面上是否折叠（只显示标题和当前值）。</summary>
        [JsonPropertyName("collapsed")]
        public bool Collapsed { get; set; }
    }

    /// <summary>按「适配包ID/语义」索引的修改项配置。</summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, EntryPrefs> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 手动指定的 HTTP 出网代理，空字符串表示自动。
    ///
    /// "auto" 或 "" = 走 UpdateClient 的自动探测（环境变量 → 系统代理 → 常见端口）
    /// 其他值 = 用户显式指定的 http://host:port
    /// "direct" = 强制直连，不使用任何代理
    /// </summary>
    [JsonPropertyName("httpProxy")]
    public string HttpProxy { get; set; } = "auto";

    /// <summary>
    /// 是否在启动时静默检查更新。默认开。
    /// </summary>
    [JsonPropertyName("checkUpdateOnStart")]
    public bool CheckUpdateOnStart { get; set; } = true;

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameTrainer");
            return Path.Combine(dir, "prefs.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Prefs Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var p = JsonSerializer.Deserialize<Prefs>(json, JsonOpts);
                if (p != null)
                {
                    // 旧配置缺少 entries 时补回，不视为损坏
                    p.Entries ??= new Dictionary<string, EntryPrefs>(StringComparer.OrdinalIgnoreCase);
                    return p;
                }
            }
        }
        catch
        {
            // 配置损坏时用默认值，不阻塞启动；下次保存会覆盖
        }
        return new Prefs();
    }

    /// <summary>保存。同目录临时文件再替换，避免写一半断电留下坏文件。</summary>
    public bool Save()
    {
        try
        {
            string path = FilePath;
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));

            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>组合键：适配包ID + 语义，用作 entries 的 key。</summary>
    public static string EntryKey(string adapterId, string semantic)
        => $"{adapterId}/{semantic}";

    /// <summary>取某个修改项的配置，没有则创建默认并挂上去。</summary>
    public EntryPrefs ForEntry(string adapterId, string semantic)
    {
        string key = EntryKey(adapterId, semantic);
        if (!Entries.TryGetValue(key, out var e))
        {
            e = new EntryPrefs();
            Entries[key] = e;
        }
        return e;
    }
}
