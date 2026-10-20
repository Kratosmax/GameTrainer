using GameTrainer.Memory;

namespace GameTrainer.Engine;

internal sealed record ScanHit(IntPtr Address, IntPtr RegionBase, uint Protect, string ModuleName);

/// <summary>
/// 扫描工作台。做法与常规内存修改器一致：首扫 → 游戏内改变数值 → 再扫收紧。
/// 所有扫描都只读，不做任何写入。
/// </summary>
internal sealed class Scanner
{
    private readonly ProcessMemory _mem;
    public List<ScanHit> Results { get; private set; } = new();

    public Scanner(ProcessMemory mem) => _mem = mem;

    public sealed class ScanOptions
    {
        public NumType Type = NumType.Double;
        /// <summary>只扫描可写区（堆/数据），跳过代码段与只读区。默认开。</summary>
        public bool WritableOnly = true;
        public bool IncludeImage = false;
        public long MaxResults = 2_000_000;
        public bool Aligned = true;
    }

    public List<ScanHit> FirstScan(object exactValue, ScanOptions opt)
    {
        byte[] needle = ValueCodec.Encode(exactValue, opt.Type);
        int size = needle.Length;
        var hits = new List<ScanHit>();
        var moduleRanges = ModuleRanges();

        foreach (var region in _mem.Regions)
        {
            if (opt.WritableOnly && !Native.NativeMethods.IsWritable(region.Protect)) continue;
            if (!opt.IncludeImage && region.Type == Native.NativeMethods.MEM_IMAGE) continue;
            if (region.Size > 512L * 1024 * 1024) continue;

            byte[] buffer;
            try { buffer = _mem.Read(region.Base, (int)region.Size); }
            catch { continue; }

            int step = opt.Aligned ? size : 1;
            string mod = ModuleAt(moduleRanges, region.Base);

            for (int i = 0; i + size <= buffer.Length; i += step)
            {
                if (!MatchesAt(buffer, i, needle)) continue;
                hits.Add(new ScanHit(region.Base + i, region.Base, region.Protect, mod));
                if (hits.Count >= opt.MaxResults) return Results = hits;
            }
        }

        return Results = hits;
    }

    /// <summary>再扫：保留当前仍等于 expected 的地址。</summary>
    public List<ScanHit> NextScanExact(object expected, NumType type)
    {
        byte[] needle = ValueCodec.Encode(expected, type);
        int size = needle.Length;
        var kept = new List<ScanHit>();

        foreach (var hit in Results)
        {
            byte[] buf = new byte[size];
            if (!_mem.TryRead(hit.Address, buf, out _)) continue;
            if (buf.AsSpan().SequenceEqual(needle)) kept.Add(hit);
        }
        return Results = kept;
    }

    // 首扫时的原始字节快照，按地址索引。用于"变化/未变化"过滤。
    private readonly Dictionary<IntPtr, byte[]> _baseline = new();

    /// <summary>从存档恢复候选列表（跨命令流转用）。</summary>
    public void SetHits(List<ScanHit> hits) => Results = hits;

    /// <summary>恢复基线快照（跨命令流转用）。</summary>
    public void RestoreBaseline(Dictionary<IntPtr, byte[]> baseline)
    {
        _baseline.Clear();
        if (baseline == null) return;
        foreach (var kv in baseline) _baseline[kv.Key] = kv.Value;
    }

    /// <summary>导出当前基线，供持久化。</summary>
    public Dictionary<IntPtr, byte[]> ExportBaseline() => new(_baseline);

    public void CaptureBaseline(NumType type)
    {
        int size = ValueCodec.SizeOf(type);
        _baseline.Clear();
        foreach (var hit in Results)
        {
            byte[] buf = new byte[size];
            if (_mem.TryRead(hit.Address, buf, out _)) _baseline[hit.Address] = buf;
        }
    }

