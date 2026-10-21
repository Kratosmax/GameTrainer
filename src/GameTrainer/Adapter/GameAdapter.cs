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

    /// <summary>
    /// 额外需要检查的反作弊模块名。
    /// 命中即整体只读，不做任何绕过（与 profile 的 anticheat.modules 同义）。
    /// </summary>
    public List<string> AntiCheatModules { get; set; } = new();

    /// <summary>连接配置（端口、标题过滤等）。</summary>
    public ConnectionSpec Connection { get; set; } = new();

    /// <summary>引擎特定的发现配置（如 Construct 的对象类名、IndexedDB 库名）。</summary>
    public EngineSpec EngineOptions { get; set; } = new();

    /// <summary>
    /// 语义映射：语义 → 取值位置。
    /// 这是适配包的核心内容。
    /// </summary>
    public List<ValueBinding> Bindings { get; set; } = new();

    /// <summary>
    /// 一键预设：把一组常用数值打包，一条命令全部应用。
    ///
    /// 用途：有些值每局都会被游戏重置（例如角色初始血量上限来自数据表），
    /// 那么「每局开局改一次」就该是一条命令，而不是逐项敲。
    /// </summary>
    public List<PresetSpec> Presets { get; set; } = new();

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

            // 集合语义（memory_collection）覆盖的是「一组条目」，不属于固定单值语义表，
            // 允许自定义名字（如 employee_good）。其它 kind 仍要求语义在白名单内。
            bool isCollectionKind = string.Equals(b.Kind, "memory_collection", StringComparison.OrdinalIgnoreCase);
            if (!isCollectionKind && !Semantics.IsKnown(b.Semantic))
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
                case "memory_collection":
                    // 集合绑定必须能定位主体、能定位字段、能区分存档副本。
                    // anchorIds 是最关键的一条：内存里常有多个结构相同的 Userdata 副本，
                    // 没有锚点就只能靠猜（实测 244 个候选都无法可靠区分）。
                    if (string.IsNullOrWhiteSpace(b.DictOffset))
                        errs.Add($"{b.Semantic}: memory_collection 需要 dictOffset");
                    if (string.IsNullOrWhiteSpace(b.FieldOffset))
                        errs.Add($"{b.Semantic}: memory_collection 需要 fieldOffset");
                    if (b.AnchorIds == null || b.AnchorIds.Count == 0)
                        errs.Add($"{b.Semantic}: memory_collection 需要至少一个 anchorIds（用于定位存档副本）");
                    break;
                case "memory_aob":
                    // aob 与 chain 二选一，不能都给（给了也没意义，语义冲突）
                    bool hasAob = !string.IsNullOrWhiteSpace(b.Aob);
                    bool hasChain = b.Chain is { Count: > 0 };
                    if (!hasAob && !hasChain)
                        errs.Add($"{b.Semantic}: memory_aob 需要 aob 或 chain（二选一）");
                    else if (hasAob && hasChain)
                        errs.Add($"{b.Semantic}: aob 与 chain 只能给一个");
                    // chain 可以基于模块基址，也可以用绝对地址（module 留空，
                    // 此时 chain[0] 直接是起始地址）。后者的典型场景是
                    // 引擎的固定内存池，不依附于任何模块。
                    if (hasChain && b.Chain.Count < 1)
                        errs.Add($"{b.Semantic}: chain 至少需要一级");
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

/// <summary>
/// 一键预设：一组「语义 → 目标值」，一次应用完毕。
///
/// 为什么需要它：部分数值每局会被游戏重置（如角色初始血量上限来自数据表），
/// 那就得每局改一次。逐项敲三条命令太烦，打包成 preset 后只需一条。
/// </summary>
internal sealed class PresetSpec
{
    /// <summary>预设 id，命令行引用。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Label { get; set; } = "";

    /// <summary>说明：这个预设会改什么、适用场景。</summary>
    public string Description { get; set; } = "";

    /// <summary>语义 → 目标值。值统一用字符串，按 binding 的 valueType 解析。</summary>
    public Dictionary<string, string> Values { get; set; } = new();
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

    /// <summary>
    /// 视为「对局进行中」的场景名白名单。
    /// 用途：持久化监控（如持久套盾）只在局内生效，退出对局自动解除。
    /// 留空时退化为「实例存在即视为局内」。
    /// </summary>
    public List<string> InGameLayouts { get; set; } = new();
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

    /// <summary>memory_aob：限定扫描的模块名（可选）。</summary>
    public string Module { get; set; }

    /// <summary>memory_aob：数值类型 byte/short/int/long/float/double。默认 double。</summary>
    public string Type { get; set; } = "double";

    /// <summary>memory_aob：偏移列表（aob 命中后叠加）。</summary>
    public List<string> Offsets { get; set; } = new();

    /// <summary>
    /// memory_aob：指针链。与 aob 二选一。
    ///
    /// 语义：Module 基址 + chain[0] → 解引用 → + chain[1] → … → 终点。
    /// 最后一级偏移后不再解引用，直接作为目标地址。
    ///
    /// 比 AOB 稳定的场景：游戏更新后代码变了但数据结构没变，
    /// 基址 + 偏移依然有效，而字节签名会失效。
    /// </summary>
    public List<string> Chain { get; set; }

    /// <summary>数值类型：number | string。默认 number。</summary>
    public string ValueType { get; set; } = "number";

    // ---- memory_collection 专用（集合语义：一次绑定覆盖字典里的全部条目）----

    /// <summary>主体对象里字典字段的偏移，如 "0x88"。</summary>
    public string DictOffset { get; set; }

