using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameTrainer.Adapter;

/// <summary>
/// 适配包（Game Adapter Pack）：一个游戏一份，声明「怎么连」+「哪个语义对应哪个实现」。
///
/// 与旧 profile 的区别：
///   旧 profile 里每个 patch 直接写死实现名（如 heros.vie），换游戏要重写全部字段。
///   适配包把「语义 → 实现」的映射独立出来，主程序只按语义寻址，
///   因此新增游戏只需投一个新 json，不碰主程序代码。
///
/// 目录约定：adapters/&lt;gameId&gt;/adapter.json（可带 README.md 说明探测过程）
/// </summary>
internal sealed class GameAdapter
{
    /// <summary>唯一 id，与目录名一致。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 引擎类型。决定用哪个探测器与执行器：
    ///   construct3_nwjs  Construct 3 导出 + NW.js（CDP + 实例变量）
    ///   electron         Electron（CDP + 页面 JS）
    ///   nwjs            NW.js 原生（CDP + 页面 JS）
    ///   native_memory   原生程序（AOB / 指针链）
    ///   generic_js      其他 CEF/WebView 类
    /// </summary>
    public string Engine { get; set; } = "";

    /// <summary>主进程 exe 名。</summary>
    public string Exe { get; set; } = "";

    /// <summary>安全模式：offline_only | readonly | allow。</summary>
    public string Mode { get; set; } = "offline_only";

    /// <summary>连接配置（端口、标题过滤等）。</summary>
    public ConnectionSpec Connection { get; set; } = new();

    /// <summary>引擎特定的发现配置（如 Construct 的对象类名、IndexedDB 库名）。</summary>
    public EngineSpec EngineOptions { get; set; } = new();

    /// <summary>
    /// 语义映射：语义 → 取值位置。
    /// 这是适配包的核心内容。
    /// </summary>
    public List<ValueBinding> Bindings { get; set; } = new();

    /// <summary>探测过程记录：这个游戏是怎么被破解的。给下一个人/下一个会话看。</summary>
    public ProbeNotes Notes { get; set; } = new();

    public ValueBinding FindBinding(string semantic) =>
        Bindings.FirstOrDefault(b => string.Equals(b.Semantic, semantic, StringComparison.OrdinalIgnoreCase));

