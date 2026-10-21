using GameTrainer.Core;
using GameTrainer.Memory;
using GameTrainer.Profiles;

namespace GameTrainer.Engine;

internal sealed class ApplyResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public List<string> Details { get; init; } = new();
    public static ApplyResult Success(string msg, List<string> details = null)
        => new() { Ok = true, Message = msg, Details = details ?? new() };
    public static ApplyResult Fail(string msg, params string[] details)
        => new() { Ok = false, Message = msg, Details = details.ToList() };
}

/// <summary>
/// 修改执行引擎。所有写入路径都必须经过这里，因为这里是唯一实施安全护栏的地方。
///
/// 护栏顺序（任一失败即中止，不写入一个字节）：
///   1. 反作弊闸   2. profile 校验   3. 签名唯一性   4. 前值校验
///   5. 区间校验   6. 字节上限      7. 记录回滚点 → 才允许写
/// </summary>
internal sealed class PatchEngine
{
    private readonly ProcessMemory _mem;
    private readonly GameProfile _profile;
    private readonly AuditLog _log;
    private readonly RollbackStack _rollback;
    private readonly ActiveStateStore _activeState;
    private readonly Dictionary<string, FreezeTask> _freezes = new();
    private bool _readonlyForced;
    private string _readonlyReason;

    public bool IsActive(string patchId) => _freezes.ContainsKey(patchId);
    public IReadOnlyList<string> ActivePatches => _freezes.Keys.ToList();

    public PatchEngine(ProcessMemory mem, GameProfile profile, AuditLog log, RollbackStack rollback,
                       ActiveStateStore activeState = null)
    {
        _mem = mem;
        _profile = profile;
        _log = log;
        _rollback = rollback;
        _activeState = activeState;
    }

