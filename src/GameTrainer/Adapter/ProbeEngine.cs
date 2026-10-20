using System.Text;
using System.Text.Json;


namespace GameTrainer.Adapter;

/// <summary>
/// 引擎探测器：把「遇到一个同类游戏，怎么摸清它」固化成可执行流程。
///
/// 设计原则：探测器只做**只读**探查，不改任何游戏状态。
/// 产出是一份可读报告 + 一份可直接落盘的适配包骨架。
///
/// 覆盖的探测阶段（按顺序，任一阶段失败即停止并报告）：
///   1. 通道：CDP 端口是否可达、页面目标是否可枚举
///   2. 框架：判断 Construct3 / Electron / NW.js / 普通页面
///   3. 运行时：能否拿到游戏对象模型（对象类、实例、变量）
///   4. 存储：IndexedDB / localStorage 里有哪些键值
///   5. 语义映射：把发现的变量名匹配到通用语义，给出建议
/// </summary>
internal static class ProbeEngine
{
    /// <summary>探测结果。</summary>
    public sealed class Report
    {
        public bool CdpReachable;
        public string Browser = "";
        public string TargetTitle = "";
        public string TargetUrl = "";
        public string DetectedEngine = "";
        public readonly List<string> Log = new();
        public readonly List<string> Warnings = new();
        public readonly List<DiscoveredObject> Objects = new();
        public readonly List<DiscoveredBinding> Bindings = new();
        public readonly List<DiscoveredDatabase> Databases = new();
        public string LocalStorageDb;

        public void Say(string s) => Log.Add(s);
        public void Warn(string s) { Log.Add("  ! " + s); Warnings.Add(s); }
    }

    public sealed record DiscoveredObject(string Name, int InstanceCount, List<DiscoveredVar> Vars);
    public sealed record DiscoveredVar(int Index, string Name, string Value, string GuessSemantic, double Score);
    public sealed record DiscoveredBinding(string Semantic, string Kind, string ObjectName, string VarName, string StorageKey, bool Realtime, string Evidence);
    public sealed record DiscoveredDatabase(string Name, int Version, List<string> Stores, int KeyCount);

    /// <summary>
    /// 执行完整探测。只读，不写游戏状态。
    /// </summary>
    public static Report Probe(int port, string titleFilter = null, Action<string> progress = null)
    {
        var r = new Report();
        void P(string s) { r.Say(s); progress?.Invoke(s); }

        // ---- 阶段 1：CDP 通道 ----
        P("[1/5] 检查 CDP 通道…");
        CdpClient cdp;
        try
        {
            cdp = CdpClient.Connect(port, titleFilter);
            r.CdpReachable = true;
            r.Browser = cdp.BrowserVersion;
            r.TargetTitle = cdp.TargetTitle;
            r.TargetUrl = cdp.TargetUrl;
            P($"      OK  {cdp.BrowserVersion}  目标: {cdp.TargetTitle}");
        }
        catch (Exception ex)
        {
            r.Warn("CDP 连接失败: " + ex.Message);
            P("      失败。" + ex.Message);
            P("      提示：目标程序需以 --remote-debugging-port=" + port + " 启动。");
            return r;
        }

        using (cdp)
        {
            // ---- 阶段 2：框架识别 ----
            P("[2/5] 识别游戏框架…");
            string engine = DetectEngine(cdp);
            r.DetectedEngine = engine;
            P($"      OK  引擎: {engine}");

            // ---- 阶段 3：运行时对象模型 ----
            P("[3/5] 探查运行时对象模型…");
            if (engine == "construct3")
            {
                ProbeConstructObjects(cdp, r, P);
            }
            else
            {
                r.Warn($"引擎 {engine} 的对象模型探测尚未实现（当前仅 construct3）");
            }

            // ---- 阶段 4：存储 ----
            P("[4/5] 探查存储（IndexedDB）…");
            ProbeStorage(cdp, r, P);

            // ---- 阶段 5：语义映射 ----
            P("[5/5] 匹配通用语义…");
            BuildSemanticBindings(r, P);
        }

        P("");
        P($"探测完成：引擎={r.DetectedEngine}，对象={r.Objects.Count}，绑定={r.Bindings.Count}，警告={r.Warnings.Count}");
        return r;
    }

