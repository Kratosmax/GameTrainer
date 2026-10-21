using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameTrainer.Adapter;
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

        string baseDir = AppContext.BaseDirectory;

        // 快路径：exe 同目录已有**非空的** adapters 或 profiles，就是它，不再向上找。
        // 这一步必须存在——否则解压出的发行包会因上层目录恰好有同名目录
        // 而被"劫持"到别人的数据目录（实测：包解压在工程内时，profiles
        // 命令列出了仓库里的自测靶配置）。
        //
        // 但"存在"不够，必须是"有内容"：构建输出目录 bin/Release/net8.0-windows
        // 下会残留一个空的 adapters（编译产物副产品），用存在性判断会让
        // 开发期直接停在那层，找不到工程根。
        if (HasData(Path.Combine(baseDir, "adapters")) ||
            HasData(Path.Combine(baseDir, "profiles")))
            return baseDir;

        // 慢路径（仅开发期）：构建输出目录层级较深
        // （temp/build/GameTrainer/Release/net8.0-windows 距工程根 4-5 层），
        // 向上找含非空 adapters/profiles 的工程根。
        //
        // 安全边界靠 looksLikeRepo 把关：目标必须同时具备 .sln 或 src 目录，
        // 才算"工程根"。没有这层判断，发行包解压在任意目录都可能被上层
        // 同名的普通文件夹误认；有了它，即使解压在工程内部也只会认真正的仓库根
        // （而仓库根的 adapters 非空时，包内自己也有非空 adapters，快路径会先命中）。
        var d = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
        {
            bool hasData = HasData(Path.Combine(d.FullName, "adapters")) ||
                           HasData(Path.Combine(d.FullName, "profiles"));
            bool looksLikeRepo = File.Exists(Path.Combine(d.FullName, "GameTrainer.sln")) ||
                                 Directory.Exists(Path.Combine(d.FullName, "src"));
            if (hasData && looksLikeRepo) return d.FullName;
        }

        return baseDir;
    }

    /// <summary>
    /// 目录是否存在且含有至少一个文件/子目录。
    /// 用"有内容"而不是"存在"，避免空目录（编译副产品）被误认为数据目录。
    /// </summary>
    private static bool HasData(string dir)
    {
        try
        {
            return Directory.Exists(dir) &&
                   Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch { return false; }
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
            case "probe": return CmdProbe(a);
            case "adapters": return CmdAdapters();
            case "sem": return CmdSemantic(a);
            case "launch": return CmdLaunch(a);
            case "status": return CmdStatus(a);
            case "preset": return CmdPreset(a);
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
        // 与 sem 路径同一条判定：非 string 必须是有限数字，否则拒绝（防命令行注入 JS）
        if (!JsLiteral.TryPrepare(rawValue, patch.ValueType, out string jsVal))
        {
            Console.Error.WriteLine($"[拒绝] 值 \"{rawValue}\" 不是合法数字（patch {patch.Id} valueType={patch.ValueType}）。");
            return 3;
        }
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
  // dry-run 不落写入，after 取计划值——读回旧值会输出 before==after 误导使用者
  const after = DRY ? VAL : (inst.SetInstanceVariableValue(idx, VAL), inst.GetInstanceVariableValue(idx));
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

    // ---------------- 适配包（Game Adapter Pack）----------------

    /// <summary>
    /// preset &lt;gameId&gt; [presetId] [--dry-run]
    /// 一键应用预设：把一组「语义→目标值」一次性写完。
    /// 不带 presetId 时列出全部预设。
    ///
    /// 存在理由：有些值每局被游戏重置（如角色初始血量上限来自数据表），
    /// 逐项敲命令太烦，于是打包成一条。
    /// </summary>
    private static int CmdPreset(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: preset <gameId> [presetId] [--dry-run]");
            return 2;
        }

        string path = Path.Combine(_skillRoot, "adapters", a[1], "adapter.json");
        if (!File.Exists(path)) { Console.Error.WriteLine($"找不到适配包: {path}"); return 2; }

        GameAdapter adapter;
        try { adapter = GameAdapter.Load(path); }
        catch (Exception ex) { WriteError("[错误] " + ex.Message); return 1; }

        // 只列预设
        if (a.Length < 3 || a[2].StartsWith("-"))
        {
            if (adapter.Presets.Count == 0)
            {
                Console.WriteLine($"{adapter.Name} 没有配置预设。");
                return 0;
            }
            Console.WriteLine($"{adapter.Name} 的预设 ({adapter.Presets.Count} 个):");
            foreach (var p in adapter.Presets)
            {
                Console.WriteLine($"  {p.Id,-14} {p.Label}");
                if (!string.IsNullOrWhiteSpace(p.Description))
                    Console.WriteLine($"                 {p.Description}");
                Console.WriteLine($"                 值: {string.Join(", ", p.Values.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
            Console.WriteLine();
            Console.WriteLine($"用法: preset {adapter.Id} <presetId> [--dry-run]");
            return 0;
        }

        string presetId = a[2];
        bool dryRun = a.Contains("--dry-run");
        var preset = adapter.Presets.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
        if (preset == null)
        {
            Console.Error.WriteLine($"找不到预设: {presetId}");
            Console.Error.WriteLine("可用: " + string.Join(", ", adapter.Presets.Select(p => p.Id)));
            return 2;
        }

        try
        {
            if (adapter.Engine != "native_memory")
                EnsureCdpReady(adapter, adapter.Connection.DebugPort, line => Console.WriteLine(line));

            Console.WriteLine($"{(dryRun ? "(dry-run) " : "")}应用预设 {preset.Id}：{preset.Label}");

            int ok = 0, fail = 0;
            foreach (var kv in preset.Values)
            {
                var r = AdapterRunner.Write(adapter, kv.Key, kv.Value, dryRun);
                if (r.Ok)
                {
                    ok++;
                    Console.WriteLine($"  {Semantics.Display(r.Semantic),-8} {r.Before,8} → {r.After,-8}" +
                                      (r.Realtime ? " [实时]" : " [需重载]"));
                }
                else
                {
                    fail++;
                    Console.Error.WriteLine($"  {Semantics.Display(kv.Key),-8} ! {r.Error}");
                }
            }

            Console.WriteLine($"完成：成功 {ok}，失败 {fail}");
            return fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>
/// 确保 CDP 端口就绪。不通时按适配包自动 launch（方案 A 的完整形态）：
/// 使用者不必先手动启动游戏，也不必记调试参数。
///
/// 只在「有适配包」时生效；没有适配包就退回原来的"提示用户自己启动"。
/// </summary>
    private static bool EnsureCdpReady(GameAdapter adapter, int port, Action<string> progress = null)
    {
        if (GameLauncher.IsCdpReady(port, out _)) return true;

        progress?.Invoke($"端口 {port} 未就绪，尝试自动启动 {adapter.Name}…");
        var r = GameLauncher.Launch(adapter, progress: progress);
        if (r.Ok && r.CdpReady)
        {
            progress?.Invoke("自动启动成功，端口已就绪。");
            return true;
        }

        progress?.Invoke("自动启动失败：" + r.Message);
        return false;
    }

    /// <summary>
    /// launch &lt;gameId&gt; [--no-wait]
    /// 替使用者带上调试参数启动游戏，并等待 CDP 就绪。
    /// 这是消除「每次要记调试端口」的方案 A，不改变游戏本身。
    /// </summary>
    private static int CmdLaunch(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: launch <gameId> [--no-wait]");
            return 2;
        }

        string path = Path.Combine(_skillRoot, "adapters", a[1], "adapter.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"找不到适配包: {path}");
            return 2;
        }

        GameAdapter adapter;
        try { adapter = GameAdapter.Load(path); }
        catch (Exception ex) { WriteError("[错误] " + ex.Message); return 1; }

        if (adapter.Engine is "native_memory")
        {
            Console.Error.WriteLine("该适配包使用内存后端，不需要调试端口，直接启动游戏即可。");
            return 2;
        }

        bool noWait = a.Contains("--no-wait");
        Console.WriteLine($"启动 {adapter.Name}（端口 {adapter.Connection.DebugPort}）");

        var r = GameLauncher.Launch(adapter, noWait, line => Console.WriteLine(line));
        Console.WriteLine();
        if (r.Ok)
        {
            Console.WriteLine("[成功] " + r.Message);
            if (r.CdpReady)
                Console.WriteLine($"下一步：sem {adapter.Id} read");
            return 0;
        }
        Console.Error.WriteLine("[失败] " + r.Message);
        return 1;
    }

    /// <summary>
    /// status &lt;gameId&gt;
    /// 检查当前状态：进程 / 调试端口 / 适配包绑定。
    /// </summary>
    private static int CmdStatus(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: status <gameId>");
            return 2;
        }

        string path = Path.Combine(_skillRoot, "adapters", a[1], "adapter.json");
        if (!File.Exists(path)) { Console.Error.WriteLine($"找不到适配包: {path}"); return 2; }

        GameAdapter adapter;
        try { adapter = GameAdapter.Load(path); }
        catch (Exception ex) { WriteError("[错误] " + ex.Message); return 1; }

        Console.WriteLine($"{adapter.Name}  引擎={adapter.Engine}  模式={adapter.Mode}");

        // 进程
        string procName = Path.GetFileNameWithoutExtension(adapter.Exe);
        var procs = System.Diagnostics.Process.GetProcessesByName(procName);
        if (procs.Length == 0)
            Console.WriteLine("  进程     : 未运行");
        else
            Console.WriteLine($"  进程     : {procs.Length} 个（主进程 PID {procs.OrderByDescending(p => p.WorkingSet64).First().Id}）");

        // 端口
        bool cdp = GameLauncher.IsCdpReady(adapter.Connection.DebugPort, out string detail);
        Console.WriteLine($"  调试端口 : {(cdp ? $"就绪（{adapter.Connection.DebugPort}）" : $"不可用（{adapter.Connection.DebugPort}）")}");

        // 绑定
        int realtime = adapter.Bindings.Count(b => b.Realtime);
        Console.WriteLine($"  语义绑定 : {adapter.Bindings.Count} 个（实时 {realtime}，需重载 {adapter.Bindings.Count - realtime}）");

        Console.WriteLine();
        if (!cdp && procs.Length > 0)
            Console.WriteLine($"建议：游戏未带调试端口启动。关掉它再执行 launch {adapter.Id}");
        else if (!cdp)
            Console.WriteLine($"建议：launch {adapter.Id}");
        else
            Console.WriteLine($"就绪，可以执行 sem {adapter.Id} read");
        return 0;
    }

    /// <summary>
    /// probe --native &lt;进程名&gt; &lt;当前值&gt; [类型]
    ///
    /// 原生进程探测：按值扫描缩小范围，再从候选生成 AOB 候选。
    /// 只读，不写任何字节。
    /// </summary>
    private static int CmdProbeNative(string[] a)
    {
        if (a.Length < 4)
        {
            Console.Error.WriteLine("用法: probe --native <进程名> <当前值> [double|int32|float|int64|byte|int16]");
            return 2;
        }

        string procName = a[2];
        string rawValue = a[3];
        string typeName = a.Length > 4 ? a[4] : "double";

        NumType type;
        switch (typeName.ToLowerInvariant())
        {
            case "byte": case "uint8": type = NumType.Byte; break;
            case "int16": case "short": type = NumType.Int16; break;
            case "int32": case "int": type = NumType.Int32; break;
            case "int64": case "long": type = NumType.Int64; break;
            case "float": type = NumType.Float; break;
            case "double": type = NumType.Double; break;
            default:
                Console.Error.WriteLine($"不支持的类型: {typeName}");
                return 2;
        }

        if (!ValueCodec.TryParse(rawValue, type, out object val))
        {
            Console.Error.WriteLine($"值 \"{rawValue}\" 无法解析为 {typeName}");
            return 2;
        }

        // --next：在上一轮候选基础上过滤（而不是重新首扫）
        bool isNext = a.Contains("--next");
        List<ScanHit> hits;
        string srcDesc;

        if (isNext)
        {
            var prev = NativeProbe.LoadCandidates(procName, out NumType ptype, out string at);
            if (prev.Count == 0)
            {
                Console.Error.WriteLine($"没有上一轮候选记录（{NativeProbe.CandidateFile(procName)}）。");
                Console.Error.WriteLine($"请先执行: probe --native {procName} <当前值> {typeName}");
                return 2;
            }
            Console.WriteLine($"原生探测（过滤）：进程={procName}  值={rawValue}  类型={typeName}");
            Console.WriteLine($"      上一轮 {prev.Count} 个候选（{at}）");
            hits = NativeProbe.FilterByValue(procName, prev, val, ptype, out string ferr);
            if (ferr != null) { Console.Error.WriteLine("[失败] " + ferr); return 1; }
            srcDesc = $"过滤后 {hits.Count} 个";
        }
        else
        {
            Console.WriteLine($"原生探测：进程={procName}  值={rawValue}  类型={typeName}");
            Console.WriteLine("[1/3] 首扫…");
            hits = NativeProbe.FirstScan(procName, val, type, out string err);
            if (err != null) { Console.Error.WriteLine("[失败] " + err); return 1; }
            srcDesc = $"首扫 {hits.Count} 个";
        }

        // 中间结果落盘，供下一轮 --next 过滤
        NativeProbe.SaveCandidates(procName, hits, type);

        Console.WriteLine($"      候选 {hits.Count} 个（{srcDesc}）");
        if (hits.Count == 0) return 1;

        // 候选还多时，引导二次过滤。阈值取 5：
        // 实测按单值扫描，一个 100 能命中 56 处内存，逐个生成签名毫无意义。
        if (hits.Count > 5)
        {
            Console.WriteLine();
            Console.WriteLine($"候选仍然较多（{hits.Count} 个）。请继续收窄：");
            Console.WriteLine($"  1. 回到游戏里改变这个值（买东西、受伤、升级等）");
            Console.WriteLine($"  2. 执行: probe --native {procName} <改变后的新值> {typeName} --next");
            Console.WriteLine($"  重复直到候选 <= 5 个。");
            Console.WriteLine();
            Console.WriteLine("前 10 个候选：");
            foreach (var h in hits.Take(10))
                Console.WriteLine($"  0x{h.Address.ToInt64():X}  [{h.ModuleName}]");
            return 0;
        }

        Console.WriteLine("[2/3] 生成 AOB 候选…");
        int ok = 0;
        foreach (var h in hits)
        {
            var c = NativeProbe.BuildSignature(procName, h.Address, type, out string serr);
            if (c == null) { Console.WriteLine($"  0x{h.Address.ToInt64():X}  签名生成失败：{serr}"); continue; }

            Console.WriteLine();
            Console.WriteLine($"  地址 0x{h.Address.ToInt64():X}  值={c.Value}");
            Console.WriteLine($"  偏移 +0x{c.OffsetFromContext:X}");
            Console.WriteLine($"  AOB : {c.Aob}");
            Console.WriteLine($"  唯一: {(c.IsUnique ? "是 ✓" : "否（需加长上下文）")}");
            if (c.IsUnique) ok++;
        }

        Console.WriteLine();
        Console.WriteLine($"[3/3] 完成：{ok}/{hits.Count} 个候选生成了唯一签名");
        if (ok > 0)
        {
            Console.WriteLine();
            Console.WriteLine("把上面的 aob + offsets 填进适配包的 memory_aob 绑定即可：");
            Console.WriteLine("  { \"semantic\": \"gold\", \"kind\": \"memory_aob\",");
            Console.WriteLine("    \"aob\": \"<上面的 AOB>\", \"offsets\": [\"<偏移>\"], \"type\": \"" + typeName + "\" }");
        }
        return 0;
    }

    /// <summary>
    /// probe &lt;port&gt; [--save &lt;gameId&gt; &lt;name&gt; &lt;exe&gt;] [titleFilter]
    /// 对目标游戏执行只读探测，输出报告；带 --save 时落盘为适配包骨架。
    /// </summary>
    private static int CmdProbe(string[] a)
    {
        // 原生进程模式：probe --native <进程名> <值> [类型]
        // 没有脚本层时用值扫描定位，再生成 AOB 候选。
        if (a.Length >= 4 && a[1] == "--native")
            return CmdProbeNative(a);

        if (a.Length < 2 || !int.TryParse(a[1], out int port))
        {
            Console.Error.WriteLine("用法: probe <port> [--game <gameId>] [--save <gameId> <name> <exe>] [titleFilter]");
            Console.Error.WriteLine("      probe --native <进程名> <当前值> [double|int32|float|int64]");
            return 2;
        }

        string saveId = null, saveName = null, saveExe = null;
        string titleFilter = null;
        for (int i = 2; i < a.Length; i++)
        {
            if (a[i] == "--save" && i + 3 < a.Length)
            {
                saveId = a[++i]; saveName = a[++i]; saveExe = a[++i];
            }
            else titleFilter = a[i];
        }

        // 方案 A：若指定了适配包或端口不通，先确保游戏起来了
        string probeGameId = saveId;
        for (int i = 2; i < a.Length - 1; i++)
            if (a[i] == "--game") probeGameId = a[i + 1];
        if (probeGameId != null)
        {
            string ap = Path.Combine(_skillRoot, "adapters", probeGameId, "adapter.json");
            if (File.Exists(ap))
            {
                try
                {
                    var ad = GameAdapter.Load(ap);
                    EnsureCdpReady(ad, port, line => Console.WriteLine(line));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"（适配包 {probeGameId} 加载失败，跳过自动启动：{ex.Message}）");
                }
            }
        }

        try
        {
            var report = ProbeEngine.Probe(port, titleFilter, line => Console.WriteLine(line));

            Console.WriteLine();
            Console.WriteLine("================ 探测报告 ================");
            Console.WriteLine($"引擎      : {report.DetectedEngine}");
            Console.WriteLine($"浏览器    : {report.Browser}");
            Console.WriteLine($"目标页面  : {report.TargetTitle}");
            Console.WriteLine($"主存储    : {report.LocalStorageDb ?? "(未发现)"}");

            var withVars = report.Objects.Where(o => o.Vars.Count > 0).ToList();
            Console.WriteLine($"对象类    : {report.Objects.Count} 个有实例，其中 {withVars.Count} 个带变量");

            if (report.Bindings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("语义映射建议:");
                foreach (var b in report.Bindings)
                {
                    string where = b.Kind == "instance_var"
                        ? $"{b.ObjectName}.{b.VarName}"
                        : $"IndexedDB[{b.StorageKey}]";
                    Console.WriteLine($"  {Semantics.Display(b.Semantic),-8} {b.Semantic,-16} → {where,-28} {(b.Realtime ? "实时" : "需重载")}");
                    Console.WriteLine($"           {b.Evidence}");
                }
            }

            if (report.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("警告:");
                foreach (string w in report.Warnings) Console.WriteLine("  ! " + w);
            }

            if (saveId != null)
            {
                var adapter = ProbeEngine.ToAdapter(report, saveId, saveName, saveExe);
                string dir = Path.Combine(_skillRoot, "adapters", saveId);
                string path = Path.Combine(dir, "adapter.json");
                adapter.Save(path);
                Console.WriteLine();
                Console.WriteLine($"适配包骨架已写入: {path}");
                Console.WriteLine("注意：自动生成的绑定未经实测，range 与正确性需人工确认后再使用。");
            }
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("[错误] " + ex.Message);
            return 1;
        }
    }

    /// <summary>adapters：列出已安装的适配包。</summary>
    private static int CmdAdapters()
    {
        string dir = Path.Combine(_skillRoot, "adapters");
        if (!Directory.Exists(dir))
        {
            Console.WriteLine("还没有安装任何适配包。");
            Console.WriteLine($"目录: {dir}");
            Console.WriteLine("用 `probe <port> --save <gameId> <name> <exe>` 生成骨架，再手工确认绑定。");
            return 0;
        }

        var files = Directory.GetFiles(dir, "adapter.json", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            Console.WriteLine("adapters 目录存在但没有任何 adapter.json。");
            return 0;
        }

        Console.WriteLine($"已安装 {files.Length} 个适配包:");
        foreach (string f in files)
        {
            try
            {
                var ad = GameAdapter.Load(f);
                int known = ad.Bindings.Count(b => b.Range != null);
                Console.WriteLine($"  {ad.Id,-16} {ad.Name,-24} 引擎={ad.Engine,-14} exe={ad.Exe}");
                Console.WriteLine($"      语义绑定 {ad.Bindings.Count} 个（含区间 {known} 个）  {(ad.Mode == "readonly" ? "只读" : ad.Mode)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {Path.GetFileName(Path.GetDirectoryName(f)),-16} [无效] {ex.Message.Split('\n')[0]}");
            }
        }
        return 0;
    }

    /// <summary>
    /// sem &lt;gameId&gt; [read|write &lt;语义&gt; &lt;值&gt;] [--dry-run]
    /// 按语义读写。read 不带语义则读全部已绑定语义。
    /// </summary>
    private static int CmdSemantic(string[] a)
    {
        if (a.Length < 2)
        {
            Console.Error.WriteLine("用法: sem <gameId> [read [语义] | write <语义> <值>] [--dry-run]");
            return 2;
        }

        string path = Path.Combine(_skillRoot, "adapters", a[1], "adapter.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"找不到适配包: {path}");
            return 2;
        }

        GameAdapter adapter;
        try { adapter = GameAdapter.Load(path); }
        catch (Exception ex) { WriteError("[错误] " + ex.Message); return 1; }

        string action = a.Length > 2 ? a[2].ToLowerInvariant() : "read";
        bool dryRun = a.Contains("--dry-run");

        try
        {
            // 方案 A：端口不通就自动启动，使用者不用记调试参数
            if (adapter.Engine != "native_memory")
            {
                EnsureCdpReady(adapter, adapter.Connection.DebugPort, line => Console.WriteLine(line));
            }

            if (action == "read")
            {
                var wanted = a.Length > 3 && !a[3].StartsWith("-")
                    ? new List<string> { a[3] }
                    : adapter.Bindings.Select(b => b.Semantic).ToList();

                Console.WriteLine($"{adapter.Name}  引擎={adapter.Engine}");
                var results = AdapterRunner.Read(adapter, wanted);
                foreach (var r in results)
                {
                    if (r.Ok)
                        Console.WriteLine($"  {Semantics.Display(r.Semantic),-8} {r.Semantic,-16} = {r.Value,-12} ({r.Where})");
                    else
                        Console.WriteLine($"  {Semantics.Display(r.Semantic),-8} {r.Semantic,-16} ! {r.Error}");
                }
                return 0;
            }

            if (action == "write")
            {
                if (a.Length < 5) { Console.Error.WriteLine("用法: sem <gameId> write <语义> <值> [--dry-run]"); return 2; }
                var r = AdapterRunner.Write(adapter, a[3], a[4], dryRun);
                if (!r.Ok)
                {
                    Console.Error.WriteLine($"[失败] {r.Semantic}: {r.Error}");
                    return 1;
                }
                Console.WriteLine($"{(dryRun ? "(dry-run) " : "")}{Semantics.Display(r.Semantic)}: {r.Before} → {r.After}" +
                                  (r.Realtime ? "  [实时]" : "  [需重载页面]"));
                return 0;
            }

            Console.Error.WriteLine("未知动作: " + action);
            return 2;
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
        // 原先用 double.TryParse(rawValue, out _) 判断"是否数字"，两个问题：
        //   1. 不带 InvariantCulture，受系统区域影响
        //   2. 接受 NaN —— "NaN" 被当数字裸插，直接写坏游戏变量
        //      （本会话早期实测过 orEver 变 NaN，界面显示异常且难恢复）
        // 现在与 sem/jsapply/jsset 同规则：数字必须是有限值，其余按字符串 JSON 序列化。
        string jsVal;
        if (double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double num))
        {
            if (double.IsNaN(num) || double.IsInfinity(num))
            {
                Console.Error.WriteLine($"[拒绝] 值 \"{rawValue}\" 是 NaN/Infinity，不是有效的游戏数值。");
                return 3;
            }
            jsVal = num.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            jsVal = JsonSerializer.Serialize(rawValue);
        }

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
  // dry-run 不落写入，after 取计划值——读回旧值会输出 before==after 误导使用者
  const after = DRY ? VAL : (inst.SetInstanceVariableValue(idx, VAL), inst.GetInstanceVariableValue(idx));
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

        // 与 ApplyInstanceVar 同一条判定：防止 jsapply <id> <patch> "1); evil((" 注入
        if (!JsLiteral.TryPrepare(rawValue, patch.ValueType, out string jsVal))
        {
            Console.Error.WriteLine($"[拒绝] 值 \"{rawValue}\" 不是合法数字（patch {patch.Id} valueType={patch.ValueType}）。");
            return 3;
        }
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

        // type 非 string 即按 number 判定：必须是有限数字，否则拒绝（防命令行注入 JS）
        if (!JsLiteral.TryPrepare(raw, type, out string jsVal))
        {
            Console.Error.WriteLine($"[拒绝] 值 \"{raw}\" 按 type={type} 不是合法数字；要写非数字请加 --type string。");
            return 3;
        }

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
        {
            // 适配包体系上线后，多数游戏只有 adapters/<id>/adapter.json，没有旧 profile。
            // 直接报「找不到 profile」会让人以为配置丢了，所以显式指向新入口。
            string adapterPath = Path.Combine(_skillRoot, "adapters", idOrPath, "adapter.json");
            if (File.Exists(adapterPath))
                throw new InvalidOperationException(
                    $"「{idOrPath}」是适配包，不是旧 profile。\n" +
                    $"请改用：sem {idOrPath} read / sem {idOrPath} write <语义> <值>\n" +
                    $"（jsapply 仅用于旧的 profiles/*.json 体系）");

            throw new InvalidOperationException(
                $"找不到 profile 或适配包：{idOrPath}\n" +
                $"  已查找: {path}\n" +
                $"  已查找: {adapterPath}");
        }
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

适配包（Game Adapter Pack，推荐用法）:
  adapters                              列出已安装的适配包
  probe    <port> [--save <id> <名> <exe>]  只读探测目标游戏，可生成适配包骨架
  sem      <gameId> read [语义]         按语义读取（health/gold/energy/attack...）
  sem      <gameId> write <语义> <值>   按语义写入（带区间校验）
  sem      <gameId> write <语义> <值> --dry-run

  launch   <gameId> [--no-wait]         替用户带调试参数启动游戏并等端口就绪
  status   <gameId>                     检查进程 / 调试端口 / 绑定状态

  适配包放在 adapters/<gameId>/adapter.json，一个游戏一份。
  新游戏适配流程见 docs/GAME-ADAPTER-GUIDE.md。

type 可选: byte int16 uint16 int32 uint32 int64 uint64 float double

安全说明:
  * 检测到反作弊组件时强制只读，拒绝一切写入（不提供任何绕过）
  * 所有写入都要求签名唯一 + 前值/区间校验通过，否则一个字节都不写
  * apply 不加 --dry-run 也会先做完整校验，校验失败即中止
");
    }
}
