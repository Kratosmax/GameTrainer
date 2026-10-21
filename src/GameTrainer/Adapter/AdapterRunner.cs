using System.Globalization;
using System.Text.Json;

namespace GameTrainer.Adapter;

/// <summary>
/// 适配包执行器：按**语义**读写数值，不关心底层是哪个游戏、哪个变量名。
///
/// 所有引擎差异收敛在这里：
///   instance_var   → Construct3/CEF 的 SetInstanceVariableValue（实时生效）
///   indexeddb_key  → IndexedDB put（需重载页面）
///   memory_aob     → 委托给 PatchEngine（TODO：后续接入）
/// </summary>
internal static class AdapterRunner
{
    public sealed record ReadResult(string Semantic, bool Ok, string Value, string Where, string Error)
{
    /// <summary>
    /// 集合语义的结果明细。每条是「条目ID → 名称 → 值」。
    /// 为空表示这是普通单值绑定。
    /// </summary>
    public List<CollectionItem> Items { get; init; }
}

/// <summary>集合里的一个条目。</summary>
public sealed record CollectionItem(int Id, string Name, string Value);
    public sealed record WriteResult(string Semantic, bool Ok, string Before, string After, bool Realtime, string Error);

    /// <summary>
    /// 一次性读出全部绑定值（GUI 刷新用）。
    ///
    /// 与 Read 的区别：把所有 instance_var 合并成**一个** JS 脚本执行，
    /// IndexedDB 部分再单独一次。11 个值从 11 次往返降到 2 次。
    /// </summary>
    public static Dictionary<string, ReadResult> ReadAll(GameAdapter adapter)
    {
        var map = new Dictionary<string, ReadResult>(StringComparer.OrdinalIgnoreCase);

        // memory_aob 不依赖 CDP：单独走内存后端。
        var memBindings = adapter.Bindings.Where(b => b.Kind == "memory_aob").ToList();
        foreach (var b in memBindings)
            map[b.Semantic] = MemoryBackend.Read(adapter, b);

        // memory_collection 同样不依赖 CDP，走集合后端。
        // 必须在这里处理：否则它会被归入 webBindings，
        // 再被下面的「引擎不是 construct3」判定整体拒绝
        // （GUI 曾因此把全部集合语义显示成「读取尚未实现」）。
        var colBindings = adapter.Bindings.Where(b => b.Kind == "memory_collection").ToList();
        foreach (var b in colBindings)
            map[b.Semantic] = CollectionRead(adapter, b);

        var webBindings = adapter.Bindings
            .Where(b => b.Kind != "memory_aob" && b.Kind != "memory_collection")
            .ToList();
        if (webBindings.Count == 0) return map;

        if (adapter.Engine != "construct3")
        {
            foreach (var b in webBindings)
                map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), $"引擎 {adapter.Engine} 的读取尚未实现");
            return map;
        }

        var instBindings = webBindings.Where(b => b.Kind == "instance_var").ToList();
        var idbBindings = webBindings.Where(b => b.Kind == "indexeddb_key").ToList();

        using var cdp = CdpClient.Connect(adapter.Connection.DebugPort, adapter.Connection.TitleFilter);

        // ---- 一轮取完所有实例变量 ----
        if (instBindings.Count > 0)
        {
            string rt = adapter.EngineOptions.RuntimePath;

            // RuntimePath 是表达式不是数据，必须白名单校验后才允许插值。
            // 读路径同样要拦：投毒的适配包只要被 read 一次就能执行任意 JS
            // （实测：sem <游戏> read 即触发，不需要写入）。
            if (!IsValidPropertyChain(rt))
            {
                foreach (var b in instBindings)
                    map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(),
                        $"RuntimePath \"{rt}\" 不是合法的属性链表达式，已拒绝执行。");
                instBindings.Clear();
            }
        }

        if (instBindings.Count > 0)
        {
            string rt = adapter.EngineOptions.RuntimePath;
            // 按对象名分组，减少查找次数
            var groups = instBindings.GroupBy(b => b.ObjectName);
            var reqs = groups.Select(g => new
            {
                obj = g.Key,
                vars = g.Select(b => b.VarName).ToArray()
            });

            string script = $$"""
(() => {
  const RT = {{SafeRuntimeExpr(rt)}};
  const REQS = {{JsonSerializer.Serialize(reqs)}};
  try {
    if (!RT) return JSON.stringify({ error: '未找到运行时' });
    const classes = [...RT.GetAllObjectClasses()];
    const out = {};
    for (const req of REQS) {
      const cls = classes.find(c => c.GetName() === req.obj);
      if (!cls) { for (const v of req.vars) out[req.obj + '.' + v] = { error: '对象不存在' }; continue; }
      const insts = [...cls.GetInstances()];
      if (insts.length === 0) { for (const v of req.vars) out[req.obj + '.' + v] = { error: '无实例' }; continue; }
      const inst = insts[0];
      const names = inst._GetAllInstanceVariableNames();
      for (const v of req.vars) {
        const i = names.indexOf(v);
        if (i < 0) { out[req.obj + '.' + v] = { error: '变量不存在' }; continue; }
        let val = inst.GetInstanceVariableValue(i);
        if (typeof val === 'number' && Number.isNaN(val)) val = 'NaN';
        out[req.obj + '.' + v] = { value: val };
      }
    }
    return JSON.stringify(out);
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";
            try
            {
                string raw = cdp.Evaluate(script, timeoutMs: 20000);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var gerr))
                {
                    foreach (var b in instBindings)
                        map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), gerr.GetString());
                }
                else
                {
                    foreach (var b in instBindings)
                    {
                        string key = b.ObjectName + "." + b.VarName;
                        if (root.TryGetProperty(key, out var cell))
                        {
                            if (cell.TryGetProperty("error", out var e2))
                                map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), e2.GetString());
                            else
                                map[b.Semantic] = new ReadResult(b.Semantic, true,
                                    cell.GetProperty("value").GetRawText(), b.Describe(), null);
                        }
                        else
                        {
                            map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), "结果缺失");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var b in instBindings)
                    map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), ex.Message);
            }
        }

        // ---- IndexedDB 部分：一轮取完所有键 ----
        if (idbBindings.Count > 0)
        {
            string db = adapter.EngineOptions.LocalStorageDb;
            string store = string.IsNullOrWhiteSpace(adapter.EngineOptions.ObjectStore)
                ? "keyvaluepairs" : adapter.EngineOptions.ObjectStore;
            string script = $$"""
(async () => {
  const DB = {{JsonSerializer.Serialize(db)}};
  const STORE = {{JsonSerializer.Serialize(store)}};
  const KEYS = {{JsonSerializer.Serialize(idbBindings.Select(b => b.StorageKey))}};
  try {
    const d = await new Promise((res, rej) => {
      const q = indexedDB.open(DB);
      q.onsuccess = () => res(q.result);
      q.onerror = () => rej(q.error);
    });
    const out = {};
    for (const k of KEYS) {
      try {
        const v = await new Promise((res, rej) => {
          const tx = d.transaction(STORE, 'readonly');
          const q = tx.objectStore(STORE).get(k);
          tx.oncomplete = () => res(q.result);
          tx.onerror = () => rej(tx.error);
        });
        out[k] = { value: (v === undefined ? null : v) };
      } catch (e) { out[k] = { error: String(e && e.message || e) }; }
    }
    d.close();
    return JSON.stringify(out);
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";
            try
            {
                string raw = cdp.Evaluate(script, awaitPromise: true, timeoutMs: 20000);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                foreach (var b in idbBindings)
                {
                    if (root.TryGetProperty(b.StorageKey, out var cell))
                    {
                        if (cell.TryGetProperty("error", out var e2))
                            map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), e2.GetString());
                        else
                            map[b.Semantic] = new ReadResult(b.Semantic, true,
                                cell.GetProperty("value").GetRawText(), b.Describe(), null);
                    }
                    else
                    {
                        map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), "结果缺失");
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var b in idbBindings)
                    map[b.Semantic] = new ReadResult(b.Semantic, false, null, b.Describe(), ex.Message);
            }
        }

        return map;
    }

    /// <summary>连接一次，批量读取多个语义。</summary>
    public static List<ReadResult> Read(GameAdapter adapter, IEnumerable<string> semantics)
    {
        // 两类内存型绑定都不需要 CDP：游戏可能根本没开调试端口，不能因此失败。
        // memory_aob 与 memory_collection 都直接读写进程内存。
        static bool NeedsCdp(ValueBinding b) =>
            b != null && b.Kind is not ("memory_aob" or "memory_collection");

        if (!semantics.Any(s => NeedsCdp(adapter.FindBinding(s))))
            return ReadOn(null, adapter, semantics);

        using var cdp = CdpClient.Connect(adapter.Connection.DebugPort, adapter.Connection.TitleFilter);
        return ReadOn(cdp, adapter, semantics);
    }

    /// <summary>
    /// 在给定连接上批量读取；cdp 为 null 时只处理 memory_aob 绑定。
    /// PersistentWatcher 传入自己复用的连接；不接管传入连接的生命周期。
    /// </summary>
    public static List<ReadResult> ReadOn(CdpClient cdp, GameAdapter adapter, IEnumerable<string> semantics)
    {
        var results = new List<ReadResult>();
        var wanted = semantics.ToList();

        // memory_aob 单独处理，不要求 CDP 通道
        var memWanted = new List<string>();
        foreach (string s in wanted)
        {
            var mb = adapter.FindBinding(s);
            if (mb?.Kind == "memory_aob")
            {
                results.Add(MemoryBackend.Read(adapter, mb));
                memWanted.Add(s);
            }
            else if (mb?.Kind == "memory_collection")
            {
                results.Add(CollectionRead(adapter, mb));
                memWanted.Add(s);
            }
        }
        wanted = wanted.Except(memWanted).ToList();
        if (wanted.Count == 0) return results;

        if (adapter.Engine != "construct3")
        {
            foreach (string s in wanted)
                results.Add(new ReadResult(s, false, null, "-", $"引擎 {adapter.Engine} 的读取尚未实现"));
            return results;
        }

        var rt = adapter.EngineOptions.RuntimePath;

        // 遍历 wanted 而非原始 semantics：memory_aob 已在上方读过一遍，
        // 遍历原集合会让 memory 语义在结果里出现两次
        foreach (string sem in wanted)
        {
            var b = adapter.FindBinding(sem);
            if (b == null)
            {
                results.Add(new ReadResult(sem, false, null, "-", "适配包没有该语义的绑定"));
                continue;
            }

            if (b.Kind == "instance_var")
            {
                // 与 Write 路径同等的白名单校验：读也要拦，否则投毒适配包
                // 只需被 read 一次即可在游戏进程执行任意 JS。
                if (!IsValidPropertyChain(rt))
                {
                    results.Add(new ReadResult(sem, false, null, b.Describe(),
                        $"RuntimePath \"{rt}\" 不是合法的属性链表达式，已拒绝执行。"));
                    continue;
                }

                string script = BuildReadInstanceVar(rt, b.ObjectName, b.VarName);
                try
                {
                    string raw = cdp.Evaluate(script, timeoutMs: 15000);
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err))
                        results.Add(new ReadResult(sem, false, null, b.Describe(), err.GetString()));
                    else
                        results.Add(new ReadResult(sem, true, root.GetProperty("value").GetRawText(), b.Describe(), null));
                }
                catch (Exception ex)
                {
                    results.Add(new ReadResult(sem, false, null, b.Describe(), ex.Message));
                }
            }
            else if (b.Kind == "indexeddb_key")
            {
                string db = adapter.EngineOptions.LocalStorageDb;
                string store = adapter.EngineOptions.ObjectStore;
                string script = BuildReadIdbKey(db, store, b.StorageKey);
                try
                {
                    string raw = cdp.Evaluate(script, awaitPromise: true, timeoutMs: 15000);
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err))
                        results.Add(new ReadResult(sem, false, null, b.Describe(), err.GetString()));
                    else
                        results.Add(new ReadResult(sem, true, root.GetProperty("value").GetRawText(), b.Describe(), null));
                }
                catch (Exception ex)
                {
                    results.Add(new ReadResult(sem, false, null, b.Describe(), ex.Message));
                }
            }
            else if (b.Kind == "memory_aob")
            {
                results.Add(MemoryBackend.Read(adapter, b));
            }
            else
            {
                results.Add(new ReadResult(sem, false, null, b.Describe(), $"kind {b.Kind} 暂不支持读取"));
            }
        }
        return results;
    }

    /// <summary>按语义写值。dryRun 时只读前值不写。</summary>
        // ---------------- 集合语义（memory_collection） ----------------
    //
    // 一次绑定覆盖「一个字典里的全部条目」，用于员工列表这类会增减的数据。
    // 每次操作都现场扫描定位，不缓存地址——读档或重开游戏后地址必变。

    private static long ParseOff(string s)
        => long.TryParse((s ?? "").Replace("0x", "").Replace("0X", ""),
               System.Globalization.NumberStyles.HexNumber, null, out long v) ? v : -1;

    private static CollectionBackend.Locator BuildLocator(ValueBinding b) => new()
    {
        DictOffset = ParseOff(b.DictOffset),
        LevelOffset = ParseOff(b.LevelOffset),
        MinLevel = b.MinLevel,
        MaxLevel = b.MaxLevel,
        GoldOffset = ParseOff(b.GoldOffset),
        MaxGold = b.MaxGold,
        AnchorIds = b.AnchorIds ?? new List<int>(),
        EntryIdOffset = ParseOff(b.EntryIdOffset) >= 0 ? ParseOff(b.EntryIdOffset) : 0x10,
        MinEntryId = b.MinEntryId,
        MaxEntryId = b.MaxEntryId,
        EntryStride = ParseOff(b.EntryStride) >= 0 ? ParseOff(b.EntryStride) : 0x18,
        EntryValueOffset = ParseOff(b.EntryValueOffset) >= 0 ? ParseOff(b.EntryValueOffset) : 0x10,
        DictCountOffset = ParseOff(b.DictCountOffset) >= 0 ? ParseOff(b.DictCountOffset) : 0x20,
        DictEntriesOffset = ParseOff(b.DictEntriesOffset) >= 0 ? ParseOff(b.DictEntriesOffset) : 0x18,
        Static = BuildStaticLookup(b),
        Names = BuildNameTable(b)
    };

    /// <summary>
    /// 二级查找参数（可选）。配了就把字段读写转到按 ID 关联的静态表上。
    /// 典型场景：医术在 Dictemployee+0x30，不在员工实例对象上。
    /// </summary>
    private static CollectionBackend.StaticLookup BuildStaticLookup(ValueBinding b)
    {
        long field = ParseOff(b.StaticFieldOffset);

        // 即使没有 staticFieldOffset（字段就在条目对象上，如好感度），
        // 只要配了 staticNameIndexOffset 就仍需一个 StaticLookup——
        // 名字是通过「条目ID -> 静态表 ename -> 名字表」查出来的。
        // 之前这里直接 return null，导致好感度/猥琐值只显示 #ID。
        long nameIdx = ParseOff(b.StaticNameIndexOffset);
        if (field < 0 && (nameIdx <= 0 || ParseOff(b.NameTableEntries) <= 0))
            return null;

        return new CollectionBackend.StaticLookup
        {
            IdOffset = ParseOff(b.StaticIdOffset) >= 0 ? ParseOff(b.StaticIdOffset) : 0x10,
            FieldOffset = field > 0 ? field : -1,
            MinValue = b.StaticMinValue,
            MaxValue = b.StaticMaxValue > 0 ? b.StaticMaxValue : 9999,
            NameIndexOffset = nameIdx,
            AgeOffset = ParseOff(b.StaticAgeOffset),
            TypeOffset = ParseOff(b.StaticTypeOffset)
        };
    }

    /// <summary>名字表参数（可选）。没配就显示 #ID。</summary>
    private static CollectionBackend.NameTable BuildNameTable(ValueBinding b)
    {
        long entries = ParseOff(b.NameTableEntries);
        if (entries <= 0) return null;

        return new CollectionBackend.NameTable
        {
            EntriesAddress = entries,
            EntryStride = ParseOff(b.NameEntryStride) >= 0 ? ParseOff(b.NameEntryStride) : 0x18,
            KeyOffset = ParseOff(b.NameKeyOffset) >= 0 ? ParseOff(b.NameKeyOffset) : 0x08,
            ValueOffset = ParseOff(b.NameValueOffset) >= 0 ? ParseOff(b.NameValueOffset) : 0x10,
            ScanCount = b.NameScanCount > 0 ? b.NameScanCount : 500
        };
    }

    private static ReadResult CollectionRead(GameAdapter adapter, ValueBinding b)
    {
        string where = b.Describe();
        long fieldOff = ParseOff(b.FieldOffset);
        if (fieldOff < 0)
            return new ReadResult(b.Semantic, false, null, where, "集合绑定缺少 fieldOffset");

        var proc = FindTargetProcess(adapter);
        if (proc == null)
            return new ReadResult(b.Semantic, false, null, where, $"找不到进程 {adapter.Exe}（游戏是否在运行？）");

        using var session = CollectionBackend.Attach(proc.Id, needWrite: false);
        if (!session.Ok)
            return new ReadResult(b.Semantic, false, null, where, session.Error);

        // 句柄打开成功不代表能读：权限不足时 OpenProcess 也返回有效句柄，
        // 后续扫描会静默扫不到任何东西，表现为"未找到存档"这种误导性报错。
        if (!CollectionBackend.CanRead(session.Handle))
        {
            long reg = CollectionBackend.FirstReadableRegion(session.Handle);
            return new ReadResult(b.Semantic, false, null, where,
                $"进程 {proc.ProcessName}({proc.Id}) 句柄打开成功但读不到内存。" +
                $"首个可读区=0x{reg:X}" + (reg == 0 ? "（VirtualQueryEx 扫描未启动）" : ""));
        }

        // 走定位缓存：首次或读档后才全量扫描，其余操作直接用缓存地址。
        // GUI 逐张卡片刷新时，若不缓存会每张卡都扫 2 秒，界面直接卡死。
        var snap = SaveLocator.Resolve(session.Handle, BuildLocator(b), out string err);
        if (snap == null)
            return new ReadResult(b.Semantic, false, null, where, err);

        var located = CollectionBackend.Reentries(session.Handle, snap.OwnerAddress,
                                                  BuildLocator(b), out string rerr);
        if (located == null)
            return new ReadResult(b.Semantic, false, null, where, rerr);

        // 单值模式：fieldOffset 相对主体对象（如总资产 Userdata+0x40）。
        // 集合模式：fieldOffset 相对每个条目对象。
        if (b.OwnerField)
        {
            if (!CollectionBackend.TryReadEntry(session.Handle, located.OwnerAddress, fieldOff, out int ov))
                return new ReadResult(b.Semantic, false, null, where, "读取主体字段失败");
            return new ReadResult(b.Semantic, true, ov.ToString(), where + " · 主体字段", null);
        }

        var entries = located.Entries;

        var items = new List<CollectionItem>();
        var locator = BuildLocator(b);
        var stat = locator.Static;

        foreach (var e in entries)
        {
            // 数值来源：
            //   FieldOffset > 0 → 借静态表按 ID 反查（如医术在 Dictemployee+0x30）
            //   FieldOffset < 0 → 数值就在条目对象上（好感度/猥琐值），
            //                     静态表只用来查名字
            long sourceAddr = e.Address;
            if (stat != null && stat.FieldOffset > 0)
            {
                sourceAddr = CollectionBackend.FindStaticEntry(session.Handle, e.Id, stat);
                if (sourceAddr == 0) continue;
            }

            if (!CollectionBackend.TryReadEntry(session.Handle, sourceAddr, fieldOff, out int val)) continue;

            // 名字：从静态表的 ename 查名字表；查不到就回退 #ID。
            string name = $"#{e.Id}";
            if (stat != null && stat.NameIndexOffset > 0 && locator.Names != null)
            {
                // 名字索引也可能在静态表上（与数值同源或不同源都支持）
                long nameSrc = stat.FieldOffset > 0
                    ? sourceAddr
                    : CollectionBackend.FindStaticEntry(session.Handle, e.Id, stat);

                if (nameSrc != 0 && CollectionBackend.TryReadEntry(session.Handle, nameSrc,
                        stat.NameIndexOffset, out int ename))
                {
                    string resolved = CollectionBackend.ResolveName(session.Handle, ename, locator.Names);
                    if (!string.IsNullOrWhiteSpace(resolved)) name = resolved;
                }
            }

            items.Add(new CollectionItem(e.Id, name, val.ToString()));
        }

        items.Sort((x, y) => x.Id.CompareTo(y.Id));
        return new ReadResult(b.Semantic, true, $"{items.Count} 个条目", where, null) { Items = items };
    }

    private static WriteResult CollectionWrite(GameAdapter adapter, ValueBinding b, string rawValue, bool dryRun)
    {
        string where = b.Describe();
        long fieldOff = ParseOff(b.FieldOffset);
        if (fieldOff < 0)
            return new WriteResult(b.Semantic, false, null, null, true, "集合绑定缺少 fieldOffset");

        // 目标格式：`1001=99`（指定条目）或 `99`（全部条目）
        int? targetId = null;
        string valuePart = rawValue?.Trim() ?? "";
        int eq = valuePart.IndexOf('=');
        if (eq > 0)
        {
            if (!int.TryParse(valuePart[..eq].Trim(), out int tid))
                return new WriteResult(b.Semantic, false, null, null, true, $"条目ID 不是数字：{valuePart[..eq]}");
            targetId = tid;
            valuePart = valuePart[(eq + 1)..].Trim();
        }
        if (!int.TryParse(valuePart, out int value))
            return new WriteResult(b.Semantic, false, null, null, true, $"值不是整数：{valuePart}");

        if (b.Range is { Length: 2 } && (value < b.Range[0] || value > b.Range[1]))
            return new WriteResult(b.Semantic, false, null, null, true,
                $"值 {value} 超出允许区间 {b.Range[0]}–{b.Range[1]}");

        var proc = FindTargetProcess(adapter);
        if (proc == null)
            return new WriteResult(b.Semantic, false, null, null, true, $"找不到进程 {adapter.Exe}");

        using var session = CollectionBackend.Attach(proc.Id, needWrite: !dryRun);
        if (!session.Ok)
            return new WriteResult(b.Semantic, false, null, null, true, session.Error);

        var snap = SaveLocator.Resolve(session.Handle, BuildLocator(b), out string lerr);
        if (snap == null)
            return new WriteResult(b.Semantic, false, null, null, true, lerr);

        var located = CollectionBackend.Reentries(session.Handle, snap.OwnerAddress,
                                                  BuildLocator(b), out string wlerr);
        if (located == null)
            return new WriteResult(b.Semantic, false, null, null, true, wlerr);

        // 单值模式（如总资产）：直接写主体对象上的字段
        if (b.OwnerField)
        {
            if (!CollectionBackend.TryReadEntry(session.Handle, located.OwnerAddress, fieldOff, out int ob))
                return new WriteResult(b.Semantic, false, null, null, true, "读取主体字段失败");

            if (dryRun)
                return new WriteResult(b.Semantic, true, ob.ToString(), value.ToString(), true, "dry-run：未实际写入");

            if (!CollectionBackend.TryWriteEntry(session.Handle, located.OwnerAddress, fieldOff, ob, value, out string owerr))
                return new WriteResult(b.Semantic, false, ob.ToString(), null, true, owerr);
            return new WriteResult(b.Semantic, true, ob.ToString(), value.ToString(), true, null);
        }

        var entries = located.Entries;

        var targets = targetId.HasValue ? entries.FindAll(e => e.Id == targetId.Value) : entries;
        if (targets.Count == 0)
            return new WriteResult(b.Semantic, false, null, null, true,
                $"集合里没有条目 {targetId}（现有：{string.Join(", ", entries.ConvertAll(e => e.Id))}）");

        var befores = new List<string>();
        var afters = new List<string>();

        var stat = BuildStaticLookup(b);

        foreach (var e in targets)
        {
            long sourceAddr = e.Address;

            // 二级查找：写入目标同样可能在静态表上（如医术 Dictemployee+0x30）
            // 只有 FieldOffset > 0 才说明「数值本身」在静态表上（如医术）。
            // FieldOffset = -1 表示静态表只用来查名字，数值仍在条目对象上。
            // 之前这里只判 stat != null，会把 ename(+0x18) 当成好感度读出来（实测读成 1001）。
            if (stat != null && stat.FieldOffset > 0)
            {
                sourceAddr = CollectionBackend.FindStaticEntry(session.Handle, e.Id, stat);
                if (sourceAddr == 0) { befores.Add($"{e.Id}:静态表未找到"); continue; }
            }

            if (!CollectionBackend.TryReadEntry(session.Handle, sourceAddr, fieldOff, out int before)) continue;
            befores.Add($"{e.Id}:{before}");

            if (dryRun) { afters.Add($"{e.Id}:{value}"); continue; }

            if (!CollectionBackend.TryWriteEntry(session.Handle, sourceAddr, fieldOff, before, value, out string werr))
                return new WriteResult(b.Semantic, false, string.Join(" ", befores), null, true,
                    $"条目 {e.Id} 写入失败：{werr}");
            afters.Add($"{e.Id}:{value}");
        }

        return new WriteResult(b.Semantic, true, string.Join(" ", befores), string.Join(" ", afters),
            true, dryRun ? "dry-run：未实际写入" : null);
    }

    private static System.Diagnostics.Process FindTargetProcess(GameAdapter adapter)
    {
        string want = (adapter.Exe ?? "").Trim();
        if (want.Length == 0) return null;
        string name = want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? want[..^4] : want;

        foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
        {
            try { if (!p.HasExited) return p; } catch { }
        }
        return null;
    }
