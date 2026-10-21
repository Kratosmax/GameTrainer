using GameTrainer.Core;
using GameTrainer.Engine;
using GameTrainer.Memory;
using GameTrainer.Profiles;

namespace GameTrainer.Adapter;

/// <summary>
/// 原生进程探测：没有脚本层时，怎么找到数值在哪。
///
/// 这是 skill 第零节「问题 3」中「无语义层」那条路的工具化。
/// 与 CDP 探测的根本差别：拿不到变量名，只能靠**值扫描**缩小范围，
/// 再靠**内存上下文**生成可复用的 AOB 签名。
///
/// 流程（每一步都需要使用者配合改游戏里的值）：
///   1. 首扫：按当前已知值找候选
///   2. 再扫：让值变化后过滤
///   3. 收敛后，从候选地址周围提取上下文 → 生成 AOB 候选
///   4. 验证签名唯一性
///
/// 重要限制：AOB 里不能包含会被写入改掉的字节，
/// 否则写一次后签名失效。生成签名时必须把目标字段本身排除在外。
/// </summary>
internal static class NativeProbe
{
    public sealed class Candidate
    {
        public IntPtr Address { get; init; }
        public NumType Type { get; init; }
        public object Value { get; init; }
        /// <summary>周围字节（已排除目标字段本身）。</summary>
        public byte[] Context { get; init; }
        /// <summary>目标字段相对上下文起点的偏移。</summary>
        public int OffsetFromContext { get; init; }
        /// <summary>自动生成的 AOB 签名。</summary>
        public string Aob { get; init; }
        /// <summary>签名是否在当前内存中唯一。</summary>
        public bool IsUnique { get; init; }
    }

    /// <summary>
    /// 候选持久化路径。
    /// 值扫描天然需要多轮（改值 → 再扫），中间结果必须落盘，否则每轮都要从头扫。
    /// </summary>
    public static string CandidateFile(string processName)
        => Path.Combine(AppContext.BaseDirectory, "temp", $"native-probe-{processName}.json");

