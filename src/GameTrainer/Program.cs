using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameTrainer.Core;
using GameTrainer.Engine;
using GameTrainer.Memory;
using GameTrainer.Profiles;

namespace GameTrainer;

internal static class Program
{
    private static string _skillRoot;

    // ---- 控制台处理 ----
    // 程序是 Exe（控制台子系统），这样命令行输出 100% 正常：
    // shell 会同步等待、stdout 重定向可用（实测 WinExe 下 `> out.txt` 是 0 字节，
    // 因为 GUI 子系统进程不被 shell 等待）。
    //
    // 但双击时控制台子系统会弹出黑框。解决办法：双击即"无参数启动"，
    // 此时把自己的控制台窗口隐藏掉。命令行用法一定带参数，不受影响。
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

    /// <summary>
    /// 双击启动时隐藏自动弹出的控制台黑框。
    /// 只隐藏、不 Free/Alloc，避免破坏已附着的 stdin/stdout。
    /// </summary>
    private static void HideConsoleWindow()
    {
        IntPtr hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
    }

    private static int Main(string[] args)
    {
        _skillRoot = ResolveSkillRoot();

        // 无参数 = 双击启动：隐藏黑框后直接进 GUI
        if (args.Length == 0)
        {
            HideConsoleWindow();
            return GuiForm.Run(_skillRoot);
        }

        // 有参数 = 命令行用法，保留控制台与标准输出。
        // 只设定 UTF-8 编码，不重建 Console 流（Exe 子系统下流本身是好的）。
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { /* 无控制台时忽略，后续输出走消息框兜底 */ }

        try
        {
            return RunWithArgs(args);
        }
        finally
        {
            try { Console.Out.Flush(); } catch { /* 调用方已退出 */ }
        }
    }