    /// <summary>再扫：保留数值相对基线发生过变化的地址（对应"变了"过滤）。</summary>
    public List<ScanHit> NextScanChanged(NumType type)
    {
        int size = ValueCodec.SizeOf(type);
        var kept = new List<ScanHit>();
        foreach (var hit in Results)
        {
            if (!_baseline.TryGetValue(hit.Address, out byte[] before)) continue;
            byte[] buf = new byte[size];
            if (!_mem.TryRead(hit.Address, buf, out _)) continue;
            if (!before.AsSpan().SequenceEqual(buf)) kept.Add(hit);
        }
        return Results = kept;
    }

    /// <summary>再扫：保留数值相对基线未变化的地址（对应"没变"过滤，用来排除干扰项）。</summary>
    public List<ScanHit> NextScanUnchanged(NumType type)
    {
        int size = ValueCodec.SizeOf(type);
        var kept = new List<ScanHit>();
        foreach (var hit in Results)
        {
            if (!_baseline.TryGetValue(hit.Address, out byte[] before)) continue;
            byte[] buf = new byte[size];
            if (!_mem.TryRead(hit.Address, buf, out _)) continue;
            if (before.AsSpan().SequenceEqual(buf)) kept.Add(hit);
        }
        return Results = kept;
    }

    /// <summary>再扫：只保留数值落在 [min,max] 区间内的地址（用于"未知初始值"场景）。</summary>
    public List<ScanHit> NextScanRange(double min, double max, NumType type)
    {
        int size = ValueCodec.SizeOf(type);
        var kept = new List<ScanHit>();
        foreach (var hit in Results)
        {
            byte[] buf = new byte[size];
            if (!_mem.TryRead(hit.Address, buf, out _)) continue;
            double v = ValueCodec.ToDouble(ValueCodec.Decode(buf, type));
            if (!double.IsNaN(v) && v >= min && v <= max) kept.Add(hit);
        }
        return Results = kept;
    }

    /// <summary>
    /// 校验一批地址当前是否仍等于期望值。写入前的最后一道闸。
    /// 任一条不符即返回 false，调用方必须放弃本次写入。
    /// </summary>
    public bool VerifyBeforeWrite(IReadOnlyList<(IntPtr Addr, byte[] Expected)> checks, out string error)
    {
        foreach (var (addr, expected) in checks)
        {
            byte[] buf = new byte[expected.Length];
            if (!_mem.TryRead(addr, buf, out _))
            {
                error = $"0x{addr.ToInt64():X} 已不可读（进程可能已退出或内存被释放）。";
                return false;
            }
            if (!buf.AsSpan().SequenceEqual(expected))
            {
                error = $"0x{addr.ToInt64():X} 前值不符：期望 {Hex(expected)}，实际 {Hex(buf)}。";
                return false;
            }
        }
        error = null;
        return true;
    }

    private static string Hex(byte[] b) => string.Join(' ', b.Select(x => x.ToString("X2")));

    public List<object> ReadAll(NumType type)
    {
        int size = ValueCodec.SizeOf(type);
        var list = new List<object>(Results.Count);
        foreach (var hit in Results)
        {
            byte[] buf = new byte[size];
            if (_mem.TryRead(hit.Address, buf, out _)) list.Add(ValueCodec.Decode(buf, type));
            else list.Add(null);
        }
        return list;
    }

    private static bool MatchesAt(byte[] hay, int off, byte[] needle)
        => hay.AsSpan(off, needle.Length).SequenceEqual(needle);

    private List<(IntPtr Base, long End, string Name)> ModuleRanges()
    {
        var list = new List<(IntPtr, long, string)>();
        foreach (System.Diagnostics.ProcessModule m in _mem.Process.Modules)
        {
            try
            {
                long b = m.BaseAddress.ToInt64();
                list.Add((m.BaseAddress, b + m.ModuleMemorySize, m.ModuleName));
            }
            catch { }
        }
        return list;
    }

    private static string ModuleAt(List<(IntPtr Base, long End, string Name)> ranges, IntPtr addr)
    {
        long a = addr.ToInt64();
        foreach (var r in ranges)
            if (a >= r.Base.ToInt64() && a < r.End) return r.Name;
        return "(private)";
    }
}