    /// <summary>应用反作弊闸结果。命中则本引擎永久只读。</summary>
    public void ApplyAntiCheat(Profiles.AntiCheatSpec spec)
    {
        AntiCheatGuard.Result r = AntiCheatGuard.Check(_mem);

        // 追加 profile 自定义模块检查
        var extra = new List<string>();
        foreach (string m in spec?.Modules ?? new List<string>())
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName(
                        m.EndsWith(".exe") ? m[..^4] : m).Length > 0)
                    extra.Add($"profile 指定模块: {m}");
            }
            catch { }
        }

        bool blocked = r.Blocked || extra.Count > 0;
        if (!blocked) return;

        _readonlyForced = true;
        _readonlyReason = r.Blocked ? r.Reason : "profile 指定了不可共存的模块：" + string.Join(", ", extra);
        _log.Record("anticheat_block", new { reason = _readonlyReason, found = r.Found.Concat(extra) });
    }

    public bool ReadOnlyForced => _readonlyForced;
    public string ReadOnlyReason => _readonlyReason;

    /// <summary>启用一个 patch。dryRun=true 时只校验、不写入。</summary>
    public ApplyResult Enable(string patchId, bool dryRun = false)
    {
        if (_readonlyForced)
            return ApplyResult.Fail("反作弊闸已阻断：" + _readonlyReason);

        PatchDef patch = _profile.Patches.FirstOrDefault(p => p.Id == patchId);
        if (patch == null) return ApplyResult.Fail($"profile 中不存在 patch: {patchId}");

        if (patch.Kind == "save")
            return ApplyResult.Fail("存档类 patch 需经由 SaveEngine 处理，不走内存引擎。");

        if (_freezes.ContainsKey(patchId))
            return ApplyResult.Fail($"patch {patchId} 已处于启用状态。");

        var details = new List<string>();
        var planned = new List<(IntPtr Addr, byte[] Before, byte[] After, string Label)>();

        foreach (WriteDef w in patch.Writes)
        {
            var r = ResolveAndVerify(w, patch.Id);
            if (!r.Ok) return ApplyResult.Fail($"patch {patchId} 校验失败，未写入任何字节。", r.Message);
            planned.AddRange(r.Planned);
            details.AddRange(r.Details);
        }

        int totalBytes = planned.Sum(p => p.After.Length);
        int limit = Math.Max(_profile.Rollback.MaxBytes, planned.Count * 8);
        if (totalBytes > limit)
            return ApplyResult.Fail(
                $"写入字节数 {totalBytes} 超过上限 {limit}。若确需此操作，请在 profile 中调大 rollback.maxBytes。");

        if (dryRun)
        {
            _log.Record("dry_run", new { patch = patchId, planned = planned.Select(p => new
            {
                addr = $"0x{p.Addr.ToInt64():X}",
                before = Hex(p.Before),
                after = Hex(p.After),
                label = p.Label
            }) });
            return ApplyResult.Success(
                $"[dry-run] patch {patchId} 校验通过，共 {planned.Count} 处、{totalBytes} 字节，未写入。", details);
        }

        // 执行写入，逐条记录回滚点
        int written = 0;
        try
        {
            foreach (var p in planned)
            {
                _mem.Write(p.Addr, p.After);
                _rollback.Push(new WriteRecord
                {
                    ProfileId = _profile.Id,
                    PatchId = patchId,
                    Target = _profile.Exe,
                    Address = p.Addr.ToInt64(),
                    Before = p.Before,
                    After = p.After,
                    ModuleName = p.Label,
                });
                // 审计日志失败绝不能触发下面的回滚分支：
                // 内存已经写成功，日志写不进去只该降级为"少一条记录"，
                // 否则会出现「磁盘满 → 已生效的修改被莫名其妙全部回滚」。
                try
                {
                    _log.Write("write", _profile.Exe, p.Label, Hex(p.Before), Hex(p.After), p.After.Length);
                }
                catch { }
                written++;
            }
        }
        catch (Exception ex)
        {
            // 部分写入：立刻把已写的回滚掉，保证不留下半截状态
            int reverted = RevertAllPending();
            _log.Record("write_failed", new { patch = patchId, error = ex.Message, reverted });
            return ApplyResult.Fail(
                $"写入过程中失败，已回滚 {reverted} 处已写数据：{ex.Message}");
        }

        if (patch.Freeze) StartFreeze(patch, planned);

        // 落盘激活记录：修改器若被强杀，下次启动能凭此发现残留
        _activeState?.MarkActive(new ActiveStateStore.Entry
        {
            ProfileId = _profile.Id,
            ProfileName = _profile.Name,
            Exe = _profile.Exe,
            PatchId = patchId,
            PatchLabel = patch.Label,
            Pid = _mem.Pid,
            ProcessCreateTime = _mem.Identity.CreateTimeUtcTicks,
            ActivatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            CanRestoreByProfile = patch.Writes.Any(w => !string.IsNullOrWhiteSpace(w.ExpectBefore)
                                                     && !string.IsNullOrWhiteSpace(w.Aob))
        });

        return ApplyResult.Success(
            $"patch {patchId} 已启用：{written} 处、{totalBytes} 字节"
            + (patch.Freeze ? $"（持续锁定，间隔 {patch.FreezeIntervalMs}ms）" : "（一次性写入）"),
            details);
    }

    public ApplyResult Disable(string patchId)
    {
        if (_freezes.TryGetValue(patchId, out FreezeTask task))
        {
            task.Stop();
            _freezes.Remove(patchId);
        }

        int reverted = RevertPatch(patchId);

        // 没有任何回滚记录时绝不能报"成功"——那会让使用者以为已还原，实际游戏里还留着改过的值。
        if (reverted == 0 && _rollback.Entries.All(e => e.PatchId != patchId))
        {
            return ApplyResult.Fail(
                $"patch {patchId} 没有可用的回滚记录。",
                "回滚记录只存在于执行写入的那个进程内。若修改器已被强制结束，",
                "跨进程无法得知原值——请用 revert-all 按 profile 语义重新定位后还原，",
                "或直接让游戏回滚到更早的存档。");
        }

        _activeState?.MarkInactive(_profile.Id, patchId);
        return ApplyResult.Success($"patch {patchId} 已停用，回滚 {reverted} 处。");
    }

    /// <summary>
    /// 按 profile 语义还原：重新解析 AOB/指针链定位到当前地址，
    /// 把 expect_before 的值写回去。用于"修改器异常退出后"的补救。
    /// 前提：profile 里写了 expectBefore，且游戏当前值仍是 patch 写入的值。
    /// </summary>
    public ApplyResult RestoreByProfile(string patchId)
    {
        PatchDef patch = _profile.Patches.FirstOrDefault(p => p.Id == patchId);
        if (patch == null) return ApplyResult.Fail($"profile 中不存在 patch: {patchId}");
        if (patch.Kind != "memory") return ApplyResult.Fail($"patch {patchId} 不是内存类。");

        var lines = new List<string>();
        int restored = 0;

        foreach (WriteDef w in patch.Writes)
        {
            if (string.IsNullOrWhiteSpace(w.ExpectBefore))
            {
                lines.Add("  跳过：该写入未配置 expectBefore，无法推断原值。");
                continue;
            }
            if (string.IsNullOrWhiteSpace(w.Aob))
            {
                lines.Add("  跳过：仅支持通过 aob 重新定位（chain 的原值无法安全推断）。");
                continue;
            }

            NumType vt = w.NumType;
            if (!ValueCodec.TryParse(w.ExpectBefore, vt, out object original))
            {
                lines.Add($"  跳过：expectBefore \"{w.ExpectBefore}\" 无法解析为 {w.Type}。");
                continue;
            }

            byte[] originalBytes = ValueCodec.Encode(original, vt);
            int size = originalBytes.Length;

            // 用"目标值"替换签名里可能包含的原值字节，重新定位
            var pat = new AobPattern(w.Aob);
            var hits = FindAob(pat, w, out string err);
            if (err != null) { lines.Add("  定位失败：" + err); continue; }
            if (hits.Count != 1)
            {
                lines.Add($"  定位失败：签名命中 {hits.Count} 处，需唯一。");
                continue;
            }

            IntPtr addr = hits[0];
            foreach (string off in w.Offsets) addr = new IntPtr(addr.ToInt64() + ParseOffset(off));

            byte[] cur = new byte[size];
            if (!_mem.TryRead(addr, cur, out _)) { lines.Add($"  0x{addr.ToInt64():X} 不可读。"); continue; }

            byte[] patchedBytes = ValueCodec.Encode(
                ValueCodec.TryParse(w.Value, vt, out object pv) ? pv : original, vt);

            if (!cur.AsSpan().SequenceEqual(patchedBytes))
            {
                lines.Add($"  0x{addr.ToInt64():X} 当前值不是本 patch 写入的值，跳过以免覆盖游戏进度。");
                continue;
            }

            _mem.Write(addr, originalBytes);
            _log.Write("restore_by_profile", _profile.Exe,
                       $"aob-restore+{string.Join(',', w.Offsets)}",
                       Hex(cur), Hex(originalBytes), size);
            lines.Add($"  0x{addr.ToInt64():X} 已还原为 {w.ExpectBefore}");
            restored++;
        }

        if (restored > 0) _activeState?.MarkInactive(_profile.Id, patchId);

        return restored > 0
            ? ApplyResult.Success($"按 profile 还原 {restored} 处。", lines)
            : ApplyResult.Fail("未能还原任何字段。", lines.ToArray());
    }

    /// <summary>回滚指定 patch 的所有写入记录（逆序）。</summary>
    private int RevertPatch(string patchId)
    {
        var all = _rollback.Entries.Where(e => e.PatchId == patchId).Reverse().ToList();
        int n = 0;
        foreach (var e in all)
        {
            try
            {
                _mem.Write(new IntPtr(e.Address), e.Before);
                _log.Write("rollback", _profile.Exe, e.ModuleName, Hex(e.After), Hex(e.Before), e.Before.Length);
                n++;
            }
            catch (Exception ex)
            {
                _log.Record("rollback_failed", new { addr = $"0x{e.Address:X}", error = ex.Message });
            }
        }
        return n;
    }

    /// <summary>回滚所有尚未回滚的记录。用于"一键关闭全部"与退出清理。</summary>
    public int RevertAllPending()
    {
        var drained = _rollback.Drain();
        int n = 0;
        foreach (var e in drained)
        {
            try
            {
                _mem.Write(new IntPtr(e.Address), e.Before);
                _log.Write("rollback", _profile.Exe, e.ModuleName, Hex(e.After), Hex(e.Before), e.Before.Length);
                n++;
            }
            catch (Exception ex)
            {
                _log.Record("rollback_failed", new { addr = $"0x{e.Address:X}", error = ex.Message });
            }
        }
        return n;
    }

    /// <summary>
    /// 适配包专用：按 AOB 签名定位目标地址（只读）。
    ///
    /// 复用 FindAob 的扫描与模块过滤逻辑，不重复实现一遍。
    /// 返回的地址已叠加 offsets。
    /// </summary>
    public List<IntPtr> ResolveAddresses(string aob, IReadOnlyList<string> offsets,
                                         string module, bool requireUnique, out string error)
    {
        error = null;
        var result = new List<IntPtr>();

        AobPattern pat;
        try { pat = new AobPattern(aob); }
        catch (Exception ex) { error = $"AOB 语法错误：{ex.Message}"; return result; }

        // 借用 FindAob：它只需要 Module 字段
        var probe = new WriteDef { Aob = aob, Module = module };
        List<IntPtr> hits = FindAob(pat, probe, out error);
        if (error != null) return result;

        if (hits.Count == 0)
        {
            error = "AOB 未命中当前内存（游戏可能已更新，或该状态尚未加载）。";
            return result;
        }
        if (requireUnique && hits.Count > 1)
        {
            error = $"AOB 命中 {hits.Count} 处（要求唯一）。请在签名中加入更多上下文。";
            return result;
        }

        foreach (IntPtr hit in hits)
        {
            IntPtr addr = hit;
            if (offsets != null)
                foreach (string off in offsets)
                    addr = new IntPtr(addr.ToInt64() + ParseOffset(off));
            result.Add(addr);
        }
        return result;
    }

    /// <summary>
    /// 适配包专用：读一个地址的原始字节。失败返回 null。
    /// </summary>
    public byte[] ReadBytes(IntPtr address, int size)
    {
        try { return _mem.Read(address, size); }
        catch { return null; }
    }

    /// <summary>
    /// 适配包专用：写一个地址，并把回滚点压栈。
    ///
    /// 与 Enable 的区别：不做 profile 里的 expectBefore/range 校验
    /// （适配包在 AdapterRunner 层已做过区间校验），
    /// 但仍然记录 before，保证可回滚。
    /// </summary>
    public bool WriteBytes(IntPtr address, byte[] after, out string error)
    {
        error = null;
        try
        {
            byte[] before = _mem.Read(address, after.Length);
            _rollback.Push(new WriteRecord
            {
                ProfileId = _profile?.Id,
                PatchId = null,
                Target = $"adapter:0x{address.ToInt64():X}",
                Address = address.ToInt64(),
                Before = before,
                After = after,
                ModuleName = null
            });

            _mem.Write(address, after);

            // 审计日志失败不能影响写入结果的判定：内存已经改了，
            // 若这里抛异常被外层 catch，就会对使用者谎报"写入失败"，
            // 而实际上值已经生效。日志只降级提示，不改变返回值。
            try
            {
                _log.Write("write", _profile?.Exe ?? "adapter", null, Hex(after), Hex(before), after.Length);
            }
            catch
            {
                // 磁盘满 / 目录只读 / 被锁 —— 都不该让"已成功写入"变成"写入失败"
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"写入失败：{ex.Message}";
            return false;
        }
    }

    public void StopAllFreezes()
    {
        foreach (var t in _freezes.Values) t.Stop();
        _freezes.Clear();
    }

    /// <summary>核心：解析地址 + 全部前置校验。不通过则返回失败，绝不写入。</summary>
    private (bool Ok, string Message, List<string> Details,
             List<(IntPtr Addr, byte[] Before, byte[] After, string Label)> Planned)
        ResolveAndVerify(WriteDef w, string patchId)
    {
        var details = new List<string>();
        var planned = new List<(IntPtr, byte[], byte[], string)>();
        NumType vtype = w.NumType;
        int size = ValueCodec.SizeOf(vtype);

        if (!ValueCodec.TryParse(w.Value, vtype, out object targetValue))
            return (false, $"值 \"{w.Value}\" 无法解析为 {w.Type}", details, planned);

        byte[] after = ValueCodec.Encode(targetValue, vtype);

        // 区间校验
        if (w.Range is { Length: 2 })
        {
            double v = ValueCodec.ToDouble(targetValue);
            if (double.IsNaN(v) || v < w.Range[0] || v > w.Range[1])
                return (false, $"目标值 {ValueCodec.Format(targetValue)} 超出允许区间 "
                             + $"[{w.Range[0]}, {w.Range[1]}]，拒绝写入。", details, planned);
        }

        if (!string.IsNullOrWhiteSpace(w.Aob))
        {
            var pat = new AobPattern(w.Aob);
            List<IntPtr> hits = FindAob(pat, w, out string err);
            if (err != null) return (false, err, details, planned);

            if (hits.Count == 0)
            {
                // 区分两种原因：签名本身失效，还是签名里嵌了会被本 patch 改掉的字节。
                // 后者极常见——AOB 常包含目标字段的原值，写入一次后签名自然不再命中。
                string hint = LooksLikeSelfInvalidating(w.Aob)
                    ? "签名中包含可能被本次写入改动的字段字节（写一次后签名即失效）。"
                      + "这是预期行为：回滚后签名会重新命中。若需反复启用，请把这类字节改成 ?? 通配。"
                    : "签名未命中当前内存，可能是游戏已更新或该状态尚未加载。";
                return (false, $"AOB 未命中：{w.Aob}\n  说明：{hint}", details, planned);
            }
            if (w.RequireUnique && hits.Count > 1)
                return (false, $"AOB 命中 {hits.Count} 处（要求唯一）：{w.Aob}。"
                             + "请在签名中加入更多上下文或改用 chain。", details, planned);

            foreach (IntPtr hit in hits)
            {
                IntPtr addr = hit;
                foreach (string off in w.Offsets)
                {
                    long delta = ParseOffset(off);
                    addr = new IntPtr(addr.ToInt64() + delta);
                }
                if (!CheckReadable(addr, size, out string e2))
                    return (false, e2, details, planned);
                if (!ReadAndVerifyBefore(addr, size, w, vtype, out byte[] before, out string e3))
                    return (false, e3, details, planned);

                planned.Add((addr, before, after, $"aob:{w.Aob}[0x{hit.ToInt64():X}]+{string.Join(',', w.Offsets)}"));
                details.Add($"  AOB 命中 0x{hit.ToInt64():X} → 写入地址 0x{addr.ToInt64():X}");
            }
        }
        else if (w.Chain is { Count: > 0 })
        {
            if (string.IsNullOrWhiteSpace(w.Module))
                return (false, "使用 chain 时必须指定 module。", details, planned);

            IntPtr addr;
            try
            {
                addr = ResolveChain(w.Module, w.Chain, out string trace);
                details.Add("  指针链: " + trace);
            }
            catch (Exception ex)
            {
                return (false, "指针链解析失败：" + ex.Message, details, planned);
            }

            if (!CheckReadable(addr, size, out string e2))
                return (false, e2, details, planned);
            if (!ReadAndVerifyBefore(addr, size, w, vtype, out byte[] before, out string e3))
                return (false, e3, details, planned);

            planned.Add((addr, before, after, $"chain:{w.Module}+{string.Join(',', w.Chain)}"));
            details.Add($"  指针链终点 0x{addr.ToInt64():X}");
        }
        else
        {
            return (false, "必须提供 aob 或 chain。", details, planned);
        }

        return (true, null, details, planned);
    }

    /// <summary>
    /// 判断签名是否"自带失效风险"。
    ///
    /// 真正的判断需要比较「签名里的固定字节」与「本次要写入的字节」是否有重叠，
    /// 但 AOB 与写入值的大小/位置都不固定，无法在这里做精确比对。
    /// 因此这里只做**保守启发式**：完全没有通配符的签名，一旦它覆盖了被改字段，
    /// 写一次就不再命中；含通配符的签名的常见写法是把易变字段挖掉，风险较低。
    ///
    /// 只用于给使用者提示，不影响是否允许写入。
    /// </summary>
    private static bool LooksLikeSelfInvalidating(string aob)
    {
        if (string.IsNullOrWhiteSpace(aob)) return false;
        // 含通配符 = 作者主动避开了可变字段，视为已处理
        if (aob.Contains('?')) return false;
        // 纯固定字节签名：存在覆盖被改字段的可能性
        return true;
    }

    private bool ReadAndVerifyBefore(IntPtr addr, int size, WriteDef w, NumType vtype,
        out byte[] before, out string error)
    {
        before = new byte[size];
        error = null;
        if (!_mem.TryRead(addr, before, out _))
        {
            error = $"0x{addr.ToInt64():X} 读取失败。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(w.ExpectBefore))
        {
            if (!ValueCodec.TryParse(w.ExpectBefore, vtype, out object expect))
            {
                error = $"expect_before \"{w.ExpectBefore}\" 无法解析为 {w.Type}。";
                return false;
            }
            byte[] expectBytes = ValueCodec.Encode(expect, vtype);
            if (!before.AsSpan().SequenceEqual(expectBytes))
            {
                error = $"0x{addr.ToInt64():X} 前值校验失败：期望 {ValueCodec.Format(expect)}"
                      + $"（{Hex(expectBytes)}），实际 {ValueCodec.Format(ValueCodec.Decode(before, vtype))}"
                      + $"（{Hex(before)}）。已拒绝写入。";
                return false;
            }
        }
        return true;
    }

    private bool CheckReadable(IntPtr addr, int size, out string error)
    {
        error = null;
        var region = _mem.DescribeRegion(addr, size);
        if (region == null)
        {
            error = $"0x{addr.ToInt64():X} 不在任何已提交区域，拒绝操作。";
            return false;
        }
        if (!Native.NativeMethods.IsReadable(region.Protect))
        {
            error = $"0x{addr.ToInt64():X} 所在页不可读（保护属性 {Native.NativeMethods.ProtectName(region.Protect)}）。";
            return false;
        }
        return true;
    }

    /// <summary>解析偏移字符串。支持 "-0x18" / "+0x18" / "-24" / "0x18"。
    /// 独立成 internal 是为了让单元测试直接覆盖——写反符号会静默读到错误地址。</summary>
    internal static long ParseOffset(string s)
    {
        string t = s.Trim();
        bool negative = false;

        // 支持 "-0x18" / "+0x18" / "-24" / "0x18" 四种写法。
        // 负号必须在十六进制转换之前取出，否则 Convert.ToInt64("-18",16) 会抛格式错误。
        if (t.StartsWith("-")) { negative = true; t = t[1..].Trim(); }
        else if (t.StartsWith("+")) { t = t[1..].Trim(); }

        long value;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                throw new FormatException($"无法解析偏移 \"{s}\"。");
        }
        else
        {
            if (!long.TryParse(t, out value))
                throw new FormatException($"无法解析偏移 \"{s}\"。");
        }

        return negative ? -value : value;
    }

    /// <summary>在目标进程的各个可读区域内搜索 AOB。</summary>
    private List<IntPtr> FindAob(AobPattern pat, WriteDef w, out string error)
    {
        error = null;
        var hits = new List<IntPtr>();

        IEnumerable<(IntPtr Base, long Size, uint Protect, uint Type)> regions = _mem.Regions;

        if (!string.IsNullOrWhiteSpace(w.Module))
        {
            // 限定模块范围
            try
            {
                IntPtr modBase = _mem.ModuleBase(w.Module);
                System.Diagnostics.ProcessModule pm = null;
                foreach (System.Diagnostics.ProcessModule m in _mem.Process.Modules)
                    if (string.Equals(m.ModuleName, w.Module, StringComparison.OrdinalIgnoreCase)) pm = m;
                if (pm == null) { error = $"模块 {w.Module} 未加载。"; return hits; }
                long start = modBase.ToInt64(), end = start + pm.ModuleMemorySize;
                regions = _mem.Regions.Where(r =>
                {
                    long rb = r.Base.ToInt64(), re = rb + r.Size;
                    return rb < end && re > start;
                });
            }
            catch (Exception ex)
            {
                error = $"定位模块 {w.Module} 失败：{ex.Message}";
                return hits;
            }
        }

        foreach (var r in regions)
        {
            if (!Native.NativeMethods.IsReadable(r.Protect)) continue;
            if (r.Size > 256L * 1024 * 1024) continue;

            byte[] buf;
            try { buf = _mem.Read(r.Base, (int)r.Size); }
            catch { continue; }

            int idx = 0;
            while (true)
            {
                int rel = pat.FirstMatch(buf.AsSpan(idx));
                if (rel < 0) break;
                int abs = idx + rel;
                hits.Add(new IntPtr(r.Base.ToInt64() + abs));
                if (hits.Count > 8) return hits; // 够多就停，反正不唯一
                idx = abs + 1;
                if (idx >= buf.Length - pat.Length) break;
            }
        }
        return hits;
    }

    /// <summary>
    /// 适配包专用：解析指针链，返回终点地址（只读）。
    ///
    /// chain 的语义：模块基址 + 偏移1 → 解引用 → + 偏移2 → 解引用 → … → 终点。
    /// 与 AOB 相比的优势：基址 + 偏移不随游戏版本变化，
    /// 只有结构体布局变了才需要调整，比字节签名稳定得多。
    /// </summary>
    public IntPtr ResolveChainAddress(string module, IReadOnlyList<string> chain, out string trace, out string error)
    {
        error = null;
        try
        {
            return ResolveChain(module, chain.ToList(), out trace);
        }
        catch (Exception ex)
        {
            trace = null;
            error = $"指针链解析失败：{ex.Message}";
            return IntPtr.Zero;
        }
    }

    private IntPtr ResolveChain(string module, List<string> chain, out string trace)
    {
        var sb = new System.Text.StringBuilder();

        long cur;
        if (string.IsNullOrWhiteSpace(module))
        {
            // 绝对地址模式：chain[0] 是起始地址（通常是一个指针槽）。
            //
            // 关键：起点本身必须先解引用，因为它存的是"指向数据的指针"。
            // 之后 chain[1..] 才是相对该数据的偏移。
            // 若只给一个元素，则视为"直接给出目标地址"，不解引用。
            cur = ParseOffset(chain[0]);
            sb.Append($"0x{cur:X}");

            if (chain.Count == 1)
            {
                trace = sb.ToString();
                return new IntPtr(cur);
            }

            byte[] rootPtr = new byte[8];
            if (!_mem.TryRead(new IntPtr(cur), rootPtr, out _))
                throw new InvalidOperationException($"链起点 0x{cur:X} 读取指针失败。");
            long rootNext = BitConverter.ToInt64(rootPtr, 0);
            if (rootNext == 0) throw new InvalidOperationException($"链起点 0x{cur:X} 指针为空。");
            sb.Append($" → [0x{cur:X}]=0x{rootNext:X}");
            cur = rootNext;

            chain = chain.Skip(1).ToList();
        }
        else
        {
            IntPtr baseAddr = _mem.ModuleBase(module);
            cur = baseAddr.ToInt64();
            sb.Append($"{module}+0x{baseAddr.ToInt64():X}");
        }

        for (int i = 0; i < chain.Count; i++)
        {
            long off = ParseOffset(chain[i]);
            bool last = i == chain.Count - 1;
            if (last)
            {
                cur += off;
                sb.Append($" +0x{off:X} → 0x{cur:X}");
            }
            else
            {
                cur += off;
                byte[] ptr = new byte[8];
                if (!_mem.TryRead(new IntPtr(cur), ptr, out _))
                    throw new InvalidOperationException($"链第 {i} 级 0x{cur:X} 读取指针失败。");
                long next = BitConverter.ToInt64(ptr, 0);
                if (next == 0) throw new InvalidOperationException($"链第 {i} 级 0x{cur:X} 指针为空。");
                sb.Append($" +0x{off:X} → [0x{cur:X}]=0x{next:X}");
                cur = next;
            }
        }
        trace = sb.ToString();
        return new IntPtr(cur);
    }

    private void StartFreeze(PatchDef patch, List<(IntPtr Addr, byte[] Before, byte[] After, string Label)> planned)
    {
        var task = new FreezeTask(planned.Select(p => (p.Addr, p.After)).ToList(),
                                  patch.FreezeIntervalMs, _mem, _log, patch.Id);
        task.Start();
        _freezes[patch.Id] = task;
    }

    private static string Hex(byte[] b) => string.Join(' ', b.Select(x => x.ToString("X2")));
}

