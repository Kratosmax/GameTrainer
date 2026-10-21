using GameTrainer.Core;
using GameTrainer.Engine;
using GameTrainer.Memory;
using GameTrainer.Profiles;

namespace GameTrainer.Adapter;

/// <summary>
/// 内存后端：让适配包能操作原生进程（Unity / UE / 任何无脚本层的游戏）。
///
/// 这是 skill 第零节「问题 3」里「没有语义层」那条路的实现。
/// 与 CDP 后端的根本差别：拿不到变量名，只能靠 AOB 签名定位地址。
///
/// 设计约束：
///   - 进程附加后必须做反作弊检查，命中即整体只读（沿用 PatchEngine 的规则）。
///   - 每次操作都重新附加并扫描，不缓存地址——进程重启后地址必然变化，
///     缓存反而会造成"写到随机地址"的危险。
///   - 写入前必须能读到前值并压入回滚栈，否则不写。
/// </summary>
internal static class MemoryBackend
{
    /// <summary>一次内存会话：附加 + 扫描 + 读写，用完即释放。</summary>
    public sealed class Session : IDisposable
    {
        public ProcessMemory Memory { get; }
        public PatchEngine Engine { get; }
        public bool ReadOnly { get; }
        public string BlockReason { get; }

        internal Session(ProcessMemory mem, PatchEngine engine, bool readOnly, string reason)
        {
            Memory = mem;
            Engine = engine;
            ReadOnly = readOnly;
            BlockReason = reason;
        }

        public void Dispose() => Memory?.Dispose();
    }

    /// <summary>
    /// 附加到适配包指定的进程。
    /// 返回 null 表示进程不存在；抛出异常表示附加失败。
    /// </summary>
    public static Session Attach(GameAdapter adapter, out string error)
    {
        error = null;
        string procName = Path.GetFileNameWithoutExtension(adapter.Exe);
        ProcessMemory mem;
        try
        {
            mem = ProcessMemory.AttachByName(procName);
        }
        catch (Exception ex)
        {
            error = $"无法附加到进程 {procName}：{ex.Message}\n" +
                    $"请先启动游戏（launch {adapter.Id}）。";
            return null;
        }

        // 反作弊闸：命中即只读，不提供任何绕过
        var guard = AntiCheatGuard.Check(mem);
        bool blocked = guard.Blocked;
        string reason = blocked ? "检测到反作弊，已切换为只读（不做绕过）" : null;

        // 适配包自定义模块检查
        foreach (string m in adapter.AntiCheatModules ?? new List<string>())
        {
            try
            {
                string n = m.EndsWith(".exe") ? m[..^4] : m;
                if (System.Diagnostics.Process.GetProcessesByName(n).Length > 0)
                {
                    blocked = true;
                    reason = $"检测到适配包指定的反作弊模块 {m}，已切换为只读";
                    break;
                }
            }
            catch { }
        }

        var profile = new GameProfile { Id = adapter.Id, Exe = adapter.Exe, Mode = adapter.Mode };
        // 审计日志路径与主程序保持一致：程序目录下的 audit.log。
        // 不引入全局单例，避免与 CLI 主流程的实例重复打开同一文件。
        var log = new AuditLog(Path.Combine(AppContext.BaseDirectory, "audit.log"));
        var rollback = new RollbackStack();
        var engine = new PatchEngine(mem, profile, log, rollback);

        return new Session(mem, engine, blocked, reason);
    }

    /// <summary>
    /// 解析绑定指向的地址。两种定位方式二选一：
    ///   chain —— 模块基址 + 指针链（优先：游戏更新后更稳）
    ///   aob   —— 字节签名 + 偏移
    /// </summary>
    private static bool TryResolveAddress(Session session, ValueBinding b, out IntPtr addr, out string error)
    {
        addr = IntPtr.Zero;
        error = null;

        if (b.Chain is { Count: > 0 })
        {
            // module 留空 = 绝对地址模式，chain[0] 直接作为起点
            addr = session.Engine.ResolveChainAddress(b.Module, b.Chain, out _, out error);
            return error == null && addr != IntPtr.Zero;
        }

        if (string.IsNullOrWhiteSpace(b.Aob))
        {
            error = "绑定既没有 aob 也没有 chain，无法定位地址。";
            return false;
        }

        var addrs = session.Engine.ResolveAddresses(b.Aob, b.Offsets, b.Module, requireUnique: true, out error);
        if (error != null) return false;
        if (addrs.Count == 0) { error = "AOB 未命中"; return false; }

        addr = addrs[0];
        return true;
    }

