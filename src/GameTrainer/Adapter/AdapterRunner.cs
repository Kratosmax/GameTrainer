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
    public sealed record ReadResult(string Semantic, bool Ok, string Value, string Where, string Error);
    public sealed record WriteResult(string Semantic, bool Ok, string Before, string After, bool Realtime, string Error);

    /// <summary>连接一次，批量读取多个语义。</summary>
    public static List<ReadResult> Read(GameAdapter adapter, IEnumerable<string> semantics)
    {
        var results = new List<ReadResult>();
        if (adapter.Engine != "construct3")
        {
            foreach (string s in semantics)
                results.Add(new ReadResult(s, false, null, "-", $"引擎 {adapter.Engine} 的读取尚未实现"));
            return results;
        }

        using var cdp = CdpClient.Connect(adapter.Connection.DebugPort, adapter.Connection.TitleFilter);
        var rt = adapter.EngineOptions.RuntimePath;

        foreach (string sem in semantics)
        {
            var b = adapter.FindBinding(sem);
            if (b == null)
            {
                results.Add(new ReadResult(sem, false, null, "-", "适配包没有该语义的绑定"));
                continue;
            }

            if (b.Kind == "instance_var")
            {
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
            else
            {
                results.Add(new ReadResult(sem, false, null, b.Describe(), $"kind {b.Kind} 暂不支持读取"));
            }
        }
        return results;
    }

    /// <summary>按语义写值。dryRun 时只读前值不写。</summary>
    public static WriteResult Write(GameAdapter adapter, string semantic, string rawValue, bool dryRun)
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

        if (adapter.Engine != "construct3")
            return new WriteResult(semantic, false, null, null, b.Realtime, $"引擎 {adapter.Engine} 的写入尚未实现");

        try
        {
            using var cdp = CdpClient.Connect(adapter.Connection.DebugPort, adapter.Connection.TitleFilter);
            var rt = adapter.EngineOptions.RuntimePath;
            string jsVal = b.ValueType == "string" ? JsonSerializer.Serialize(rawValue) : rawValue;

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

    // ---------------- JS 构造 ----------------

    private static string BuildReadInstanceVar(string rtPath, string obj, string var) => $$"""
(() => {
  try {
    const rt = {{rtPath}};
    if (!rt) return JSON.stringify({ error: '未找到运行时' });
    const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === {{JsonSerializer.Serialize(obj)}});
    if (!cls) return JSON.stringify({ error: '找不到对象类 {{obj}}' });
    const insts = [...cls.GetInstances()];
    if (insts.length === 0) return JSON.stringify({ error: '{{obj}} 当前无实例（不在对应场景？）' });
    const inst = insts[0];
    const names = inst._GetAllInstanceVariableNames();
    const idx = names.indexOf({{JsonSerializer.Serialize(var)}});
    if (idx < 0) return JSON.stringify({ error: '变量 {{var}} 不存在' });
    let v = inst.GetInstanceVariableValue(idx);
    if (typeof v === 'number' && Number.isNaN(v)) v = 'NaN';
    return JSON.stringify({ value: v });
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";

    private static string BuildWriteInstanceVar(string rtPath, string obj, string var, string jsVal, bool dryRun) => $$"""
(() => {
  try {
    const rt = {{rtPath}};
    if (!rt) return JSON.stringify({ error: '未找到运行时' });
    const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === {{JsonSerializer.Serialize(obj)}});
    if (!cls) return JSON.stringify({ error: '找不到对象类 {{obj}}' });
    const insts = [...cls.GetInstances()];
    if (insts.length === 0) return JSON.stringify({ error: '{{obj}} 当前无实例（不在对应场景？）' });
    const inst = insts[0];
    const names = inst._GetAllInstanceVariableNames();
    const idx = names.indexOf({{JsonSerializer.Serialize(var)}});
    if (idx < 0) return JSON.stringify({ error: '变量 {{var}} 不存在' });
    const before = inst.GetInstanceVariableValue(idx);
    if (!{{(dryRun ? "true" : "false")}}) inst.SetInstanceVariableValue(idx, {{jsVal}});
    const after = inst.GetInstanceVariableValue(idx);
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
    if (!{{(dryRun ? "true" : "false")}}) {
      await new Promise((res, rej) => {
        const tx = d.transaction({{JsonSerializer.Serialize(store)}}, 'readwrite');
        tx.objectStore({{JsonSerializer.Serialize(store)}}).put({{jsVal}}, {{JsonSerializer.Serialize(key)}});
        tx.oncomplete = () => res();
        tx.onerror = () => rej(tx.error);
      });
    }
    const after = await read();
    d.close();
    return JSON.stringify({ before, after });
  } catch (e) { return JSON.stringify({ error: String(e && e.message || e) }); }
})()
""";
}