    /// <summary>保存候选，供下一轮过滤。</summary>
    public static void SaveCandidates(string processName, List<ScanHit> hits, NumType type)
    {
        string path = CandidateFile(processName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var doc = new
        {
            process = processName,
            type = type.ToString(),
            at = DateTime.Now,
            count = hits.Count,
            addresses = hits.Select(h => h.Address.ToInt64()).ToList()
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(doc));
    }

    /// <summary>读取上一轮候选。没有则返回空。</summary>
    public static List<ScanHit> LoadCandidates(string processName, out NumType type, out string at)
    {
        type = NumType.Double;
        at = null;
        string path = CandidateFile(processName);
        if (!File.Exists(path)) return new List<ScanHit>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && Enum.TryParse(t.GetString(), out NumType nt))
                type = nt;
            if (root.TryGetProperty("at", out var a)) at = a.GetString();

            var list = new List<ScanHit>();
            if (root.TryGetProperty("addresses", out var arr))
                foreach (var e in arr.EnumerateArray())
                    list.Add(new ScanHit(new IntPtr(e.GetInt64()), IntPtr.Zero, 0, null));
            return list;
        }
        catch { return new List<ScanHit>(); }
    }

    /// <summary>
    /// 按已知值首扫。
    /// </summary>
    public static List<ScanHit> FirstScan(string processName, object value, NumType type, out string error)
    {
        error = null;
        ProcessMemory mem;
        try { mem = ProcessMemory.AttachByName(processName); }
        catch (Exception ex)
        {
            error = $"无法附加到进程 {processName}：{ex.Message}";
            return new List<ScanHit>();
        }

        using (mem)
        {
            var guard = AntiCheatGuard.Check(mem);
            if (guard.Blocked)
            {
                error = "检测到反作弊，拒绝探测（不做绕过）";
                return new List<ScanHit>();
            }

            var scanner = new Scanner(mem);
            var opt = new Scanner.ScanOptions { Type = type, Aligned = true, MaxResults = 200000 };
            var hits = scanner.FirstScan(value, opt);
            if (hits.Count == 0) error = $"未找到值为 {value} 的 {type} 候选";
            return hits;
        }
    }

    /// <summary>
    /// 在已有候选基础上，按新值过滤。
    /// </summary>
    public static List<ScanHit> FilterByValue(string processName, List<ScanHit> previous,
                                              object expected, NumType type, out string error)
    {
        error = null;
        if (previous == null || previous.Count == 0) { error = "没有可过滤的候选"; return new List<ScanHit>(); }

        ProcessMemory mem;
        try { mem = ProcessMemory.AttachByName(processName); }
        catch (Exception ex) { error = $"无法附加：{ex.Message}"; return new List<ScanHit>(); }

        using (mem)
        {
            var scanner = new Scanner(mem);
            scanner.SetHits(previous);
            byte[] want = ValueCodec.Encode(expected, type);
            var kept = new List<ScanHit>();
            foreach (var h in previous)
            {
                try
                {
                    byte[] buf = mem.Read(h.Address, want.Length);
                    if (buf != null && buf.AsSpan().SequenceEqual(want)) kept.Add(h);
                }
                catch { }
            }
            if (kept.Count == 0) error = $"按 {expected} 过滤后没有剩余候选";
            return kept;
        }
    }

    /// <summary>
    /// 从候选地址生成 AOB 签名。
    ///
    /// 关键约束：签名区间必须**跳过目标字段本身**，否则写一次就失效。
    /// 做法是从目标字段往前取 contextBytes 字节，签名 = 前文 + 通配（占位目标字段）+ 后文。
    /// </summary>
    public static Candidate BuildSignature(string processName, IntPtr address, NumType type,
                                           out string error,
                                           int contextBefore = 16, int contextAfter = 16)
    {
        error = null;
        ProcessMemory mem;
        try { mem = ProcessMemory.AttachByName(processName); }
        catch (Exception ex) { error = $"无法附加：{ex.Message}"; return null; }

        using (mem)
        {
            int fieldSize = ValueCodec.SizeOf(type);
            IntPtr start = new IntPtr(address.ToInt64() - contextBefore);
            int total = contextBefore + fieldSize + contextAfter;

            byte[] buf;
            try { buf = mem.Read(start, total); }
            catch (Exception ex) { error = $"读取上下文失败：{ex.Message}"; return null; }

            // 目标字段本身改成通配，避免自失效
            var parts = new List<string>();
            for (int i = 0; i < buf.Length; i++)
            {
                if (i >= contextBefore && i < contextBefore + fieldSize) parts.Add("??");
                else parts.Add(buf[i].ToString("X2"));
            }
            string aob = string.Join(' ', parts);

            // 验证唯一性
            bool unique = false;
            try
            {
                var pat = new AobPattern(aob);
                int n = 0;
                foreach (var r in mem.Regions)
                {
                    if (!Native.NativeMethods.IsReadable(r.Protect)) continue;
                    if (r.Size > 256L * 1024 * 1024) continue;
                    byte[] region;
                    try { region = mem.Read(r.Base, (int)r.Size); } catch { continue; }
                    n += pat.CountMatches(region, maxCount: 2);
                    if (n > 1) break;
                }
                unique = n == 1;
            }
            catch { }

            object val = null;
            try
            {
                byte[] vb = mem.Read(address, fieldSize);
                if (vb != null) val = ValueCodec.Decode(vb, type);
            }
            catch { }

            return new Candidate
            {
                Address = address,
                Type = type,
                Value = val,
                Context = buf,
                OffsetFromContext = contextBefore,
                Aob = aob,
                IsUnique = unique
            };
        }
    }

    /// <summary>把候选转成适配包绑定。</summary>
    public static ValueBinding ToBinding(Candidate c, string semantic)
    {
        return new ValueBinding
        {
            Semantic = semantic,
            Kind = "memory_aob",
            Aob = c.Aob,
            // 签名起点 = 前文开头，目标字段在起点**之后** OffsetFromContext 字节处，
            // 所以偏移必须是正数。写成负数会落到前文里，读出垃圾值（实测踩过）。
            Offsets = new List<string> { $"+0x{c.OffsetFromContext:X}" },
            Type = TypeName(c.Type),
            Range = null,
            Realtime = true,
            Style = "number"
        };
    }

    private static string TypeName(NumType t) => t switch
    {
        NumType.Byte => "byte",
        NumType.Int16 => "int16",
        NumType.Int32 => "int32",
        NumType.Int64 => "int64",
        NumType.Float => "float",
        NumType.Double => "double",
        _ => "double"
    };
}
