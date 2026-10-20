using System.Text.Json;
using System.Text.Json.Serialization;
using GameTrainer.Memory;

namespace GameTrainer.Profiles;

/// <summary>
/// 一个游戏的修改 profile。这是"内存映射表"的落地形式——
/// 换游戏只需换一个 json，程序本身不改。
/// </summary>
internal sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";

    /// <summary>
    /// offline_only：检测到反作弊即拒绝；
    /// readonly：永远只读（仅扫描查看）；
    /// allow：不检查反作弊（仅限确认无联网/无反作弊的单机）。
    /// </summary>
    public string Mode { get; set; } = "offline_only";

    public AntiCheatSpec Anticheat { get; set; } = new();
    public List<PatchDef> Patches { get; set; } = new();
    public RollbackSpec Rollback { get; set; } = new();

    /// <summary>存档相关配置（可选）。</summary>
    public SaveSpec Save { get; set; }
}

internal sealed class AntiCheatSpec
{
    /// <summary>额外需要检查的模块名。</summary>
    public List<string> Modules { get; set; } = new();
}

internal sealed class RollbackSpec
{
    /// <summary>单次操作最大写入字节数，超过需显式放宽。</summary>
    public int MaxBytes { get; set; } = 64;
    /// <summary>启用前是否强制先做一次快照。</summary>
    public bool RequireSnapshot { get; set; } = true;
}

internal sealed class PatchDef
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "memory"; // memory | save

    /// <summary>true = 持续锁定（定时重写）；false = 一次性写入。</summary>
    public bool Freeze { get; set; }

    /// <summary>锁定间隔毫秒（Freeze=true 时生效）。</summary>
    public int FreezeIntervalMs { get; set; } = 200;

    public List<WriteDef> Writes { get; set; } = new();
    public SaveWriteDef SaveWrite { get; set; }
}

internal sealed class WriteDef
{
    /// <summary>模块名，例如 "love.dll"。留空表示不按模块限定。</summary>
    public string Module { get; set; }

    /// <summary>AOB 签名（支持 ?? 通配）。二选一：Aob 或 Chain。</summary>
    public string Aob { get; set; }

    /// <summary>相对命中位置的偏移，例如 ["+0x18", "+0x8"]。</summary>
    public List<string> Offsets { get; set; } = new();

    /// <summary>指针链（模块内静态偏移 + 多级偏移），例如 ["0x1A2B3C", "0x18", "0x8"]。</summary>
    public List<string> Chain { get; set; }

    public string Type { get; set; } = "double";
    public string Value { get; set; }

    /// <summary>写入前必须匹配的前值；null 表示不校验。</summary>
    public string ExpectBefore { get; set; }

    /// <summary>允许的数值区间 [min,max]，越界拒绝写入。</summary>
    public double[] Range { get; set; }

    /// <summary>签名必须唯一命中，默认 true。</summary>
    public bool RequireUnique { get; set; } = true;

    public NumType NumType => Enum.Parse<NumType>(Type, ignoreCase: true);
}

internal sealed class SaveSpec
{
    /// <summary>存档目录或文件。支持环境变量展开，例如 "%LOCALAPPDATA%\SolCesto"。危险操作前必先备份。</summary>
    public string Path { get; set; }
    /// <summary>备份根目录（相对 profile 目录）。</summary>
    public string BackupDir { get; set; } = "snapshots";
    /// <summary>backup_only：只备份/恢复，不编辑内容。</summary>
    public string EditMode { get; set; } = "backup_only";
}

internal sealed class SaveWriteDef
{
    public string File { get; set; }
    /// <summary>解压方式：none | deflate | zlib。</summary>
    public string Compression { get; set; } = "none";
    public List<SaveEdit> Edits { get; set; } = new();
}

internal sealed class SaveEdit
{
    /// <summary>仅在文本存档下有效：正则定位 + 替换。</summary>
    public string Pattern { get; set; }
    public string Replacement { get; set; }
}

internal static class ProfileLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static GameProfile Load(string path)
    {
        string json = File.ReadAllText(path);
        var p = JsonSerializer.Deserialize<GameProfile>(json, Options)
            ?? throw new InvalidOperationException($"profile 解析为空: {path}");
        Validate(p, path);
        return p;
    }

    public static void Validate(GameProfile p, string path)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Id)) errs.Add("id 不能为空");
        if (string.IsNullOrWhiteSpace(p.Exe)) errs.Add("exe 不能为空");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in p.Patches)
        {
            if (string.IsNullOrWhiteSpace(patch.Id)) { errs.Add("patch.id 不能为空"); continue; }
            if (!ids.Add(patch.Id)) errs.Add($"patch.id 重复: {patch.Id}");

            if (patch.Kind == "memory")
            {
                if (patch.Writes.Count == 0) errs.Add($"patch {patch.Id}: writes 为空");
                foreach (var w in patch.Writes)
                {
                    bool hasAob = !string.IsNullOrWhiteSpace(w.Aob);
                    bool hasChain = w.Chain is { Count: > 0 };
                    if (!hasAob && !hasChain)
                        errs.Add($"patch {patch.Id}: 必须提供 aob 或 chain");
                    if (hasAob && hasChain)
                        errs.Add($"patch {patch.Id}: aob 与 chain 只能二选一");
                    if (string.IsNullOrWhiteSpace(w.Value))
                        errs.Add($"patch {patch.Id}: value 不能为空");
                    if (!Enum.TryParse<NumType>(w.Type, true, out _))
                        errs.Add($"patch {patch.Id}: 未知 type {w.Type}");
                    if (hasAob)
                    {
                        try { _ = new AobPattern(w.Aob); }
                        catch (Exception ex) { errs.Add($"patch {patch.Id}: AOB 无效 — {ex.Message}"); }
                    }
                }
            }
            else if (patch.Kind == "save")
            {
                if (patch.SaveWrite == null) errs.Add($"patch {patch.Id}: kind=save 需要 saveWrite");
            }
            else
            {
                errs.Add($"patch {patch.Id}: 未知 kind {patch.Kind}（仅支持 memory/save）");
            }
        }

        if (errs.Count > 0)
            throw new InvalidOperationException(
                $"profile 校验失败 ({System.IO.Path.GetFileName(path)}):\n  - " + string.Join("\n  - ", errs));
    }
}