    /// <summary>根据页面特征判断框架类型。</summary>
    private static string DetectEngine(CdpClient cdp)
    {
        string probe = """
(() => {
  const r = {};
  r.hasC3Runtime = !!(self.c3_runtimeInterface && self.c3_runtimeInterface._localRuntime);
  r.hasC3Global = typeof self.C3 !== 'undefined';
  r.c3Main = typeof self.C3_CreateRuntime === 'function';
  r.hasElectron = typeof process !== 'undefined' && !!(process.versions && process.versions.electron);
  r.hasNw = typeof process !== 'undefined' && !!(process.versions && process.versions.nw);
  r.versions = (typeof process !== 'undefined' && process.versions) ? {
    electron: process.versions.electron || null,
    nw: process.versions.nw || null,
    chrome: process.versions.chrome || null
  } : null;
  r.hasRequire = typeof require === 'function';
  return JSON.stringify(r);
})()
""";
        try
        {
            string json = cdp.Evaluate(probe);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool hasC3 = root.TryGetProperty("hasC3Runtime", out var c3r) && c3r.GetBoolean();
            if (hasC3) return "construct3";

            bool hasElectron = root.TryGetProperty("hasElectron", out var el) && el.GetBoolean();
            if (hasElectron) return "electron";

            bool hasNw = root.TryGetProperty("hasNw", out var nw) && nw.GetBoolean();
            if (hasNw) return "nwjs";

            return "generic_js";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>探查 Construct 3 的对象类与实例变量。</summary>
    private static void ProbeConstructObjects(CdpClient cdp, Report r, Action<string> P)
    {
        string script = """
(() => {
  const rt = self.c3_runtimeInterface._localRuntime;
  if (!rt) return JSON.stringify({ error: 'no runtime' });
  const classes = [...rt.GetAllObjectClasses()];
  const out = [];
  for (const c of classes) {
    let insts = [];
    try { insts = [...c.GetInstances()]; } catch (e) { continue; }
    if (insts.length === 0) continue;
    const inst = insts[0];
    const names = inst._GetAllInstanceVariableNames ? inst._GetAllInstanceVariableNames() : null;
    if (!names || names.length === 0) {
      out.push({ name: c.GetName(), count: insts.length, vars: [] });
      continue;
    }
    const vars = [];
    for (let i = 0; i < names.length; i++) {
      let v;
      try { v = inst.GetInstanceVariableValue(i); } catch (e) { v = null; }
      vars.push({ i, n: names[i], v: (typeof v === 'number' && Number.isNaN(v)) ? 'NaN' : v });
    }
    out.push({ name: c.GetName(), count: insts.length, vars });
  }
  return JSON.stringify(out);
})()
""";
        try
        {
            string json = cdp.Evaluate(script, timeoutMs: 60000);
            using var doc = JsonDocument.Parse(json);
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                string name = o.GetProperty("name").GetString();
                int count = o.GetProperty("count").GetInt32();
                var vars = new List<DiscoveredVar>();
                if (o.TryGetProperty("vars", out var va))
                {
                    foreach (var v in va.EnumerateArray())
                    {
                        int idx = v.GetProperty("i").GetInt32();
                        string vn = v.GetProperty("n").GetString();
                        string val = v.GetProperty("v").ValueKind == JsonValueKind.String
                            ? v.GetProperty("v").GetString()
                            : v.GetProperty("v").GetRawText();
                        var (sem, score) = Semantics.Guess(vn);
                        vars.Add(new DiscoveredVar(idx, vn, val, sem, score));
                    }
                }
                r.Objects.Add(new DiscoveredObject(name, count, vars));
            }
            P($"      OK  有实例的对象类 {r.Objects.Count} 个");

            // 只报告带了语义命中的对象，避免刷屏
            int meaningful = r.Objects.Count(x => x.Vars.Any(v => v.Score >= 0.7));
            P($"      其中含可识别语义变量的对象 {meaningful} 个");
        }
        catch (Exception ex)
        {
            r.Warn("对象模型探测失败: " + ex.Message);
        }
    }

    /// <summary>探查 IndexedDB。</summary>
    private static void ProbeStorage(CdpClient cdp, Report r, Action<string> P)
    {
        string script = """
(async () => {
  const out = { dbs: [], localDb: null };
  let dbs = [];
  try { dbs = await indexedDB.databases(); } catch (e) { return JSON.stringify(out); }
  for (const info of dbs) {
    const name = info.name;
    let version = info.version;
    let stores = [], keyCount = 0;
    try {
      const db = await new Promise((res, rej) => {
        const q = indexedDB.open(name);
        q.onsuccess = () => res(q.result);
        q.onerror = () => rej(q.error);
      });
      stores = [...db.objectStoreNames];
      for (const s of stores) {
        try {
          keyCount += await new Promise((res, rej) => {
            const tx = db.transaction(s, 'readonly');
            const q = tx.objectStore(s).count();
            tx.oncomplete = () => res(q.result);
            tx.onerror = () => rej(tx.error);
          });
        } catch (e) {}
      }
      db.close();
    } catch (e) {}
    out.dbs.push({ name, version, stores, keyCount });
    if (name.startsWith('c3-localstorage-')) out.localDb = name;
  }
  return JSON.stringify(out);
})()
""";
        try
        {
            string json = cdp.Evaluate(script, timeoutMs: 30000);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("dbs", out var dbs))
            {
                foreach (var d in dbs.EnumerateArray())
                {
                    string name = d.GetProperty("name").GetString();
                    int ver = d.TryGetProperty("version", out var vv) ? vv.GetInt32() : 0;
                    int kc = d.TryGetProperty("keyCount", out var kk) ? kk.GetInt32() : 0;
                    var stores = new List<string>();
                    if (d.TryGetProperty("stores", out var ss))
                        foreach (var s in ss.EnumerateArray()) stores.Add(s.GetString());
                    r.Databases.Add(new DiscoveredDatabase(name, ver, stores, kc));
                }
            }
            if (doc.RootElement.TryGetProperty("localDb", out var ldb) && ldb.ValueKind == JsonValueKind.String)
                r.LocalStorageDb = ldb.GetString();

            P($"      OK  数据库 {r.Databases.Count} 个" + (r.LocalStorageDb != null ? $"，主存储 {r.LocalStorageDb}" : ""));
        }
        catch (Exception ex)
        {
            r.Warn("存储探测失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 由探测结果生成语义绑定建议。
    /// 规则：对每个已知语义，取置信度最高的变量；同一变量只绑定一次。
    /// </summary>
    private static void BuildSemanticBindings(Report r, Action<string> P)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 实例变量优先（实时生效）
        var candidates = new List<(string Semantic, double Score, DiscoveredObject Obj, DiscoveredVar Var)>();
        foreach (var obj in r.Objects)
        {
            foreach (var v in obj.Vars)
            {
                if (v.Score < 0.7 || v.GuessSemantic == null) continue;
                candidates.Add((v.GuessSemantic, v.Score, obj, v));
            }
        }

        foreach (var c in candidates.OrderByDescending(x => x.Score).ThenBy(x => x.Obj.Name))
        {
            string key = c.Semantic;
            if (!used.Add(key)) continue;
            r.Bindings.Add(new DiscoveredBinding(
                c.Semantic, "instance_var", c.Obj.Name, c.Var.Name, null, true,
                $"{c.Obj.Name}.{c.Var.Name}={c.Var.Value} (score {c.Score:0.00})"));
        }

        // IndexedDB 键补充（非实时）
        if (r.LocalStorageDb != null)
        {
            foreach (var (semantic, info) in Semantics.All)
            {
                if (used.Contains(semantic)) continue;
                foreach (string alias in info.Aliases)
                {
                    if (used.Add(semantic))
                    {
                        r.Bindings.Add(new DiscoveredBinding(
                            semantic, "indexeddb_key", null, null, alias, false,
                            $"IndexedDB 键 '{alias}'（按别名推测，需实测确认）"));
                        break;
                    }
                }
            }
        }

        foreach (var b in r.Bindings)
            P($"      {Semantics.Display(b.Semantic),-8} → {b.Kind,-14} {b.ObjectName ?? b.StorageKey}.{b.VarName ?? ""}");
    }

    /// <summary>
    /// 由探测报告生成适配包骨架（JSON），供人工确认后落盘。
    /// </summary>
    public static GameAdapter ToAdapter(Report r, string id, string name, string exe)
    {
        var a = new GameAdapter
        {
            Id = id,
            Name = name,
            Exe = exe,
            Engine = r.DetectedEngine,
            Connection = new ConnectionSpec { TitleFilter = r.TargetTitle },
            EngineOptions = new EngineSpec { LocalStorageDb = r.LocalStorageDb },
            Notes = new ProbeNotes
            {
                Stack = r.Browser,
                ProbedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            }
        };

        foreach (string line in r.Log.Where(l => l.TrimStart().StartsWith("[") || l.Contains("OK")))
            a.Notes.Steps.Add(line.Trim());
        a.Notes.KeyFindings.Add($"CDP 目标: {r.TargetTitle} ({r.TargetUrl})");
        if (r.LocalStorageDb != null) a.Notes.KeyFindings.Add($"Construct 主存储: {r.LocalStorageDb}");
        foreach (string w in r.Warnings) a.Notes.Pitfalls.Add(w);

        foreach (var b in r.Bindings)
        {
            // 探测阶段不猜区间，交给人工/实测填
            a.Bindings.Add(new ValueBinding
            {
                Semantic = b.Semantic,
                Kind = b.Kind,
                ObjectName = b.ObjectName,
                VarName = b.VarName,
                StorageKey = b.StorageKey,
                Realtime = b.Realtime,
                Note = "自动探测生成，区间与正确性待实测确认。依据: " + b.Evidence
            });
        }
        return a;
    }
}
