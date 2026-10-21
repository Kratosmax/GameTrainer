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
    public sealed record ReadResult(string Semantic, bool Ok, string Value, string Where, string Error);
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

        // memory_aob 不依赖 CDP：单独走内存后端，其余仍走 Web 路径。
        var memBindings = adapter.Bindings.Where(b => b.Kind == "memory_aob").ToList();
        foreach (var b in memBindings)
            map[b.Semantic] = MemoryBackend.Read(adapter, b);

        var webBindings = adapter.Bindings.Where(b => b.Kind != "memory_aob").ToList();
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
            // 按对象名分组，减少查找次数
            var groups = instBindings.GroupBy(b => b.ObjectName);
            var reqs = groups.Select(g => new
            {
                obj = g.Key,
                vars = g.Select(b => b.VarName).ToArray()
            });

            string script = $$"""
(() => {
  const RT = {{JsonSerializer.Serialize(rt)}};
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
        }
        wanted = wanted.Except(memWanted).ToList();
        if (wanted.Count == 0) return results;

        if (adapter.Engine != "construct3")
        {
            foreach (string s in wanted)
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

        // memory_aob 走内存后端，不依赖 CDP
        if (b.Kind == "memory_aob")
            return MemoryBackend.Write(adapter, b, rawValue, dryRun);

        if (adapter.Engine != "construct3")
            return new WriteResult(semantic, false, null, null, b.Realtime, $"引擎 {adapter.Engine} 的写入尚未实现");

        try
        {
            using var cdp = CdpClient.Connect(adapter.Connection.DebugPort, adapter.Connection.TitleFilter);
            var rt = adapter.EngineOptions.RuntimePath;

            // 拼进 JS 的值必须是**可控形态**，否则命令行参数可直接注入页面脚本：
            //   sem <游戏> write health "1); evil(("
            // 规则：string 类型走 JSON 序列化（自带引号与转义）；
            //       其他类型必须能解析成有限数字才允许裸插入；
            //       解析不了就转成 JSON 字符串（引号包裹，无法逃逸成代码）。
            string jsVal;
            if (b.ValueType == "string")
            {
                jsVal = JsonSerializer.Serialize(rawValue);
            }
            else if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                     && !double.IsNaN(num) && !double.IsInfinity(num))
            {
                jsVal = num.ToString("R", CultureInfo.InvariantCulture);
            }
            else
            {
                // 既不是 string 也不是合法数字：拒绝，不给注入留任何空间
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

    // ---------------- JS 构造 ----------------

    private static string BuildReadInstanceVar(string rtPath, string obj, string var) => $$"""
(() => {
  try {
    const rt = {{JsonSerializer.Serialize(rtPath)}};
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
    const rt = {{JsonSerializer.Serialize(rtPath)}};
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
