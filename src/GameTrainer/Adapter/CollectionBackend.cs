using System.Runtime.InteropServices;

namespace GameTrainer.Adapter;

/// <summary>
/// 集合型内存后端：一次绑定覆盖「一个字典里的全部条目」。
///
/// 存在理由：Hospital Rhapsody 的员工数据是 Dictionary&lt;int, Useremployee&gt;，
/// 员工会随进度增加、被开除的会消失。用传统「一条绑定一个固定地址」的写法，
/// 每招一个员工就要改一次适配包，不可维护。
///
/// 而且 Unity IL2CPP 的托管对象地址每次启动都变，AOB 签名也无法固定
/// （已实测：Global 单例的静态字段引用全部落在托管堆，不在 .data 段）。
/// 所以必须「每次操作时现场扫描定位」，不缓存任何地址。
///
/// 工作流程：
///   1. 扫描内存找主体对象（等级范围 + 资产上限 + 锚点条目 ID 三重过滤）
///   2. 顺着 dictOffset 找到字典
///   3. 遍历 entries，用「指针自证」过滤出真实条目
///   4. 按 fieldOffset 读/写目标字段
/// </summary>
internal static class CollectionBackend
{
    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern IntPtr OpenProcessNative(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    private static extern bool ReadProcessMemoryNative(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
    private static extern bool WriteProcessMemoryNative(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    private static extern bool CloseHandleNative(IntPtr h);

    [DllImport("kernel32.dll", EntryPoint = "VirtualQueryEx", SetLastError = true)]
    private static extern IntPtr VirtualQueryExNative(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);

    /// <summary>x64 布局 48 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;

    /// <summary>
    /// 必须要有这一位。漏了它 OpenProcess 会成功、但 VirtualQueryEx 恒返回 0
    /// 且 GetLastError 也是 0，极难排查。
    /// </summary>
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private const uint MEM_COMMIT = 0x1000;

    private static bool IsReadable(uint protect)
        => protect == 0x04 || protect == 0x08 || protect == 0x40 || protect == 0x80;

    private static bool LooksLikePointer(long v) => v > 0x100000000 && v < 0x400000000000;

    // ---------------- 会话 ----------------

    /// <summary>一次操作的内存会话。用完必须 Dispose。</summary>
    internal sealed class Session : IDisposable
    {
        public IntPtr Handle { get; init; }
        public int Pid { get; init; }
        public string Error { get; init; }

        public bool Ok => Handle != IntPtr.Zero && Error == null;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) CloseHandleNative(Handle);
        }
    }

    public static Session Attach(int pid, bool needWrite)
    {
        uint access = PROCESS_VM_READ | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION;
        if (needWrite) access |= PROCESS_VM_WRITE;

        IntPtr h = OpenProcessNative(access, false, pid);
        if (h == IntPtr.Zero)
            return new Session
            {
                Handle = IntPtr.Zero,
                Pid = pid,
                Error = $"OpenProcess 失败（PID {pid}，错误 {Marshal.GetLastWin32Error()}）。"
                      + "目标可能已退出，或需要与目标相同的权限级别。"
            };

        return new Session { Handle = h, Pid = pid };
    }

    // ---------------- 基础读写 ----------------

    private static byte[] Read(IntPtr h, long addr, int len)
    {
        var buf = new byte[len];
        return ReadProcessMemoryNative(h, (IntPtr)addr, buf, (IntPtr)len, out IntPtr got) ? buf : null;
    }

    /// <summary>供 SaveLocator 复用的读取原语。</summary>
    public static bool ReadMemory(IntPtr h, long addr, byte[] buf, int len)
        => ReadProcessMemoryNative(h, (IntPtr)addr, buf, (IntPtr)len, out _);

    private static int I32(byte[] b, int off) => BitConverter.ToInt32(b, off);
    private static long I64(byte[] b, int off) => BitConverter.ToInt64(b, off);

    // ---------------- 定位参数 ----------------

    /// <summary>一条集合绑定的定位参数。</summary>
    internal sealed class Locator
    {
        /// <summary>主体对象里字典字段的偏移（如 0x88）。</summary>
        public long DictOffset { get; init; }

        /// <summary>主体对象里等级字段偏移（如 0x2C）。-1 表示不校验。</summary>
        public long LevelOffset { get; init; } = -1;

        public int MinLevel { get; init; } = 1;
        public int MaxLevel { get; init; } = 99;

        /// <summary>主体对象里资产字段偏移（如 0x40）。-1 表示不校验。</summary>
        public long GoldOffset { get; init; } = -1;

        public long MaxGold { get; init; } = 100_000_000;

        /// <summary>
        /// 锚点条目 ID：真实存档必然包含这些 ID（主角团不会被开除）。
        /// 这是区分「正在玩的存档」与内存里其它副本的唯一可靠判据——
        /// 实测同一时刻内存里有 244 个结构相似的候选。
        /// </summary>
        public List<int> AnchorIds { get; init; } = new();

        /// <summary>字典 entry 里 value 指针的偏移。</summary>
        public long EntryValueOffset { get; init; } = 0x10;

        /// <summary>字典 count 字段偏移。</summary>
        public long DictCountOffset { get; init; } = 0x20;

        /// <summary>字典 entries 指针偏移。</summary>
        public long DictEntriesOffset { get; init; } = 0x18;

        /// <summary>entry 步长（实测 0x18）。</summary>
        public long EntryStride { get; init; } = 0x18;

        /// <summary>条目 ID 字段偏移。</summary>
        public long EntryIdOffset { get; init; } = 0x10;

        /// <summary>条目 ID 的合理范围——用来排除内存垃圾数据。</summary>
        public int MinEntryId { get; init; } = 1000;
        public int MaxEntryId { get; init; } = 9999;

        /// <summary>二级查找参数（可选）。配了就把字段读写转到静态表上。</summary>
        public StaticLookup Static { get; init; }

        /// <summary>名字表参数（可选）。配了就把条目 ID 显示成人名。</summary>
        public NameTable Names { get; init; }
    }

    /// <summary>一个集合条目：ID + 对象地址。</summary>
    internal readonly record struct Entry(int Id, long Address);

    /// <summary>
    /// 校验一个 int 是否是合理的时间戳：0（未设置）或 2020-2033 年之间的 Unix 秒。
    /// 内存垃圾数据经常落在这个范围之外（负数、超大值、随机位模式）。
    /// </summary>
    private static bool IsValidEpoch(int v)
        => v == 0 || (v >= 1577836800 && v <= 2000000000);

    /// <summary>
    /// 给候存档打分为的是在多个结构合格的对象里选出「游戏真正在用的那个」。
    ///
    /// 不能用条目数排序：垃圾对象也可能挂着一大堆条目
    /// （实测错误候选有 47 个条目，正确存档只有 10 个员工）。
    /// 改用「字典指针完整度 + 时间戳已设置 + 资金量级」这几个与存档真实度相关的信号。
    /// </summary>
    private static int ScoreCandidate(byte[] buf, int i, List<Entry> entries, Locator loc)
    {
        int score = 0;

        // 字典指针完整度（0..17），越接近 Userdata 的真实布局越可信
        for (int off = 0x60; off <= 0xB8; off += 8)
            if (LooksLikePointer(I64(buf, i + off))) score += 10;

        // 时间戳已设置说明这份存档被保存过
        if (I32(buf, i + 0x44) != 0) score += 50;
        if (I32(buf, i + 0x50) != 0) score += 50;

        // 资金量级：经营游戏正常在万元以上
        if (loc.GoldOffset >= 0)
        {
            int gold = I32(buf, i + (int)loc.GoldOffset);
            if (gold >= 10000) score += 30;
            if (gold >= 100000) score += 20;
        }

        // 条目数只作微弱加权，避免"垃圾条目多"反超
        score += Math.Min(entries.Count, 30);

        return score;
    }

    /// <summary>
    /// 二级查找：条目的某个字段不在条目对象上，而在「按 ID 关联的静态表」里。
    ///
    /// 典型场景：Hospital Rhapsody 的医术（bmedical）在 Dictemployee +0x30，
    /// 而 Dictemployee 只能通过 employeeid 反查（内存里没有从条目指向它的指针）。
    ///
    /// 做法：扫描内存找满足「+IdOffset == 条目ID」且「+FieldOffset 值合理」的对象。
    /// 这比维护一张地址表可靠——静态表条目同样是托管对象，地址每次启动都变。
    /// </summary>
    internal sealed class StaticLookup
    {
        /// <summary>静态表里 ID 字段的偏移（Dictemployee +0x10）。</summary>
        public long IdOffset { get; init; } = 0x10;

        /// <summary>静态表里目标字段的偏移（bmedical = +0x30）。</summary>
        public long FieldOffset { get; init; }

        /// <summary>目标字段的合理范围，用于排除误命中。</summary>
        public int MinValue { get; init; } = 0;
        public int MaxValue { get; init; } = 9999;

        /// <summary>静态表里名字索引字段的偏移（ename = +0x18）。0 表示没有。</summary>
        public long NameIndexOffset { get; init; }

        /// <summary>静态表里年龄字段的偏移（age = +0x58）。0 表示不需要校验。</summary>
        public long AgeOffset { get; init; }

        /// <summary>
        /// 静态表里类型字段的偏移（Dictemployee +0x14 = employeetype）。
        /// 这是区分「真实员工定义」与「内存里其它含相同 ID 的对象」的关键：
        /// 实测不加这一项会命中别的对象，导致名字和医术全错（1001 显示成别人的名字）。
        /// </summary>
        public long TypeOffset { get; init; }

        /// <summary>类型值的合理范围。</summary>
        public int MinType { get; init; } = 1;
        public int MaxType { get; init; } = 20;
    }

    /// <summary>
    /// 名字表：把名字索引（ename）映射到字符串。
    ///
    /// 结构是 Dictionary&lt;int,string&gt;，entries 地址是实测固定值。
    /// 这个地址在游戏更新后会失效——失效时回退显示 #ID，不影响读写。
    /// </summary>
    internal sealed class NameTable
    {
        public long EntriesAddress { get; init; }
        public long EntryStride { get; init; } = 0x18;
        public long KeyOffset { get; init; } = 0x08;
        public long ValueOffset { get; init; } = 0x10;
        public int ScanCount { get; init; } = 500;
    }

    /// <summary>定位结果：主体对象地址 + 全部条目。</summary>
    internal sealed class Located
    {
        public long OwnerAddress { get; init; }
        public List<Entry> Entries { get; init; } = new();
    }

    // ---------------- 定位 ----------------

    /// <summary>
    /// 定位主体对象并读出全部条目。返回 null 表示找不到（附原因）。
    /// </summary>
    public static Located Locate(IntPtr h, Locator loc, out string error)
    {
        error = null;
        var candidates = new List<(long Addr, List<Entry> Entries, int Score)>();

        int need = (int)Math.Max(loc.DictOffset + 8, 0x130);

        ScanReadable(h, (baseA, buf, n) =>
        {
            if (need > n) return;

            for (int i = 0; i + need <= n; i += 8)
            {
                if (!LooksLikePointer(I64(buf, i))) continue;

                if (loc.LevelOffset >= 0)
                {
                    int level = I32(buf, i + (int)loc.LevelOffset);
                    if (level < loc.MinLevel || level > loc.MaxLevel) continue;
                }

                if (loc.GoldOffset >= 0)
                {
                    int gold = I32(buf, i + (int)loc.GoldOffset);
                    if (gold < 0 || gold > loc.MaxGold) continue;
                }

                // 存档身份校验：starttime(+0x44) / savetime(+0x50) 是 Unix 时间戳
                // 或 0（尚未存档）。负数和随机值是内存垃圾。
                //
                // 不加这一条会命中大量结构相似的垃圾对象：
                // 实测曾出现同一份存档读出 10 / 47 个条目、资金在 51~103 万间跳变，
                // 原因是"条目数越多分越高"把垃圾候选排到了正确候选前面。
                if (!IsValidEpoch(I32(buf, i + 0x44))) continue;
                if (!IsValidEpoch(I32(buf, i + 0x50))) continue;

                // Userdata 有一整排字典指针（+0x60..+0xB8），真实存档这些几乎都有效。
                // 只检查目标字典会把大量垃圾放进来。
                int validDicts = 0;
                for (int off = 0x60; off <= 0xB8; off += 8)
                    if (LooksLikePointer(I64(buf, i + off))) validDicts++;
                if (validDicts < 8) continue;

                long dict = I64(buf, i + (int)loc.DictOffset);
                if (!LooksLikePointer(dict)) continue;

                var entries = ReadDictionary(h, dict, loc);
                if (entries.Count == 0) continue;

                // 锚点：必须包含全部指定 ID
                bool allAnchors = true;
                foreach (int anchor in loc.AnchorIds)
                {
                    if (!entries.Exists(e => e.Id == anchor)) { allAnchors = false; break; }
                }
                if (!allAnchors) continue;

                // 评分：优先"字典结构完整 + 锚点齐"的候选，
                // 不再用条目数排序——条目多不代表是正确存档（垃圾对象也可能很多）。
                candidates.Add((baseA + i, entries, ScoreCandidate(buf, i, entries, loc)));
            }
        });

        if (candidates.Count == 0)
        {
            error = "未找到目标存档对象。" +
                    "可能原因：游戏尚未读取存档、进程刚重启，或锚点员工 ID 与当前存档不符。";
            return null;
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return new Located { OwnerAddress = candidates[0].Addr, Entries = candidates[0].Entries };
    }

    /// <summary>
    /// 已知主体地址时，只重读它的条目表，不再全量扫描。
    ///
    /// 定位缓存命中后走这条路：一次操作省掉 2 秒扫描。
    /// 注意仍要重新读字典——员工会增减，缓存的条目列表会过期。
    /// </summary>
    public static Located Reentries(IntPtr h, long ownerAddress, Locator loc, out string error)
    {
        error = null;

        int need = (int)loc.DictOffset + 8;
        if (need < 0x10) need = 0x10;
        byte[] head = Read(h, ownerAddress, need);
        if (head == null)
        {
            error = "读取主体对象失败（地址可能已失效，读档后需要重新定位）";
            return null;
        }

        long klass = I64(head, 0);
        if (!LooksLikePointer(klass))
        {
            error = "主体对象已失效（读档或退出游戏后地址会变，请点「刷新数值」）";
            return null;
        }

        long dict = I64(head, (int)loc.DictOffset);
        if (!LooksLikePointer(dict))
        {
            error = "主体对象的字典指针无效";
            return null;
        }

        var entries = ReadDictionary(h, dict, loc);
        return new Located { OwnerAddress = ownerAddress, Entries = entries };
    }

    /// <summary>
    /// 遍历 Dictionary。
    ///
    /// 不用 hashCode 判断槽位有效性——实测字典有空洞与扩容残留，
    /// 按 hashCode==-1 过滤会漏掉真实条目。改为「指针自证」：
    /// 读到的对象必须有有效 klass、且 ID 在合理范围内。
    /// </summary>
    private static List<Entry> ReadDictionary(IntPtr h, long dictAddr, Locator loc)
    {
        var result = new List<Entry>();

        int headerLen = (int)Math.Max(loc.DictCountOffset, loc.DictEntriesOffset) + 8;
        byte[] d = Read(h, dictAddr, headerLen);
        if (d == null) return result;

        long entries = I64(d, (int)loc.DictEntriesOffset);
        int count = I32(d, (int)loc.DictCountOffset);
        if (!LooksLikePointer(entries) || count <= 0 || count > 100000) return result;

        int span = (int)Math.Min(count * (loc.EntryStride + 8) + 0x100, 0x40000);
        if (span < 0x100) span = 0x100;
        byte[] area = Read(h, entries, span);
        if (area == null) return result;

        var seen = new HashSet<long>();
        for (int off = 0; off + loc.EntryStride <= area.Length; off += 4)
        {
            long val = I64(area, off + (int)loc.EntryValueOffset);
            if (!LooksLikePointer(val) || !seen.Add(val)) continue;

            int selfLen = (int)Math.Max(loc.EntryIdOffset + 4, 0x10);
            byte[] e = Read(h, val, selfLen);
            if (e == null || !LooksLikePointer(I64(e, 0))) continue;

            int id = I32(e, (int)loc.EntryIdOffset);
            if (id < loc.MinEntryId || id > loc.MaxEntryId) continue;

            result.Add(new Entry(id, val));
        }
        return result;
    }

    // ---------------- 二级查找（静态表） ----------------

    /// <summary>
    /// 按条目 ID 在静态表里找对应对象。
    ///
    /// 必须现场扫描而不是维护地址表：静态表条目也是托管对象，地址每次启动都变。
    /// 判定条件用「ID 精确匹配 + 目标字段值在合理范围内 + 年龄/名字索引合理」，
    /// 单靠 ID 会命中大量内存垃圾。
    /// </summary>
    public static long FindStaticEntry(IntPtr h, int id, StaticLookup sl)
    {
        long hit = 0;
        int bestScore = -1;

        ScanReadable(h, (baseA, buf, n) =>
        {
            // 不能因为已有命中就提前退出：同一 ID 在内存里有多份结构相似的副本
            // （模板/缓存/垃圾数据），必须扫完再按 score 择优。
            // FieldOffset 可能是 -1（只查名字、数值不从静态表取），要排除在长度计算外。
            int need = (int)Math.Max(Math.Max(Math.Max(sl.FieldOffset, sl.NameIndexOffset), sl.AgeOffset), sl.IdOffset) + 8;
            if (need > n) return;

            for (int i = 0; i + need <= n; i += 8)
            {
                if (!LooksLikePointer(I64(buf, i))) continue;
                if (I32(buf, i + (int)sl.IdOffset) != id) continue;

                // FieldOffset <= 0 表示不从静态表取数值，跳过范围校验。
                int val = 0;
                if (sl.FieldOffset > 0)
                {
                    val = I32(buf, i + (int)sl.FieldOffset);
                    if (val < sl.MinValue || val > sl.MaxValue) continue;
                }

                // 年龄用来排除误命中：游戏里员工年龄在 10..100 之间。
                // 内存垃圾数据很难同时满足 ID + 字段范围 + 年龄范围。
                if (sl.AgeOffset > 0)
                {
                    int age = I32(buf, i + (int)sl.AgeOffset);
                    if (age < 10 || age > 100) continue;
                }

                // 类型校验：区分真实员工定义与其它含相同 ID 的对象。
                // 不加这一项会命中错误对象（实测 1001 会显示成别人的名字和医术）。
                if (sl.TypeOffset > 0)
                {
                    int etype = I32(buf, i + (int)sl.TypeOffset);
                    if (etype < sl.MinType || etype > sl.MaxType) { continue; }
                }

                // 收集所有通过校验的候选，而不是取第一个命中。
                // 实测同一 ID 在内存里有多份结构相似的副本（模板/缓存/垃圾），
                // 取第一个会命中 med=0 的空壳（1001 和 1208 就因此找不到值）。
                // 判据：优先 med > 0 的，同等情况下优先年龄更合理的。
                // 择优：med 有值的最像真实条目；再看年龄是否在常见范围（18-60）。
                int score = val > 0 ? 1000 : 0;
                if (sl.AgeOffset > 0)
                {
                    int a2 = I32(buf, i + (int)sl.AgeOffset);
                    if (a2 >= 18 && a2 <= 60) score += 100;
                }
                if (score > bestScore) { bestScore = score; hit = baseA + i; }
            }
        });

        return hit;
    }

    // ---------------- 名字表 ----------------

    private static Dictionary<int, string> _nameCache;

    /// <summary>
    /// 读名字索引对应的显示名。
    /// 名字表地址失效时返回 null，调用方回退显示 #ID。
    /// </summary>
    public static string ResolveName(IntPtr h, int nameIndex, NameTable nt)
    {
        if (nt == null || nt.EntriesAddress == 0) return null;

        if (_nameCache == null)
        {
            _nameCache = new Dictionary<int, string>();

            for (int i = 0; i < nt.ScanCount; i++)
            {
                byte[] e = Read(h, nt.EntriesAddress + i * nt.EntryStride, (int)nt.EntryStride);
                if (e == null) continue;

                int key = I32(e, (int)nt.KeyOffset);
                long val = I64(e, (int)nt.ValueOffset);
                if (!LooksLikePointer(val)) continue;

                byte[] s = Read(h, val, 0x40);
                if (s == null) continue;

                // IL2CPP String 对象：+0x10 是长度（字符数），+0x14 起是 UTF-16 数据
                int len = I32(s, 0x10);
                if (len < 1 || len > 16) continue;

                string text = System.Text.Encoding.Unicode.GetString(s, 0x14, len * 2);
                if (!string.IsNullOrWhiteSpace(text) && !_nameCache.ContainsKey(key))
                    _nameCache[key] = text;
            }
        }

        return _nameCache.TryGetValue(nameIndex, out var name) ? name : null;
    }

    /// <summary>读条目上某个偏移的 int32。</summary>
    public static bool TryReadEntry(IntPtr h, long entryAddr, long fieldOffset, out int value)
    {
        value = 0;
        byte[] b = Read(h, entryAddr + fieldOffset, 4);
        if (b == null) return false;
        value = BitConverter.ToInt32(b, 0);
        return true;
    }

    /// <summary>写条目上某个偏移的 int32，带前值与回读双重校验。</summary>
    public static bool TryWriteEntry(IntPtr h, long entryAddr, long fieldOffset,
                                     int expect, int value, out string error)
    {
        error = null;

        if (!TryReadEntry(h, entryAddr, fieldOffset, out int before))
        {
            error = "读取前值失败";
            return false;
        }
        if (before != expect)
        {
            error = $"前值校验失败：当前 {before}，期望 {expect}";
            return false;
        }

        if (!WriteProcessMemoryNative(h, (IntPtr)(entryAddr + fieldOffset),
                BitConverter.GetBytes(value), (IntPtr)4, out _))
        {
            error = $"写入失败（错误 {Marshal.GetLastWin32Error()}）";
            return false;
        }

        if (!TryReadEntry(h, entryAddr, fieldOffset, out int after) || after != value)
        {
            error = $"回读校验失败：期望 {value}，实际 {after}";
            return false;
        }
        return true;
    }

    // ---------------- 诊断 ----------------

    /// <summary>
    /// 句柄能否读取该进程内存。
    /// 不能死探固定地址——现代 Windows 上 0x10000 这类低地址通常未提交，
    /// 会误判为"读不到内存"。改为先找一个确定可读的区再去读。
    /// </summary>
    public static bool CanRead(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        long region = FirstReadableRegion(h);
        return region != 0 && Read(h, region, 4) != null;
    }

    /// <summary>返回第一个可读的已提交内存区基址；0 表示扫描没起来。</summary>
    public static long FirstReadableRegion(IntPtr h)
    {
        long addr = 0x10000;
        for (int i = 0; i < 64; i++)
        {
            if (VirtualQueryExNative(h, (IntPtr)addr, out MEMORY_BASIC_INFORMATION mbi,
                    (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                return 0;

            long size = (long)mbi.RegionSize;
            if (mbi.State == MEM_COMMIT && IsReadable(mbi.Protect) && size > 0)
                return (long)mbi.BaseAddress;

            addr = (long)mbi.BaseAddress + (size == 0 ? 0x1000 : size);
        }
        return 0;
    }

    // ---------------- 内存扫描 ----------------

    /// <summary>遍历全部可读的已提交内存区。</summary>
    private static void ScanReadable(IntPtr h, Action<long, byte[], int> onChunk)
    {
        const long start = 0x10000;
        const long end = 0x7FFFFFFFFFFF;
        byte[] buf = new byte[4 * 1024 * 1024];

        long addr = start;
        while (addr < end)
        {
            if (VirtualQueryExNative(h, (IntPtr)addr, out MEMORY_BASIC_INFORMATION mbi,
                    (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                break;

            long baseAddr = (long)mbi.BaseAddress;
            long size = (long)mbi.RegionSize;

            if (mbi.State == MEM_COMMIT && IsReadable(mbi.Protect) && size > 0)
            {
                long off = 0;
                while (off < size)
                {
                    int chunk = (int)Math.Min(buf.Length, size - off);
                    if (ReadProcessMemoryNative(h, (IntPtr)(baseAddr + off), buf, (IntPtr)chunk, out IntPtr read))
                        onChunk(baseAddr + off, buf, (int)read);
                    off += chunk;
                }
            }

            addr = baseAddr + (size == 0 ? 0x1000 : size);
        }
    }
}