    private static int RunWithArgs(string[] args)
    {
        // --background：由开机自启注册项传入，启动后直接进托盘、不抢焦点
        if (args[0] == "--background")
            return GuiForm.Run(_skillRoot, startBackground: true);

        if (args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return Dispatch(args);
        }
        catch (Exception ex)
        {
            WriteError($"[错误] {ex.Message}");
            if (Environment.GetEnvironmentVariable("GT_DEBUG") == "1")
                WriteError(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// 写错误。无控制台时（例如从快捷方式带参数启动）用消息框兜底，
    /// 不能让错误凭空消失。
    /// </summary>
    private static void WriteError(string message)
    {
        if (Console.IsErrorRedirected || Console.Error != null)
        {
            try { Console.Error.WriteLine(message); return; } catch { /* 无控制台 */ }
        }
        MessageBox.Show(message, "GameTrainer", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// 定位数据根目录（profiles / snapshots / audit.log 所在处）。
    ///
    /// 优先级：环境变量 → exe 自身目录。
    ///
    /// 不再向上遍历目录找 SKILL.md：那是 skill 时代的约定，程序搬到独立发行目录后
    /// 会误命中仓库根（实测：exe 在 temp/run/ 却去找 F:\git\temp\game-trainer\profiles）。
    /// 便携程序的数据目录就该跟 exe 在一起。
    /// </summary>
    private static string ResolveSkillRoot()
    {
        string env = Environment.GetEnvironmentVariable("GT_SKILL_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        // exe 所在目录即数据目录。开发期（bin/Release/net8.0-windows）向上找到
        // 含 profiles 的工程级目录，保证 `dotnet run` 也能用。
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "profiles"))) return d.FullName;
        }
        return AppContext.BaseDirectory;
    }

    private static int Dispatch(string[] a)
    {
        switch (a[0].ToLowerInvariant())
        {
            case "profiles": return CmdProfiles();
            case "check": return CmdCheck(a);
            case "scan": return CmdScan(a);
            case "narrow": return CmdNarrow(a);
            case "apply": return CmdApply(a);
            case "revert": return CmdRevert(a);
            case "snapshot": return CmdSnapshot(a);
            case "restore": return CmdRestore(a);
            case "save-edit": return CmdSaveEdit(a);
            case "aob": return CmdAob(a);
            case "js": return CmdJs(a);
            case "jsdb": return CmdJsDb(a);
            case "jsset": return CmdJsSet(a);
            case "jsapply": return CmdJsApply(a);
            case "jsobj": return CmdJsObj(a);
            case "jssetvar": return CmdJsSetVar(a);
            case "gui": return GuiForm.Run(_skillRoot);
            default:
                Console.Error.WriteLine($"未知命令: {a[0]}");
                PrintHelp();
                return 2;
        }
    }

    // ---------- 命令实现 ----------

    private static int CmdProfiles()
    {
        string dir = Path.Combine(_skillRoot, "profiles");
        if (!Directory.Exists(dir)) { Console.WriteLine("（还没有任何 profile）"); return 0; }

        foreach (string f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var p = ProfileLoader.Load(f);
                Console.WriteLine($"{p.Id,-16} {p.Name,-28} exe={p.Exe}  patches={p.Patches.Count}  mode={p.Mode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Path.GetFileName(f),-16} [无效] {ex.Message.Split('\n')[0]}");
            }
        }
        return 0;
    }

    private static int CmdCheck(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("用法: GameTrainer check <profileId>"); return 2; }
        GameProfile p = LoadProfile(a[1]);

        Console.WriteLine($"profile : {p.Id} ({p.Name})");
        Console.WriteLine($"exe     : {p.Exe}");
        Console.WriteLine($"mode    : {p.Mode}");

        ProcessMemory mem = null;
        try
        {
            mem = ProcessMemory.AttachByName(p.Exe);
            Console.WriteLine($"进程    : PID {mem.Pid}，已附加");
            Console.WriteLine($"区域数  : {mem.Regions.Count}");

            AntiCheatGuard.Result r = AntiCheatGuard.Check(mem);
            Console.WriteLine($"反作弊  : {(r.Blocked ? "检测到！" : "未检测到")}");
            if (r.Blocked) foreach (string s in r.Found) Console.WriteLine($"          - {s}");
            return r.Blocked ? 3 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"进程    : 未附加 — {ex.Message}");
            return 4;
        }
        finally { mem?.Dispose(); }
    }

    private static int CmdScan(string[] a)
    {
        // scan <profileId> <value> [type]
        if (a.Length < 3) { Console.Error.WriteLine("用法: GameTrainer scan <profileId> <value> [type]"); return 2; }
        GameProfile p = LoadProfile(a[1]);
        string valStr = a[2];
        NumType type = a.Length > 3 ? Enum.Parse<NumType>(a[3], true) : NumType.Double;

        if (!ValueCodec.TryParse(valStr, type, out object val))
        {
            Console.Error.WriteLine($"无法把 \"{valStr}\" 解析为 {type}");
            return 2;
        }

        using var mem = ProcessMemory.AttachByName(p.Exe);
        var sc = new Scanner(mem);
        var opt = new Scanner.ScanOptions { Type = type, WritableOnly = true, IncludeImage = false, Aligned = true };

        var sw = Stopwatch.StartNew();
        var hits = sc.FirstScan(val, opt);
        sw.Stop();
        sc.CaptureBaseline(type);

        Console.WriteLine($"首扫完成：{hits.Count} 个候选（{type} = {valStr}，耗时 {sw.ElapsedMilliseconds}ms）");
        PrintHits(hits, 40);

        SaveScanState(p.Id, hits, type);
        SaveBaseline(p.Id, sc.ExportBaseline());
        return 0;
    }

    private static int CmdNarrow(string[] a)
    {
        // narrow <profileId> exact <value> | range <min> <max> | changed | unchanged
        if (a.Length < 3) { Console.Error.WriteLine("用法: narrow <profileId> exact|range|changed|unchanged ..."); return 2; }
        GameProfile p = LoadProfile(a[1]);
        var (hits, type) = LoadScanState(p.Id);

        using var mem = ProcessMemory.AttachByName(p.Exe);
        var sc = new Scanner(mem);
        SetResults(sc, hits);
        sc.RestoreBaseline(LoadBaseline(p.Id));

        List<ScanHit> kept;
        switch (a[2].ToLowerInvariant())
        {
            case "exact":
                if (a.Length < 4) { Console.Error.WriteLine("需要值"); return 2; }
                if (!ValueCodec.TryParse(a[3], type, out object v)) { Console.Error.WriteLine("无法解析"); return 2; }
                kept = sc.NextScanExact(v, type);
                break;
            case "range":
                if (a.Length < 5) { Console.Error.WriteLine("需要 min max"); return 2; }
                kept = sc.NextScanRange(double.Parse(a[3]), double.Parse(a[4]), type);
                break;
            case "changed":
                kept = sc.NextScanChanged(type);
                break;
            case "unchanged":
                kept = sc.NextScanUnchanged(type);
                break;
            default:
                Console.Error.WriteLine("未知过滤方式"); return 2;
        }

        Console.WriteLine($"再扫完成：{kept.Count} 个候选");
        PrintHits(kept, 40);
        SaveScanState(p.Id, kept, type);
        return 0;
    }

    private static int CmdAob(string[] a)
    {
        // aob <profileId> "<pattern>" [module]
        if (a.Length < 3) { Console.Error.WriteLine("用法: GameTrainer aob <profileId> \"<pattern>\" [module]"); return 2; }
        GameProfile p = LoadProfile(a[1]);
        var pat = new AobPattern(a[2]);
        string module = a.Length > 3 ? a[3] : null;

        using var mem = ProcessMemory.AttachByName(p.Exe);
        var results = new List<IntPtr>();

        foreach (var r in mem.Regions)
        {
            if (!Native.NativeMethods.IsReadable(r.Protect)) continue;
            if (r.Size > 256L * 1024 * 1024) continue;
            byte[] buf;
            try { buf = mem.Read(r.Base, (int)r.Size); } catch { continue; }

            int idx = 0;
            while (idx < buf.Length - pat.Length)
            {
                int rel = pat.FirstMatch(buf.AsSpan(idx));
                if (rel < 0) break;
                results.Add(new IntPtr(r.Base.ToInt64() + idx + rel));
                idx = idx + rel + 1;
                if (results.Count > 200) break;
            }
            if (results.Count > 200) break;
        }

        Console.WriteLine($"AOB \"{a[2]}\" 命中 {results.Count} 处{(module != null ? $"（模块过滤未生效，仅提示）" : "")}");
        foreach (var h in results.Take(50)) Console.WriteLine($"  0x{h.ToInt64():X}");
        if (results.Count == 1) Console.WriteLine("  → 唯一命中，可安全用于 profile。");
        else if (results.Count == 0) Console.WriteLine("  → 未命中，签名需要调整。");
        else Console.WriteLine("  → 不唯一，请在签名中加入更多上下文。");
        return 0;
    }

    // ---------------- js_runtime 后端（NW.js / Electron / CEF） ----------------

    /// <summary>
    /// js &lt;port&gt; "&lt;javascript&gt;" [titleFilter]
    /// 在目标页面的 JS 上下文里求值。用于 NW.js 类游戏：直接读改变量，
    /// 不受 V8 对象在堆中移动（地址漂移）影响。
    /// </summary>
    private static int CmdJs(string[] a)
    {
        if (a.Length < 3)
        {
            Console.Error.WriteLine("用法: js <port> \"<javascript>\" [titleFilter]");
            Console.Error.WriteLine("注意：目标需以 --remote-debugging-port=<port> 启动。");
            return 2;
        }
        if (!int.TryParse(a[1], out int port)) { Console.Error.WriteLine("端口无效"); return 2; }
        string code = a[2];
        string filter = a.Length > 3 ? a[3] : null;

        try
        {
            using var cdp = CdpClient.Connect(port, filter);
            Console.WriteLine($"目标: {cdp.TargetTitle}  ({cdp.TargetUrl})");
            Console.WriteLine($"浏览器: {cdp.BrowserVersion}");
            string result = cdp.Evaluate(code);
            Console.WriteLine("--- 结果 ---");
            Console.WriteLine(result ?? "(undefined)");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// jsdb &lt;port&gt; [dbName] [titleFilter]
    /// 列出 IndexedDB 数据库，或导出指定库的全部 object store 键值。
    /// Construct 3 把进度存在 c3-localstorage-&lt;projectId&gt; 里。
    /// </summary>
    private static int CmdJsDb(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: jsdb <port> [dbName] [titleFilter]");
            return 2;
        }
        if (!int.TryParse(a[1], out int port)) { Console.Error.WriteLine("端口无效"); return 2; }
        string dbName = a.Length > 2 && a[2] != "-" ? a[2] : null;
        string filter = a.Length > 3 ? a[3] : null;

        string script = dbName == null ? JsListDatabases() : JsDumpDatabase(dbName);

        try
        {
            using var cdp = CdpClient.Connect(port, filter);
            Console.WriteLine($"目标: {cdp.TargetTitle}");
            string result = cdp.Evaluate(script, awaitPromise: true, timeoutMs: 60000);
            Console.WriteLine(result ?? "(无结果)");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// jsapply 的实例变量分支：通过官方接口 SetInstanceVariableValue 修改。
    /// 注意参数顺序是 (索引, 值)，与 C# 直觉相反，实测写反会静默失败或抛 RangeError。
    /// </summary>
    private static int ApplyInstanceVar(GameProfile p, PatchDef patch, string rawValue, bool dryRun)
    {
        string jsVal = patch.ValueType == "string" ? JsonSerializer.Serialize(rawValue) : rawValue;
        string script = $$"""
(() => {
  const OBJ = {{JsonSerializer.Serialize(patch.ObjectName)}};
  const VAR = {{JsonSerializer.Serialize(patch.VarName)}};
  const VAL = {{jsVal}};
  const DRY = {{(dryRun ? "true" : "false")}};
  const rt = self.c3_runtimeInterface._localRuntime;
  if (!rt) return '未找到 Construct 运行时';
  const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === OBJ);
  if (!cls) return '找不到对象类: ' + OBJ;
  const insts = [...cls.GetInstances()];
  if (insts.length === 0) return OBJ + ' 当前没有实例（可能不在对应场景）';
  const inst = insts[0];
  const names = inst._GetAllInstanceVariableNames();
  const idx = names.indexOf(VAR);
  if (idx < 0) return '对象 ' + OBJ + ' 没有变量 ' + VAR + '\n可用: ' + names.join(', ');
  const before = inst.GetInstanceVariableValue(idx);
  if (!DRY) inst.SetInstanceVariableValue(idx, VAL);
  const after = inst.GetInstanceVariableValue(idx);
  return JSON.stringify({ object: OBJ, variable: VAR, index: idx, dryRun: DRY, before, planned: VAL, after }, null, 2);
})()
""";

        try
        {
            using var cdp = CdpClient.Connect(p.Runtime.DebugPort, p.Runtime.TitleFilter);
            Console.WriteLine(cdp.Evaluate(script, awaitPromise: true, timeoutMs: 30000) ?? "(无结果)");
            if (dryRun) Console.WriteLine("（dry-run：未写入）");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// jssetvar &lt;port&gt; &lt;对象名&gt; &lt;变量名&gt; &lt;值&gt; [--dry-run]
    /// 通过 Construct 3 官方接口 SetInstanceVariableValue 修改实例变量。
    /// 与直接改 _instVarValues 数组不同，走官方接口能让游戏自身感知变化。
    /// </summary>
    private static int CmdJsSetVar(string[] a)
    {
        if (a.Length < 5)
        {
            Console.Error.WriteLine("用法: jssetvar <port> <对象名> <变量名> <值> [--dry-run]");
            return 2;
        }
        if (!int.TryParse(a[1], out int port)) { Console.Error.WriteLine("端口无效"); return 2; }
        string objName = a[2], varName = a[3], rawValue = a[4];
        bool dryRun = a.Skip(5).Any(x => x == "--dry-run" || x == "-n");
        string jsVal = double.TryParse(rawValue, out _) ? rawValue : JsonSerializer.Serialize(rawValue);

        string script = $$"""
(() => {
  const OBJ = {{JsonSerializer.Serialize(objName)}};
  const VAR = {{JsonSerializer.Serialize(varName)}};
  const VAL = {{jsVal}};
  const DRY = {{(dryRun ? "true" : "false")}};
  const rt = self.c3_runtimeInterface._localRuntime;
  if (!rt) return '未找到 Construct 运行时';
  const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === OBJ);
  if (!cls) return '找不到对象类: ' + OBJ;
  const insts = [...cls.GetInstances()];
  if (insts.length === 0) return OBJ + ' 当前没有实例（可能不在对应场景）';
  const inst = insts[0];
  const names = inst._GetAllInstanceVariableNames();
  const idx = names.indexOf(VAR);
  if (idx < 0) return '对象 ' + OBJ + ' 没有变量 ' + VAR + '\n可用: ' + names.join(', ');
  const before = inst.GetInstanceVariableValue(idx);
  if (!DRY) inst.SetInstanceVariableValue(idx, VAL);
  const after = inst.GetInstanceVariableValue(idx);
  return JSON.stringify({ object: OBJ, variable: VAR, index: idx, dryRun: DRY, before, planned: VAL, after }, null, 2);
})()
""";

        try
        {
            using var cdp = CdpClient.Connect(port, null);
            Console.WriteLine(cdp.Evaluate(script, awaitPromise: true, timeoutMs: 30000) ?? "(无结果)");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// jsobj &lt;port&gt; [对象名] [titleFilter]
    /// 探查 Construct 3 运行时：
    ///   - 不带对象名：列出所有有实例的对象类及实例数
    ///   - 带对象名：打印该对象第一个实例的全部实例变量
    /// 用于定位局内数值（血量、攻击力等）。
    /// </summary>
    private static int CmdJsObj(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: jsobj <port> [对象名] [titleFilter]");
            return 2;
        }
        if (!int.TryParse(a[1], out int port)) { Console.Error.WriteLine("端口无效"); return 2; }
        string objName = a.Length > 2 && a[2] != "-" ? a[2] : null;
        string filter = a.Length > 3 ? a[3] : null;

        string script = objName == null ? JsListInstances() : JsDumpInstance(objName);

        try
        {
            using var cdp = CdpClient.Connect(port, filter);
            Console.WriteLine($"目标: {cdp.TargetTitle}");
            Console.WriteLine(cdp.Evaluate(script, awaitPromise: true, timeoutMs: 30000) ?? "(无结果)");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>列出所有存在实例的对象类。</summary>
    private static string JsListInstances() => """
(() => {
  const rt = self.c3_runtimeInterface._localRuntime;
  if (!rt) return '未找到 Construct 运行时（游戏未加载或非 C3 项目）';
  const rows = [];
  let layout = '?';
  try { layout = rt.GetLayoutManager().GetCurrentLayout().GetName(); } catch (e) {}
  for (const c of rt.GetAllObjectClasses()) {
    let n = 0;
    try { n = [...c.GetInstances()].length; } catch (e) { n = -1; }
    if (n > 0) rows.push(`${c.GetName()}\t${n}`);
  }
  return '当前场景: ' + layout + '\n有实例的对象类 (' + rows.length + '):\n' + rows.join('\n');
})()
""";

    /// <summary>打印指定对象第一个实例的全部实例变量。</summary>
    private static string JsDumpInstance(string objName) => $$"""
(() => {
  const NAME = {{JsonSerializer.Serialize(objName)}};
  const rt = self.c3_runtimeInterface._localRuntime;
  if (!rt) return '未找到 Construct 运行时';
  const cls = [...rt.GetAllObjectClasses()].find(c => c.GetName() === NAME);
  if (!cls) return '找不到对象类: ' + NAME;
  const insts = [...cls.GetInstances()];
  if (insts.length === 0) return NAME + ' 当前没有实例（可能不在对应场景）';

  const lines = [NAME + ' 共 ' + insts.length + ' 个实例，展示前 3 个：'];
  for (let k = 0; k < Math.min(3, insts.length); k++) {
    const inst = insts[k];
    const vars = inst._objectType && inst._objectType._instVars;
    const vals = inst._instVarValues;
    lines.push(`\n--- 实例 #${k}  uid=${inst._uid} ---`);
    if (vars && vals) {
      for (let i = 0; i < vars.length; i++) {
        let v = vals[i];
        if (typeof v === 'number' && Number.isNaN(v)) v = 'NaN';
        lines.push(`  ${vars[i].name} = ${JSON.stringify(v)}`);
      }
    } else {
      lines.push('  (该对象类没有实例变量)');
    }
  }
  return lines.join('\n');
})()
""";

    /// <summary>
    /// jsapply &lt;profileId&gt; &lt;patchId&gt; &lt;value&gt; [--dry-run]
    /// 按 profile 的 runtime/storage 配置写入 JS 侧存储。
    /// 与内存后端同样走闸门：区间校验、写前读旧值、支持 dry-run。
    /// </summary>
    private static int CmdJsApply(string[] a)
    {
        if (a.Length < 4)
        {
            Console.Error.WriteLine("用法: jsapply <profileId> <patchId> <value> [--dry-run]");
            return 2;
        }
        GameProfile p;
        try { p = LoadProfile(a[1]); }
        catch (Exception ex) { WriteError("[错误] " + ex.Message); return 1; }

        string patchId = a[2];
        string rawValue = a[3];
        bool dryRun = a.Skip(4).Any(x => x == "--dry-run" || x == "-n");

        if (p.Runtime == null || p.Runtime.Type != "js_cdp")
        {
            Console.Error.WriteLine($"profile {p.Id} 未配置 js_cdp 运行时。");
            return 2;
        }
        if (p.Storage == null || string.IsNullOrWhiteSpace(p.Storage.Database))
        {
            Console.Error.WriteLine($"profile {p.Id} 未配置 storage。");
            return 2;
        }

        PatchDef patch = p.Patches.FirstOrDefault(x => string.Equals(x.Id, patchId, StringComparison.OrdinalIgnoreCase));
        if (patch == null) { Console.Error.WriteLine($"找不到 patch: {patchId}"); return 2; }
        if (patch.Kind != "jsvar") { Console.Error.WriteLine($"patch {patchId} 不是 jsvar 类型。"); return 2; }

        if (patch.Range is { Length: 2 } && double.TryParse(rawValue, out double dv))
        {
            if (dv < patch.Range[0] || dv > patch.Range[1])
            {
                Console.Error.WriteLine($"[拒绝] {dv} 超出允许区间 [{patch.Range[0]}, {patch.Range[1]}]");
                return 3;
            }
        }

        // 两种 jsvar：
        //   A. 实例变量型（objectName + varName）→ 走 SetInstanceVariableValue，实时生效
        //   B. storage 型（storageKey）        → 写 IndexedDB，需重载页面
        if (!string.IsNullOrWhiteSpace(patch.ObjectName))
        {
            return ApplyInstanceVar(p, patch, rawValue, dryRun);
        }

        string jsVal = patch.ValueType == "string" ? JsonSerializer.Serialize(rawValue) : rawValue;
        string store = string.IsNullOrWhiteSpace(p.Storage.ObjectStore) ? "keyvaluepairs" : p.Storage.ObjectStore;

        string script = $$"""
(async () => {
  const DB = {{JsonSerializer.Serialize(p.Storage.Database)}};
  const STORE = {{JsonSerializer.Serialize(store)}};
  const KEY = {{JsonSerializer.Serialize(patch.StorageKey)}};
  const VAL = {{jsVal}};
  const DRY = {{(dryRun ? "true" : "false")}};
  const db = await new Promise((res, rej) => {
    const r = indexedDB.open(DB);
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
  const read = () => new Promise((res, rej) => {
    const tx = db.transaction(STORE, 'readonly');
    const req = tx.objectStore(STORE).get(KEY);
    tx.oncomplete = () => res(req.result);
    tx.onerror = () => rej(tx.error);
  });
  const before = await read();
  let after = before;
  if (!DRY) {
    await new Promise((res, rej) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(VAL, KEY);
      tx.oncomplete = () => res();
      tx.onerror = () => rej(tx.error);
    });
    after = await read();
  }
  db.close();
  return JSON.stringify({ dryRun: DRY, key: KEY, before, planned: VAL, after }, null, 2);
})()
""";

        try
        {
            using var cdp = CdpClient.Connect(p.Runtime.DebugPort, p.Runtime.TitleFilter);
            Console.WriteLine($"目标: {cdp.TargetTitle}   库: {p.Storage.Database}/{store}   键: {patch.StorageKey}");
            string result = cdp.Evaluate(script, awaitPromise: true, timeoutMs: 30000);
            Console.WriteLine(result ?? "(无结果)");
            if (dryRun) Console.WriteLine("（dry-run：未写入）");
            else Console.WriteLine("提示：多数游戏需重载页面或重启才能读到新值。");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// jsset &lt;port&gt; &lt;db&gt; &lt;key&gt; &lt;value&gt; [--type number|string] [titleFilter]
    /// 写入 IndexedDB 的键值。写前自动读取旧值并打印，便于回滚。
    /// </summary>
    private static int CmdJsSet(string[] a)
    {
        if (a.Length < 5)
        {
            Console.Error.WriteLine("用法: jsset <port> <db> <key> <value> [--type number|string]");
            return 2;
        }
        if (!int.TryParse(a[1], out int port)) { Console.Error.WriteLine("端口无效"); return 2; }
        string db = a[2], key = a[3], raw = a[4];
        string type = "number";
        string filter = null;
        for (int i = 5; i < a.Length; i++)
        {
            if (a[i] == "--type" && i + 1 < a.Length) { type = a[++i]; }
            else filter = a[i];
        }

        string jsVal = type == "string" ? JsonSerializer.Serialize(raw) : raw;

        string script = $$"""
(async () => {
  const DB = {{JsonSerializer.Serialize(db)}};
  const KEY = {{JsonSerializer.Serialize(key)}};
  const VAL = {{jsVal}};
  const db = await new Promise((res, rej) => {
    const r = indexedDB.open(DB);
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
  const read = () => new Promise((res, rej) => {
    const tx = db.transaction('keyvaluepairs', 'readonly');
    const req = tx.objectStore('keyvaluepairs').get(KEY);
    tx.oncomplete = () => res(req.result);
    tx.onerror = () => rej(tx.error);
  });
  const before = await read();
  await new Promise((res, rej) => {
    const tx = db.transaction('keyvaluepairs', 'readwrite');
    tx.objectStore('keyvaluepairs').put(VAL, KEY);
    tx.oncomplete = () => res();
    tx.onerror = () => rej(tx.error);
  });
  const after = await read();
  db.close();
  return JSON.stringify({ key: KEY, before, after }, null, 2);
})()
""";

        try
        {
            using var cdp = CdpClient.Connect(port, filter);
            string result = cdp.Evaluate(script, awaitPromise: true, timeoutMs: 30000);
            Console.WriteLine(result ?? "(无结果)");
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>列出所有 IndexedDB 数据库（含版本）。</summary>
    private static string JsListDatabases() => """
(async () => {
  const dbs = await indexedDB.databases();
  return JSON.stringify(dbs, null, 2);
})()
""";

    /// <summary>
    /// 导出指定数据库全部 object store 的键值。
    /// value 可能是 JSON 字符串，尝试解析后展示前 2000 字符。
    /// </summary>
    private static string JsDumpDatabase(string dbName) => $$"""
(async () => {
  const DB = {{JsonSerializer.Serialize(dbName)}};
  const open = () => new Promise((res, rej) => {
    const r = indexedDB.open(DB);
    r.onsuccess = () => res(r.result);
    r.onerror = () => rej(r.error);
  });
  const db = await open();
  const stores = [...db.objectStoreNames];
  const out = { database: DB, version: db.version, stores: {} };
  for (const s of stores) {
    const items = await new Promise((res, rej) => {
      const tx = db.transaction(s, 'readonly');
      const os = tx.objectStore(s);
      const req = os.getAll();
      const kreq = os.getAllKeys();
      tx.oncomplete = () => res({ values: req.result, keys: kreq.result });
      tx.onerror = () => rej(tx.error);
    });
    out.stores[s] = items.keys.map((k, i) => {
      let v = items.values[i];
      if (v && typeof v === 'object' && !(v instanceof ArrayBuffer)) {
        try { v = JSON.stringify(v); } catch { v = String(v); }
      } else if (v instanceof ArrayBuffer) {
        v = '[' + v.byteLength + ' bytes]';
      }
      if (typeof v === 'string' && v.length > 4000) v = v.slice(0, 4000) + '...(' + v.length + ')';
      return { key: k, value: v };
    });
  }
  db.close();
  return JSON.stringify(out, null, 2);
})()
""";


    private static int CmdApply(string[] a)
    {
        // apply <profileId> <patchId> [--dry-run]
        if (a.Length < 3) { Console.Error.WriteLine("用法: GameTrainer apply <profileId> <patchId> [--dry-run]"); return 2; }
        bool dry = a.Contains("--dry-run");
        GameProfile p = LoadProfile(a[1]);

        using var mem = ProcessMemory.AttachByName(p.Exe);
        var log = new AuditLog(Path.Combine(_skillRoot, "audit.log"));
        var engine = new PatchEngine(mem, p, log, new RollbackStack(), new ActiveStateStore(_skillRoot));
        engine.ApplyAntiCheat(p.Anticheat);

        if (engine.ReadOnlyForced)
        {
            Console.Error.WriteLine("[阻断] " + engine.ReadOnlyReason);
            return 3;
        }

        ApplyResult r = engine.Enable(a[2], dry);
        WriteResult(r);

        if (r.Ok && !dry) Console.WriteLine("\n保持进程运行以维持修改。Ctrl+C 退出时会自动回滚。");
        if (!r.Ok) return 1;

        if (!dry) WaitWithRollbackOnExit(engine, mem);
        return 0;
    }

    private static int CmdRevert(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("用法: GameTrainer revert <profileId> [patchId]"); return 2; }
        GameProfile p = LoadProfile(a[1]);
        using var mem = ProcessMemory.AttachByName(p.Exe);
        var log = new AuditLog(Path.Combine(_skillRoot, "audit.log"));
        var engine = new PatchEngine(mem, p, log, new RollbackStack(), new ActiveStateStore(_skillRoot));

        if (a.Length >= 3)
        {
            // 带 --by-profile：跨进程补救模式，按 profile 语义重新定位并还原
            if (a.Contains("--by-profile"))
            {
                ApplyResult r2 = engine.RestoreByProfile(a[2]);
                WriteResult(r2);
                return r2.Ok ? 0 : 1;
            }

            ApplyResult r = engine.Disable(a[2]);
            WriteResult(r);
            return r.Ok ? 0 : 1;
        }

        engine.StopAllFreezes();
        int n = engine.RevertAllPending();
        Console.WriteLine($"已停止全部锁定，回滚 {n} 处。");
        return 0;
    }

    private static int CmdSnapshot(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("用法: GameTrainer snapshot <profileId> [说明]"); return 2; }
        GameProfile p = LoadProfile(a[1]);
        var log = new AuditLog(Path.Combine(_skillRoot, "audit.log"));
        var se = new SaveEngine(p, log, _skillRoot);
        string note = a.Length > 2 ? string.Join(' ', a.Skip(2)) : null;

        SnapshotInfo s = se.CreateSnapshot(note);
        Console.WriteLine($"快照已创建: {s.Id}");
        Console.WriteLine($"  来源: {s.SourcePath}");
        Console.WriteLine($"  文件: {s.Files.Count} 个，共 {s.Files.Sum(f => f.Size)} 字节");
        return 0;
    }

    private static int CmdRestore(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("用法: GameTrainer restore <profileId> <snapshotId>"); return 2; }
        GameProfile p = LoadProfile(a[1]);
        var log = new AuditLog(Path.Combine(_skillRoot, "audit.log"));
        var se = new SaveEngine(p, log, _skillRoot);
        ApplyResult r = se.Restore(a[2]);
        WriteResult(r);
        return r.Ok ? 0 : 1;
    }

    private static int CmdSaveEdit(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("用法: GameTrainer save-edit <profileId> <patchId>"); return 2; }
        GameProfile p = LoadProfile(a[1]);
        var patch = p.Patches.FirstOrDefault(x => x.Id == a[2]);
        if (patch?.SaveWrite == null) { Console.Error.WriteLine("未找到存档类 patch"); return 2; }

        var log = new AuditLog(Path.Combine(_skillRoot, "audit.log"));
        var se = new SaveEngine(p, log, _skillRoot);
        ApplyResult r = se.ApplyEdit(patch.SaveWrite);
        WriteResult(r);
        return r.Ok ? 0 : 1;
    }

    // ---------- 辅助 ----------

    private static GameProfile LoadProfile(string idOrPath)
    {
        string path = File.Exists(idOrPath)
            ? idOrPath
            : Path.Combine(_skillRoot, "profiles", idOrPath + ".json");
        if (!File.Exists(path))
            throw new InvalidOperationException($"找不到 profile: {path}");
        return ProfileLoader.Load(path);
    }

    private static void PrintHits(List<ScanHit> hits, int limit)
    {
        foreach (var h in hits.Take(limit))
            Console.WriteLine($"  0x{h.Address.ToInt64():X}  [{h.ModuleName}]  {Native.NativeMethods.ProtectName(h.Protect)}");
        if (hits.Count > limit) Console.WriteLine($"  ... 还有 {hits.Count - limit} 个");
    }

    private static void WriteResult(ApplyResult r)
    {
        Console.WriteLine((r.Ok ? "[成功] " : "[失败] ") + r.Message);
        foreach (string d in r.Details) Console.WriteLine(d);
    }

    private static void WaitWithRollbackOnExit(PatchEngine engine, ProcessMemory mem)
    {
        bool interrupted = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            interrupted = true;
        };

        int pid = mem.Pid;

        // 常驻等待。目标进程一旦退出，立刻停止锁定并退出——
        // 目标已消失时继续持有写入毫无意义，且会留下孤儿进程锁住 exe。
        while (!interrupted)
        {
            if (!IsProcessAlive(pid))
            {
                Console.WriteLine($"\n目标进程 {pid} 已退出，修改随之失效。");
                engine.StopAllFreezes();
                engine.RevertAllPending(); // 尽力回滚（目标已退出时失败属预期）
                return;
            }
            Thread.Sleep(500);
        }

        Console.WriteLine("\n收到中断，正在回滚…");
        engine.StopAllFreezes();
        int n = engine.RevertAllPending();
        Console.WriteLine($"已回滚 {n} 处。");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    // 扫描状态持久化（候选地址跨命令保留）
    private static string ScanStatePath(string profileId)
        => Path.Combine(_skillRoot, "snapshots", "scan-" + profileId + ".json");

    private static void SaveScanState(string profileId, List<ScanHit> hits, NumType type)
    {
        string p = ScanStatePath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var dto = new
        {
            type = type.ToString(),
            hits = hits.Select(h => new { addr = h.Address.ToInt64(), @base = h.RegionBase.ToInt64(), protect = h.Protect, mod = h.ModuleName })
        };
        File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(dto));
    }

    private static (List<ScanHit>, NumType) LoadScanState(string profileId)
    {
        string p = ScanStatePath(profileId);
        if (!File.Exists(p)) throw new InvalidOperationException("还没有扫描记录，请先执行 scan。");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
        var type = Enum.Parse<NumType>(doc.RootElement.GetProperty("type").GetString(), true);
        var hits = new List<ScanHit>();
        foreach (var h in doc.RootElement.GetProperty("hits").EnumerateArray())
        {
            // JSON 属性名是 "base"：匿名类型里的 @base 只是 C# 转义写法，
            // 序列化后 "@" 不会保留（实测踩坑：读 "@base" 抛 KeyNotFound）。
            hits.Add(new ScanHit(
                new IntPtr(h.GetProperty("addr").GetInt64()),
                new IntPtr(h.GetProperty("base").GetInt64()),
                h.GetProperty("protect").GetUInt32(),
                h.GetProperty("mod").GetString()));
        }
        return (hits, type);
    }

    private static Dictionary<IntPtr, byte[]> LoadBaseline(string profileId)
    {
        string p = ScanStatePath(profileId) + ".baseline";
        var d = new Dictionary<IntPtr, byte[]>();
        if (!File.Exists(p)) return d;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
        foreach (var e in doc.RootElement.EnumerateObject())
            d[new IntPtr(long.Parse(e.Name))] = Convert.FromHexString(e.Value.GetString());
        return d;
    }

    private static void SaveBaseline(string profileId, Dictionary<IntPtr, byte[]> baseline)
    {
        string p = ScanStatePath(profileId) + ".baseline";
        var obj = baseline.ToDictionary(kv => kv.Key.ToInt64().ToString(), kv => Convert.ToHexString(kv.Value));
        File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(obj));
    }

    private static void SetResults(Scanner sc, List<ScanHit> hits) => sc.SetHits(hits);

    private static void PrintHelp()
    {
        Console.WriteLine(@"GameTrainer — 单机游戏内存/存档修改器（统一入口）

用法: GameTrainer <命令> [参数]

命令:
  profiles                              列出全部 profile
  check    <profileId>                  检查进程附加与反作弊状态（只读）
  aob      <profileId> ""<pattern>""     在内存中搜索 AOB 签名，报告命中数与唯一性
  scan     <profileId> <value> [type]   首扫：搜数值（type 默认 double）
  narrow   <profileId> exact <v>        再扫：精确等于
  narrow   <profileId> range <min> <max> 再扫：区间
  narrow   <profileId> changed|unchanged 再扫：相对首扫有/无变化
  apply    <profileId> <patchId> [--dry-run]
                                        启用 patch（默认先 dry-run 验证更安全）
  revert   <profileId> [patchId]        回滚（不指定 patch 则全部回滚）
  snapshot <profileId> [说明]           备份存档（改存档前必做）
  restore  <profileId> <snapshotId>     从快照恢复
  save-edit <profileId> <patchId>       执行存档编辑（需 profile 允许）

js_runtime 后端（NW.js / Electron / CEF）:
  先以 --remote-debugging-port=9222 启动目标程序，再使用以下命令：

  jsobj    <port> [对象名]              列出有实例的对象类；给对象名则打印其实例变量
  jsdb     <port> [dbName]              列出 IndexedDB 库；给库名则导出全部键值
  js       <port> ""<javascript>""      在页面上下文执行任意 JS
  jsset    <port> <db> <key> <value>    直接写 IndexedDB 键值（打印写前值）
  jsapply  <profileId> <patchId> <value> [--dry-run]
                                        按 profile 的 storage 配置写入（带区间校验）

type 可选: byte int16 uint16 int32 uint32 int64 uint64 float double

安全说明:
  * 检测到反作弊组件时强制只读，拒绝一切写入（不提供任何绕过）
  * 所有写入都要求签名唯一 + 前值/区间校验通过，否则一个字节都不写
  * apply 不加 --dry-run 也会先做完整校验，校验失败即中止
");
    }
}
