using System.Diagnostics;
using System.Text;
using GameTrainer.Core;
using GameTrainer.Engine;
using GameTrainer.Memory;
using GameTrainer.Profiles;

namespace GameTrainer;

internal static class Program
{
    private static string _skillRoot;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        _skillRoot = ResolveSkillRoot();

        // --background：由开机自启注册项传入，启动后直接进托盘、不抢焦点
        if (args.Length > 0 && args[0] == "--background")
            return GuiForm.Run(_skillRoot, startBackground: true);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
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
            Console.Error.WriteLine($"[错误] {ex.Message}");
            if (Environment.GetEnvironmentVariable("GT_DEBUG") == "1")
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    /// <summary>定位 skill 根目录：优先 exe 所在目录上溯，再回退到环境变量。</summary>
    private static string ResolveSkillRoot()
    {
        string env = Environment.GetEnvironmentVariable("GT_SKILL_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        // exe 位于 <skill>/dist/ 或 <skill>/src/GameTrainer/bin/... 
        string dir = AppContext.BaseDirectory;
        var d = new DirectoryInfo(dir);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "SKILL.md"))) return d.FullName;
            d = d.Parent;
        }
        // 开发期：从 bin/Debug/net8.0-windows 上溯到 skill 根
        d = new DirectoryInfo(dir);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "profiles"))) return d.FullName;
        }
        return Directory.GetCurrentDirectory();
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
            hits.Add(new ScanHit(
                new IntPtr(h.GetProperty("addr").GetInt64()),
                new IntPtr(h.GetProperty("@base").GetInt64()),
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

type 可选: byte int16 uint16 int32 uint32 int64 uint64 float double

安全说明:
  * 检测到反作弊组件时强制只读，拒绝一切写入（不提供任何绕过）
  * 所有写入都要求签名唯一 + 前值/区间校验通过，否则一个字节都不写
  * apply 不加 --dry-run 也会先做完整校验，校验失败即中止
");
    }
}
