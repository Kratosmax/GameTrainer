using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GameTrainer.Adapter;

/// <summary>
/// 存档定位缓存：把「哪一份 Userdata 是当前存档」这件事的结果记住，
/// 避免每次读写都全量扫描，也避免每次扫描都重新猜。
///
/// 核心难点（实测得出）：内存里同时存在多份结构完全合法的 Userdata，
/// 靠「员工数/资金量级/含主角团」都无法可靠区分——
/// 曾出现 77 个候选全部通过结构校验的情况。
///
/// 解法：不做「选一个」的启发式，改为记录存档身份并用身份复核：
///   身份 = starttime（本局开局时间）+ rolename（角色名）
///
/// 为什么用这两个：
///   - starttime 在整局游戏内不变，**保存存档后也不变**（实测：
///     保存前 1792604246 → 保存后仍是 1792604246，而 savetime 变了）
///   - rolename 同理
///   所以「保存存档」不会导致缓存失效，只有「读档/开新档」才会。
///
/// 缓存失效时自动重新定位，不需要用户手动点刷新。
/// </summary>
internal static class SaveLocator
{
    /// <summary>一次定位的结果。</summary>
    internal sealed class Snapshot
    {
        /// <summary>Userdata 主体对象地址。</summary>
        public long OwnerAddress { get; init; }

        /// <summary>本局开局时间（Unix 秒），存档身份的一部分。</summary>
        public int StartTime { get; init; }

        /// <summary>角色名，存档身份的一部分。</summary>
        public string RoleName { get; init; } = "";

        public int Level { get; init; }
        public int Gold { get; init; }

        /// <summary>定位时看到的员工数，仅用于展示。</summary>
        public int EmployeeCount { get; init; }

        /// <summary>人类可读的身份描述，用于输出与核对。</summary>
        public string Identity =>
            $"开局时间 {DescribeEpoch(StartTime)} · 角色 {(string.IsNullOrEmpty(RoleName) ? "-" : RoleName)}";
    }

    private static Snapshot _cached;
    private static readonly object _lock = new();

    /// <summary>清空缓存（GUI 的「刷新数值」会调用）。</summary>
    public static void Invalidate()
    {
        lock (_lock) _cached = null;
    }

    /// <summary>
    /// 取当前存档定位结果。
    ///
    /// 有缓存且身份复核通过 → 直接用（微秒级，读 3 个字段）。
    /// 否则（首次 / 读档后）→ 全量扫描定位一次。
    /// </summary>
    public static Snapshot Resolve(IntPtr handle, CollectionBackend.Locator locator, out string error)
    {
        error = null;

        lock (_lock)
        {
            if (_cached != null && VerifyIdentity(handle, _cached))
                return _cached;

            // 缓存失效或首次：走完整定位
            var located = CollectionBackend.Locate(handle, locator, out error);
            if (located == null) { _cached = null; return null; }

            // 必须读到 0x48（starttime 在 +0x44，占 4 字节）。
            // 只读 0x30 会让下面三处 BitConverter 全部越界，
            // 表现是 Index was out of range（Parameter 'startIndex'），与定位无关。
            byte[] head = Read(handle, located.OwnerAddress, 0x48);
            if (head == null || head.Length < 0x48)
            {
                error = "定位成功但读取对象头失败";
                return null;
            }

            var snap = new Snapshot
            {
                OwnerAddress = located.OwnerAddress,
                StartTime = BitConverter.ToInt32(head, 0x44),
                RoleName = ReadString(handle, BitConverter.ToInt64(head, 0x20)) ?? "",
                Level = BitConverter.ToInt32(head, 0x2C),
                Gold = BitConverter.ToInt32(head, 0x40),
                EmployeeCount = located.Entries.Count
            };

            _cached = snap;
            return snap;
        }
    }

    /// <summary>
    /// 复核缓存是否仍然指向同一份存档。
    /// 只读 starttime + rolename 指针，开销可忽略。
    /// </summary>
    private static bool VerifyIdentity(IntPtr handle, Snapshot snap)
    {
        byte[] head = Read(handle, snap.OwnerAddress, 0x48);
        if (head == null) return false;

        // 对象仍是有效托管对象
        long klass = BitConverter.ToInt64(head, 0);
        if (klass < 0x100000000 || klass > 0x400000000000) return false;

        int startTime = BitConverter.ToInt32(head, 0x44);
        if (startTime != snap.StartTime) return false;

        string name = ReadString(handle, BitConverter.ToInt64(head, 0x20)) ?? "";
        return string.Equals(name, snap.RoleName, StringComparison.Ordinal);
    }

    private static byte[] Read(IntPtr h, long addr, int len)
    {
        var b = new byte[len];
        return CollectionBackend.ReadMemory(h, addr, b, len) ? b : null;
    }

    /// <summary>读 IL2CPP String 对象：+0x10 长度，+0x14 UTF-16 数据。</summary>
    private static string ReadString(IntPtr h, long objAddr)
    {
        if (objAddr < 0x100000000 || objAddr > 0x400000000000) return null;

        byte[] head = Read(h, objAddr, 0x20);
        if (head == null || head.Length < 0x20) return null;

        int len = BitConverter.ToInt32(head, 0x10);
        if (len < 1 || len > 64) return null;

        byte[] data = Read(h, objAddr + 0x14, len * 2);
        return data == null ? null : Encoding.Unicode.GetString(data);
    }

    /// <summary>把 Unix 秒转成可读时间，0 表示未设置。</summary>
    private static string DescribeEpoch(int seconds)
    {
        if (seconds <= 0) return "未设置";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch { return seconds.ToString(); }
    }
}