public static WriteResult Write(GameAdapter adapter, string semantic, string rawValue, bool dryRun)
    {
        // 预检只为决定要不要建 CDP 连接（游戏没开端口时保持"绑定缺失/超区间"等准确错误），
        // 权限与合法性判定以 WriteOn 内部为准
        var b = adapter.FindBinding(semantic);
        bool needsCdp = b != null && b.Kind != "memory_aob"
                        && adapter.Mode != "readonly" && adapter.Engine == "construct3";
        if (!needsCdp) return WriteOn(null, adapter, semantic, rawValue, dryRun);

        using var cdp = CdpClient.Connect(adapter.Connection.DebugPort, adapter.Connection.TitleFilter);
        return WriteOn(cdp, adapter, semantic, rawValue, dryRun);
    }

    /// <summary>
    /// 在给定连接上写值；cdp 为 null 时仅处理不需要 CDP 的分支（memory_aob / 校验拒绝）。
    /// PersistentWatcher 传入自己复用的连接。
    /// </summary>
    public static WriteResult WriteOn(CdpClient cdp, GameAdapter adapter, string semantic, string rawValue, bool dryRun)
    {
        var b = adapter.FindBinding(semantic);
        if (b == null) return new WriteResult(semantic, false, null, null, false, "适配包没有该语义的绑定");

        // 区间校验
        if (b.Range is { Length: 2 } && double.TryParse(rawValue, out double dv))
        {
            if (dv < b.Range[0] || dv > b.Range[1])
                return new WriteResult(semantic, false, null, null, b.Realtime,
                    $"值 {dv} 超出允许区间 [{b.Range[0]}, {b.Range[1]}]");
        }

        if (adapter.Mode == "readonly")
            return new WriteResult(semantic, false, null, null, b.Realtime, "适配包处于 readonly 模式，拒绝写入");

        // memory_aob 走内存后端，不依赖 CDP
        if (b.Kind == "memory_aob")
            return MemoryBackend.Write(adapter, b, rawValue, dryRun);

        // memory_collection：集合语义，同样不依赖 CDP
        if (b.Kind == "memory_collection")
            return CollectionWrite(adapter, b, rawValue, dryRun);

        if (adapter.Engine != "construct3")
            return new WriteResult(semantic, false, null, null, b.Realtime, $"引擎 {adapter.Engine} 的写入尚未实现");

        try
        {
            if (cdp == null)
                return new WriteResult(semantic, false, null, null, b.Realtime,
                    "内部错误：该写入需要 CDP 连接但未提供（预检与执行判定不一致，请报告）。");
            var rt = adapter.EngineOptions.RuntimePath;

            // RuntimePath 是**可执行的属性链表达式**（如 self.c3_runtimeInterface._localRuntime），
            // 不能 JSON 序列化——包上引号就变成字符串常量，运行时调用全部失败（实测踩过）。
            // 也不能裸插值——适配包是外部文件，可能携带任意 JS。
            // 解法：白名单校验「标识符.标识符…」形态，通过才允许以表达式形式插入。
            if (!IsValidPropertyChain(rt))
                return new WriteResult(semantic, false, null, null, b.Realtime,
                    $"RuntimePath \"{rt}\" 不是合法的属性链表达式（只允许 标识符.标识符.… 形式）。");

            // 拼进 JS 的值必须是**可控形态**，否则命令行参数可直接注入页面脚本：
            //   sem <游戏> write health "1); evil(("
            //
            // 判定规则统一收敛到 JsLiteral（string → JSON 序列化；
            // 非 string 必须是有限数字，否则拒绝）。这里不再内联一份，
            // 否则两处规则会漂移——dfa55eb 建了 JsLiteral 却漏切这一处，
            // 正是"只堵新路径、不回头统一旧路径"的老毛病。
            if (!JsLiteral.TryPrepare(rawValue, b.ValueType, out string jsVal))
            {
                return new WriteResult(semantic, false, null, null, b.Realtime,
                    $"值 \"{rawValue}\" 不是合法数字（该绑定 valueType={b.ValueType}）。");
            }

            string script;
            if (b.Kind == "instance_var")
                script = BuildWriteInstanceVar(rt, b.ObjectName, b.VarName, jsVal, dryRun);
            else if (b.Kind == "indexeddb_key")
                script = BuildWriteIdbKey(adapter.EngineOptions.LocalStorageDb, adapter.EngineOptions.ObjectStore,
                                          b.StorageKey, jsVal, dryRun);
            else
                return new WriteResult(semantic, false, null, null, b.Realtime, $"kind {b.Kind} 暂不支持写入");

            string raw = cdp.Evaluate(script, awaitPromise: true, timeoutMs: 30000);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
                return new WriteResult(semantic, false, null, null, b.Realtime, err.GetString());

            string before = root.TryGetProperty("before", out var bv) ? bv.GetRawText() : null;
            string after = root.TryGetProperty("after", out var av) ? av.GetRawText() : null;
            return new WriteResult(semantic, true, before, after, b.Realtime, null);
        }
        catch (Exception ex)
        {
            return new WriteResult(semantic, false, null, null, b.Realtime, ex.Message);
        }
    }

    /// <summary>
    /// RuntimePath 白名单校验：只允许「标识符.标识符.…」形式的属性链。
    ///
    /// 为什么不能 JSON 序列化：它是可执行表达式，包引号就变成字符串常量，
    /// rt.GetAllObjectClasses() 之类全部静默失败（实测踩过，导致 probe/sem 集体失灵）。
    /// 为什么不能裸插值：适配包是外部文件，可能携带任意 JS。
    /// 属性链是两者的交集：足够表达 self.a.b._c 这类运行时入口，
    /// 又不可能包含括号/分号/赋值等任何可执行结构。
    /// </summary>
    internal static bool IsValidPropertyChain(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        if (expr.Length > 256) return false;

        foreach (string seg in expr.Split('.'))
        {
            if (seg.Length == 0) return false;
            // 允许 $ 和 _ 开头（JS 合法标识符），其余必须是字母数字/$_
            if (seg[0] != '_' && seg[0] != '$' && !char.IsLetter(seg[0])) return false;
            foreach (char ch in seg)
                if (ch != '_' && ch != '$' && !char.IsLetterOrDigit(ch)) return false;
        }
        return true;
    }

    // ---------------- JS 构造 ----------------

    /// <summary>
    /// 校验 RuntimePath 并把非法值转成无害的 null 表达式。
    ///
    /// 为什么放在构造器里而不是调用方：
    /// 上一轮白名单只加在 Write 路径，ReadAll 和 BuildReadInstanceVar 漏了，
    /// 导致投毒适配包只要被 read 一次就能执行任意 JS（实测打上了标记）。
    /// 把校验下沉到插值点，新增调用点就自动受保护，不依赖调用方记得校验。
    /// </summary>
    private static string SafeRuntimeExpr(string rtPath)
        => IsValidPropertyChain(rtPath) ? rtPath : "null";

    private static string BuildReadInstanceVar(string rtPath, string obj, string var) => $$"""
(() => {
  try {
    const rt = {{SafeRuntimeExpr(rtPath)}};
    if (!rt) return JSON.stringify({ error: '未找到运行时' });
    const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === {{JsonSerializer.Serialize(obj)}});
    if (!cls) return JSON.stringify({ error: '找不到对象类 ' + {{JsonSerializer.Serialize(obj)}} });
    const insts = [...cls.GetInstances()];
    if (insts.length === 0) return JSON.stringify({ error: {{JsonSerializer.Serialize(obj)}} + ' 当前无实例（不在对应场景？）' });
    const inst = insts[0];
    const names = inst._GetAllInstanceVariableNames();
    const idx = names.indexOf({{JsonSerializer.Serialize(var)}});
    if (idx < 0) return JSON.stringify({ error: '变量 ' + {{JsonSerializer.Serialize(var)}} + ' 不存在' });
    let v = inst.GetInstanceVariableValue(idx);
    if (typeof v === 'number' && Number.isNaN(v)) v = 'NaN';
    return JSON.stringify({ value: v });
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";

    private static string BuildWriteInstanceVar(string rtPath, string obj, string var, string jsVal, bool dryRun) => $$"""
