using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.Adapter;

/// <summary>
/// 用户为每个游戏指定的可执行文件路径。
///
/// 为什么需要它：
/// 早期实现会在 C:/D:/E:/F: 的 Games 目录下按文件名自动找游戏。
/// 那是未经用户同意的磁盘探测——即便只读文件名，也属于用户不知情的行为。
///
/// 现在的规则：
///   1. 适配包里的 installDir 优先（适配包作者明确写的）
///   2. 否则用用户在这里指定的路径
///   3. 都没有就报错，提示用户去设置
///
/// 绝不自动搜索。
/// </summary>
internal sealed class AdapterInstallPaths
{
    [JsonPropertyName("paths")]
    public Dictionary<string, string> Paths { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameTrainer", "install-paths.json");

    private static AdapterInstallPaths _cached;

    /// <summary>
    /// 取某个适配包记录的可执行文件路径。没有记录返回 null。
    /// </summary>
    public static string Resolve(string adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) return null;

        var store = Load();
        return store.Paths.TryGetValue(adapterId, out string p) && !string.IsNullOrWhiteSpace(p)
            ? p
            : null;
    }

    /// <summary>记录路径。传 null 或空则删除该条。</summary>
    public static void Set(string adapterId, string exePath)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) return;

        var store = Load();

        if (string.IsNullOrWhiteSpace(exePath))
            store.Paths.Remove(adapterId);
        else
            store.Paths[adapterId] = exePath.Trim();

        store.Save();
        _cached = store;
    }

    private static AdapterInstallPaths Load()
    {
        if (_cached != null) return _cached;

        try
        {
            if (!File.Exists(FilePath)) return _cached = new AdapterInstallPaths();

            string json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<AdapterInstallPaths>(json);
            if (parsed == null) return _cached = new AdapterInstallPaths();

            // JsonSerializer 反序列化时会新建 Dictionary，
            // 属性初始化器里的 OrdinalIgnoreCase 比较器不会被保留。
            // 结果：写进去时是忽略大小写的字典，读回来变成区分大小写，
            // 适配包 id 大小写一变就查不到路径（实测踩到）。
            // 这里必须显式重建字典并带上比较器。
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parsed.Paths != null)
            {
                foreach (var kv in parsed.Paths)
                    normalized[kv.Key] = kv.Value;
            }
            parsed.Paths = normalized;

            return _cached = parsed;
        }
        catch
        {
            // 配置坏了就当没有：路径是可重新指定的，不该因此起不来
            return _cached = new AdapterInstallPaths();
        }
    }

    private void Save()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true });

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 写失败不影响当前会话 */ }
    }
}