    /// <summary>按绑定读一个值。</summary>
    public static AdapterRunner.ReadResult Read(GameAdapter adapter, ValueBinding b)
    {
        var session = Attach(adapter, out string err);
        if (session == null)
            return new AdapterRunner.ReadResult(b.Semantic, false, null, b.Describe(), err);

        using (session)
        {
            if (!TryResolveAddress(session, b, out IntPtr addr, out string aerr))
                return new AdapterRunner.ReadResult(b.Semantic, false, null, b.Describe(), aerr);
            if (!TryParseType(b.Type, out NumType nt))
                return new AdapterRunner.ReadResult(b.Semantic, false, null, b.Describe(),
                    $"不支持的数值类型：{b.Type}");

            byte[] buf = session.Engine.ReadBytes(addr, ValueCodec.SizeOf(nt));
            if (buf == null)
                return new AdapterRunner.ReadResult(b.Semantic, false, null, b.Describe(),
                    $"地址 0x{addr.ToInt64():X} 读取失败（可能已失效）");

            object val = ValueCodec.Decode(buf, nt);
            string text = ValueCodec.Format(val);
            return new AdapterRunner.ReadResult(b.Semantic, true, text,
                $"{b.Describe()}@0x{addr.ToInt64():X}", null);
        }
    }

    /// <summary>按绑定写一个值。dryRun 时只读前值不写。</summary>
    public static AdapterRunner.WriteResult Write(GameAdapter adapter, ValueBinding b,
                                                  string rawValue, bool dryRun)
    {
        var session = Attach(adapter, out string err);
        if (session == null)
            return new AdapterRunner.WriteResult(b.Semantic, false, null, null, true, err);

        using (session)
        {
            if (session.ReadOnly)
                return new AdapterRunner.WriteResult(b.Semantic, false, null, null, true, session.BlockReason);

            if (!TryResolveAddress(session, b, out IntPtr addr, out string aerr))
                return new AdapterRunner.WriteResult(b.Semantic, false, null, null, true, aerr);
            if (!TryParseType(b.Type, out NumType nt))
                return new AdapterRunner.WriteResult(b.Semantic, false, null, null, true,
                    $"不支持的数值类型：{b.Type}");

            int size = ValueCodec.SizeOf(nt);
            byte[] beforeBuf = session.Engine.ReadBytes(addr, size);
            if (beforeBuf == null)
                return new AdapterRunner.WriteResult(b.Semantic, false, null, null, true,
                    $"地址 0x{addr.ToInt64():X} 读取失败，拒绝盲写");

            object beforeVal = ValueCodec.Decode(beforeBuf, nt);
            string beforeText = ValueCodec.Format(beforeVal);

            if (!ValueCodec.TryParse(rawValue, nt, out object target))
                return new AdapterRunner.WriteResult(b.Semantic, false, beforeText, null, true,
                    $"值 \"{rawValue}\" 无法解析为 {b.Type}");

            byte[] afterBuf = ValueCodec.Encode(target, nt);
            string afterText = ValueCodec.Format(target);

            if (dryRun)
                return new AdapterRunner.WriteResult(b.Semantic, true, beforeText, afterText, true, null);

            if (!session.Engine.WriteBytes(addr, afterBuf, out string werr))
                return new AdapterRunner.WriteResult(b.Semantic, false, beforeText, null, true, werr);

            return new AdapterRunner.WriteResult(b.Semantic, true, beforeText, afterText, true, null);
        }
    }

    /// <summary>把适配包的 type 字段映射到 NumType。</summary>
    private static bool TryParseType(string type, out NumType nt)
    {
        nt = NumType.Int32;
        switch ((type ?? "double").Trim().ToLowerInvariant())
        {
            case "byte": case "uint8": nt = NumType.Byte; return true;
            case "short": case "int16": nt = NumType.Int16; return true;
            case "int": case "int32": nt = NumType.Int32; return true;
            case "long": case "int64": nt = NumType.Int64; return true;
            case "float": case "single": nt = NumType.Float; return true;
            case "double": nt = NumType.Double; return true;
            default: return false;
        }
    }
}