(() => {
  try {
    const rt = {{SafeRuntimeExpr(rtPath)}};
    if (!rt) return JSON.stringify({ error: '未找到运行时' });
    const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === {{JsonSerializer.Serialize(obj)}});
    if (!cls) return JSON.stringify({ error: '找不到对象类 ' + {{JsonSerializer.Serialize(obj)}} });
    const insts = [...cls.GetInstances()];
    if (insts.length === 0) return JSON.stringify({ error: {{JsonSerializer.Serialize(obj)}} + ' 当前无实例（不在对应场景？）' });
    const inst = insts[0];
    const names = inst._GetAllInstanceVariableNames();
    const idx = names.indexOf({{JsonSerializer.Serialize(var)}});
    if (idx < 0) return JSON.stringify({ error: '变量 ' + {{JsonSerializer.Serialize(var)}} + ' 不存在' });
    const before = inst.GetInstanceVariableValue(idx);
    const DRY = {{(dryRun ? "true" : "false")}};
    // dry-run 不落写入，after 直接取目标值求值——若读回旧值会输出 "5 → 5" 误导使用者
    const after = DRY
      ? ({{jsVal}})
      : (inst.SetInstanceVariableValue(idx, {{jsVal}}), inst.GetInstanceVariableValue(idx));
    return JSON.stringify({ before, after });
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";

    private static string BuildReadIdbKey(string db, string store, string key) => $$"""
(async () => {
  try {
    const d = await new Promise((res, rej) => {
      const q = indexedDB.open({{JsonSerializer.Serialize(db)}});
      q.onsuccess = () => res(q.result);
      q.onerror = () => rej(q.error);
    });
    const v = await new Promise((res, rej) => {
      const tx = d.transaction({{JsonSerializer.Serialize(store)}}, 'readonly');
      const q = tx.objectStore({{JsonSerializer.Serialize(store)}}).get({{JsonSerializer.Serialize(key)}});
      tx.oncomplete = () => res(q.result);
      tx.onerror = () => rej(tx.error);
    });
    d.close();
    return JSON.stringify({ value: (v === undefined ? null : v) });
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";

    private static string BuildWriteIdbKey(string db, string store, string key, string jsVal, bool dryRun) => $$"""
(async () => {
  try {
    const d = await new Promise((res, rej) => {
      const q = indexedDB.open({{JsonSerializer.Serialize(db)}});
      q.onsuccess = () => res(q.result);
      q.onerror = () => rej(q.error);
    });
    const read = () => new Promise((res, rej) => {
      const tx = d.transaction({{JsonSerializer.Serialize(store)}}, 'readonly');
      const q = tx.objectStore({{JsonSerializer.Serialize(store)}}).get({{JsonSerializer.Serialize(key)}});
      tx.oncomplete = () => res(q.result);
      tx.onerror = () => rej(tx.error);
    });
    const before = await read();
    const DRY = {{(dryRun ? "true" : "false")}};
    // dry-run 不落写入，after 取目标值——读回旧值会输出 "5 → 5" 误导使用者
    let after;
    if (DRY) {
      after = ({{jsVal}});
    } else {
      await new Promise((res, rej) => {
        const tx = d.transaction({{JsonSerializer.Serialize(store)}}, 'readwrite');
        tx.objectStore({{JsonSerializer.Serialize(store)}}).put({{jsVal}}, {{JsonSerializer.Serialize(key)}});
        tx.oncomplete = () => res();
        tx.onerror = () => rej(tx.error);
      });
      after = await read();
    }
    d.close();
    return JSON.stringify({ before, after });
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";
}