    /// <summary>主体对象里等级字段的偏移，如 "0x2C"。</summary>
    public string LevelOffset { get; set; }

    /// <summary>等级最小值（含）。</summary>
    public int MinLevel { get; set; } = 1;

    /// <summary>等级最大值（含）。</summary>
    public int MaxLevel { get; set; } = 99;

    /// <summary>主体对象里资产字段的偏移，如 "0x40"。</summary>
    public string GoldOffset { get; set; }

    /// <summary>资产上限，超过视为异常数据。</summary>
    public long MaxGold { get; set; } = 100_000_000;

    /// <summary>锚点条目 ID：真实存档必然包含这些 ID，用于区分内存里的其它副本。</summary>
    public List<int> AnchorIds { get; set; } = new();

    /// <summary>目标字段在条目对象里的偏移，如好感度 "0x18"。</summary>
    public string FieldOffset { get; set; }

    /// <summary>
    /// true = fieldOffset 相对主体对象而非条目对象。
    /// 用于总资产这类单值字段（Userdata+0x40）：它们借用同一套存档定位逻辑，
    /// 但不是集合里的元素。
    /// </summary>
    public bool OwnerField { get; set; }

    // ---- memory_collection 二级查找（字段在按 ID 关联的静态表上）----

    /// <summary>静态表里目标字段的偏移。留空表示字段就在条目对象上。</summary>
    public string StaticFieldOffset { get; set; }

    /// <summary>静态表里 ID 字段的偏移。</summary>
    public string StaticIdOffset { get; set; } = "0x10";

    /// <summary>静态表里名字索引字段的偏移（如 ename = 0x18）。0 表示没有。</summary>
    public string StaticNameIndexOffset { get; set; }

    /// <summary>静态表里年龄字段的偏移，用于排除误命中。</summary>
    public string StaticAgeOffset { get; set; }

    /// <summary>
    /// 静态表里类型字段的偏移（Dictemployee +0x14 = employeetype）。
    /// 必须配：不加这一项会命中内存里其它含相同 ID 的对象，名字和数值全错
    /// （实测 1001 会显示成别人的名字）。
    /// </summary>
    public string StaticTypeOffset { get; set; }

    /// <summary>静态字段值的合理范围。</summary>
    public int StaticMinValue { get; set; }
    public int StaticMaxValue { get; set; } = 9999;

    // ---- 名字表（把名字索引映射成显示名）----

    /// <summary>名字表 entries 的地址（十六进制）。0 表示不解析名字。</summary>
    public string NameTableEntries { get; set; }

    /// <summary>名字表 entry 步长。</summary>
    public string NameEntryStride { get; set; } = "0x18";

    /// <summary>名字表 entry 里 key 的偏移。</summary>
    public string NameKeyOffset { get; set; } = "0x08";

    /// <summary>名字表 entry 里 value 指针的偏移。</summary>
    public string NameValueOffset { get; set; } = "0x10";

    /// <summary>扫描多少个名字条目。</summary>
    public int NameScanCount { get; set; } = 500;

    /// <summary>条目 ID 字段的偏移。</summary>
    public string EntryIdOffset { get; set; } = "0x10";

    /// <summary>条目 ID 的合理范围，用来排除内存垃圾。</summary>
    public int MinEntryId { get; set; } = 1000;
    public int MaxEntryId { get; set; } = 9999;

    /// <summary>字典 entry 步长（实测 0x18）。</summary>
    public string EntryStride { get; set; } = "0x18";

    /// <summary>字典里 value 指针的偏移。</summary>
    public string EntryValueOffset { get; set; } = "0x10";

    /// <summary>字典 count 字段偏移。</summary>
    public string DictCountOffset { get; set; } = "0x20";

    /// <summary>字典 entries 指针偏移。</summary>
    public string DictEntriesOffset { get; set; } = "0x18";

    /// <summary>允许区间。</summary>
    public double[] Range { get; set; }

    /// <summary>是否实时生效（实例变量为 true，IndexedDB 为 false 需重载）。</summary>
    public bool Realtime { get; set; }

    /// <summary>备注。</summary>
    public string Note { get; set; }

    /// <summary>
    /// 界面交互样式。决定 GUI 上这个值用哪种控件：
    ///   fill      主按钮「回满」+ 折叠手动设置   —— 血量、能量累积
    ///   step      增减按钮 + 折叠手动设置        —— 攻击、魔法、能量、背包
    ///   number    输入框 + 应用按钮              —— 金币
    ///   toggle    开关（设置/取消）              —— 护盾
    /// 缺省按 number 处理。
    /// </summary>
    public string Style { get; set; } = "number";

    /// <summary>
    /// style=step 时每次增减的步长。默认 1。
    /// </summary>
    public double Step { get; set; } = 1;

    /// <summary>
    /// 「回满」时的目标值从哪个语义取（如 health 回满取 health_max）。
    /// 留空时使用该 binding 自己的 range 上限或当前会话最大值。
    /// </summary>
    public string FillFrom { get; set; }

    /// <summary>
    /// style=toggle 时「开启」要写入的值。默认 1。
    /// </summary>
    public double OnValue { get; set; } = 1;

    /// <summary>
    /// 是否支持「持久」模式（仅 toggle 有意义）。
    /// 开启后监视器会在值丢失时自动补回，退出对局自动停止。
    /// </summary>
    public bool AllowPersist { get; set; }

    /// <summary>界面上的警告文字（如「测试项」）。</summary>
    public string Warning { get; set; }

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