    /// <summary>校验适配包完整性。返回错误列表，空表示通过。</summary>
    public List<string> Validate()
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(Id)) errs.Add("id 不能为空");
        if (string.IsNullOrWhiteSpace(Exe)) errs.Add("exe 不能为空");
        if (string.IsNullOrWhiteSpace(Engine)) errs.Add("engine 不能为空");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Bindings)
        {
            if (string.IsNullOrWhiteSpace(b.Semantic)) { errs.Add("binding.semantic 不能为空"); continue; }
            if (!seen.Add(b.Semantic)) errs.Add($"语义重复: {b.Semantic}");
            if (!Semantics.IsKnown(b.Semantic))
                errs.Add($"未知语义 {b.Semantic}（已知: {string.Join(", ", Semantics.All.Keys)}）");

            switch (b.Kind)
            {
                case "instance_var":
                    if (string.IsNullOrWhiteSpace(b.ObjectName) || string.IsNullOrWhiteSpace(b.VarName))
                        errs.Add($"{b.Semantic}: instance_var 需要 objectName + varName");
                    break;
                case "indexeddb_key":
                    if (string.IsNullOrWhiteSpace(b.StorageKey))
                        errs.Add($"{b.Semantic}: indexeddb_key 需要 storageKey");
                    break;
                case "memory_aob":
                    if (string.IsNullOrWhiteSpace(b.Aob))
                        errs.Add($"{b.Semantic}: memory_aob 需要 aob");
                    break;
                default:
                    errs.Add($"{b.Semantic}: 未知 kind {b.Kind}（instance_var|indexeddb_key|memory_aob）");
                    break;
            }

            if (b.Range is { Length: 2 } && b.Range[0] > b.Range[1])
                errs.Add($"{b.Semantic}: range 顺序错误 [{b.Range[0]}, {b.Range[1]}]");
        }
        return errs;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static GameAdapter Load(string path)
    {
        string json = File.ReadAllText(path);
        var a = JsonSerializer.Deserialize<GameAdapter>(json, JsonOpts)
            ?? throw new InvalidOperationException($"适配包解析为空: {path}");

        var errs = a.Validate();
        if (errs.Count > 0)
            throw new InvalidOperationException(
                $"适配包校验失败 ({Path.GetFileName(path)}):\n  - " + string.Join("\n  - ", errs));
        return a;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>连接参数。</summary>
internal sealed class ConnectionSpec
{
    /// <summary>CDP 调试端口（启动参数 --remote-debugging-port）。</summary>
    public int DebugPort { get; set; } = 9222;

    /// <summary>页面标题过滤，多页面时选中游戏页。</summary>
    public string TitleFilter { get; set; }

    /// <summary>
    /// 游戏安装目录。用于 launch 命令定位 exe。
    /// 留空时按 exe 所在目录、已运行进程路径、常见安装盘符依次探测。
    /// </summary>
    public string InstallDir { get; set; }

    /// <summary>启动时自动附加的额外参数。</summary>
    public List<string> LaunchArgs { get; set; } = new();

    /// <summary>启动后等待 CDP 就绪的最长秒数。默认 30。</summary>
    public int LaunchTimeoutSec { get; set; } = 30;
}

/// <summary>引擎特定配置。</summary>
internal sealed class EngineSpec
{
    /// <summary>Construct 3：IndexedDB 库名（c3-localstorage-&lt;projectId&gt;）。</summary>
    public string LocalStorageDb { get; set; }

    /// <summary>Construct 3：object store 名，固定 keyvaluepairs。</summary>
    public string ObjectStore { get; set; } = "keyvaluepairs";

    /// <summary>Construct 3：运行时入口表达式（默认已适配）。</summary>
    public string RuntimePath { get; set; } = "self.c3_runtimeInterface._localRuntime";

    /// <summary>旧版存档（System Save slot）库名，通常不存在。</summary>
    public string SavegamesDb { get; set; }
}

/// <summary>
/// 语义到实现的映射。
/// </summary>
internal sealed class ValueBinding
{
    /// <summary>语义名，见 Semantics。</summary>
    public string Semantic { get; set; } = "";

    /// <summary>实现类型：instance_var | indexeddb_key | memory_aob。</summary>
    public string Kind { get; set; } = "";

    /// <summary>instance_var：对象类名。</summary>
    public string ObjectName { get; set; }

    /// <summary>instance_var：变量名。</summary>
    public string VarName { get; set; }

    /// <summary>indexeddb_key：键名。</summary>
    public string StorageKey { get; set; }

    /// <summary>memory_aob：AOB 签名。</summary>
    public string Aob { get; set; }

    /// <summary>memory_aob：偏移列表。</summary>
    public List<string> Offsets { get; set; } = new();

    /// <summary>数值类型：number | string。默认 number。</summary>
    public string ValueType { get; set; } = "number";

    /// <summary>允许区间。</summary>
    public double[] Range { get; set; }

    /// <summary>是否实时生效（实例变量为 true，IndexedDB 为 false 需重载）。</summary>
    public bool Realtime { get; set; }

    /// <summary>备注。</summary>
    public string Note { get; set; }

    /// <summary>人类可读的取值位置描述，用于报告。</summary>
    public string Describe() => Kind switch
    {
        "instance_var" => $"{ObjectName}.{VarName}",
        "indexeddb_key" => $"IndexedDB[{StorageKey}]",
        "memory_aob" => $"AOB({Aob})",
        _ => Kind
    };
}

/// <summary>
/// 探测笔记：记录这个游戏是如何被破解的。
/// 目的：让下一个人（或下一个 AI 会话）能复用同一条路径，而不是从零再试。
/// </summary>
internal sealed class ProbeNotes
{
    /// <summary>一句话说明游戏技术栈。</summary>
    public string Stack { get; set; }

    /// <summary>逐步探测过程，按时间顺序。</summary>
    public List<string> Steps { get; set; } = new();

    /// <summary>踩过的坑，避免重复。</summary>
    public List<string> Pitfalls { get; set; } = new();

    /// <summary>关键发现（DOM 路径、运行时入口、存储位置等）。</summary>
    public List<string> KeyFindings { get; set; } = new();

    /// <summary>探测日期。</summary>
    public string ProbedAt { get; set; }
}