/// <summary>
/// 持续锁定。定时把目标值写回去，与游戏自身的写入抢。
///
/// 已知风险（写进 skill 正文）：锁定期间游戏逻辑仍在推进，若某字段有派生关系
/// （例如"当前值 ≤ 上限"），只锁一端会导致状态不一致。这是 lock 模式的固有代价。
/// </summary>
internal sealed class FreezeTask
{
    private readonly List<(IntPtr Addr, byte[] Value)> _targets;
    private readonly int _intervalMs;
    private readonly ProcessMemory _mem;
    private readonly AuditLog _log;
    private readonly string _patchId;
    private CancellationTokenSource _cts;
    private Task _loop;

    public long WriteCount { get; private set; }

    public FreezeTask(List<(IntPtr, byte[])> targets, int intervalMs,
                      ProcessMemory mem, AuditLog log, string patchId)
    {
        _targets = targets;
        _intervalMs = Math.Max(50, intervalMs);
        _mem = mem;
        _log = log;
        _patchId = patchId;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(async () =>
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var (addr, val) in _targets)
                    {
                        byte[] cur = new byte[val.Length];
                        if (!_mem.TryRead(addr, cur, out _)) continue;
                        if (!cur.AsSpan().SequenceEqual(val))
                        {
                            _mem.Write(addr, val);
                            WriteCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Record("freeze_error", new { patch = _patchId, error = ex.Message });
                }

                try { await Task.Delay(_intervalMs, token); }
                catch (TaskCanceledException) { break; }
            }
        }, _cts.Token);
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _loop?.Wait(2000);
        }
        catch { }
        _cts?.Dispose();
        _cts = null;
    }
}
