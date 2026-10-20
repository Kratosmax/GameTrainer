using System.Runtime.InteropServices;
using System.Text;

namespace GameTrainer.Probe;

/// <summary>
/// 通用内存读取/转储工具。用于分析任意进程的内存结构，不针对特定游戏。
///
/// 用法：
///   GtProbe dump  &lt;pid&gt; &lt;addr&gt; [bytes]     转储十六进制 + ASCII
///   GtProbe qword &lt;pid&gt; &lt;addr&gt;             读 8 字节并按多种类型解释
///   GtProbe find  &lt;pid&gt; &lt;hexBytes&gt;          在全进程搜索字节序列
///   GtProbe regions &lt;pid&gt;                   列出所有可读内存区域
/// </summary>
internal static class Program
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

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

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length < 2) { Usage(); return 2; }

        string cmd = args[0].ToLowerInvariant();
        if (!int.TryParse(args[1], out int pid)) { Console.Error.WriteLine("PID 无效"); return 2; }

        IntPtr h = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
        {
            Console.Error.WriteLine($"OpenProcess 失败 (Win32 {Marshal.GetLastWin32Error()})");
            return 1;
        }

        try
        {
            return cmd switch
            {
                "dump" => CmdDump(h, args),
                "qword" => CmdQword(h, args),
                "find" => CmdFind(h, args),
                "regions" => CmdRegions(h),
                "write" => CmdWrite(h, args),
                "writedouble" => CmdWriteDouble(h, args),
                "snap" => CmdSnap(h, args),
                "diff" => CmdDiff(h, args),
                "findchange" => CmdFindChange(h, args),
                _ => Unknown()
            };
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// 对可读内存做全量快照存盘。
    /// 用于差分分析：不猜类型、不猜结构，直接对比"值变化前后哪些字节变了"。
    /// 快照格式：每个区域 = 8 字节地址 + 4 字节长度 + 原始字节。
    /// </summary>
    private static int CmdSnap(IntPtr h, string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("用法: snap <pid> <outFile>"); return 2; }
        string path = a[2];
        long total = 0, regions = 0;

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var bw = new BinaryWriter(fs))
        {
            long addr = 0;
            while (true)
            {
                if (VirtualQueryEx(h, new IntPtr(addr), out MEMORY_BASIC_INFORMATION mbi,
                        (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero) break;

                long size = mbi.RegionSize.ToInt64();
                bool readable = mbi.State == MEM_COMMIT && (mbi.Protect & 0x100) == 0 &&
                                (mbi.Protect & 0xFF) is 0x02 or 0x04 or 0x20 or 0x40 or 0x08 or 0x80;

                if (readable && size > 0 && size <= 256L * 1024 * 1024)
                {
                    byte[] buf = new byte[size];
                    if (ReadProcessMemory(h, mbi.BaseAddress, buf, (IntPtr)size, out IntPtr read))
                    {
                        int n = (int)read;
                        bw.Write(mbi.BaseAddress.ToInt64());
                        bw.Write(n);
                        bw.Write(buf, 0, n);
                        total += n;
                        regions++;
                    }
                }
                long next = mbi.BaseAddress.ToInt64() + size;
                if (next <= addr) break;
                addr = next;
            }
        }
        Console.WriteLine($"快照完成：{regions} 个区域，{total / 1024 / 1024} MB → {path}");
        return 0;
    }

    /// <summary>
    /// 差分两个快照，列出变化的区域。
    /// 只报告"由某个 double 变成另一个 double"的地址，过滤掉无关噪声。
    /// </summary>
    private static int CmdDiff(IntPtr h, string[] a)
    {
        // diff <pid> <snapA> <snapB>
        if (a.Length < 4) { Console.Error.WriteLine("用法: diff <pid> <snapA> <snapB>"); return 2; }
        var regionsA = LoadSnap(a[2]);
        var regionsB = LoadSnap(a[3]);

        // 按基址配对
        var dictB = regionsB.ToDictionary(r => r.Base, r => r.Data);
        int changed = 0;

        foreach (var ra in regionsA)
        {
            if (!dictB.TryGetValue(ra.Base, out byte[] db)) continue;
            int n = Math.Min(ra.Data.Length, db.Length);

            int i = 0;
            while (i + 8 <= n)
            {
                if (ra.Data[i] != db[i] || ra.Data[i + 1] != db[i + 1] ||
                    ra.Data[i + 2] != db[i + 2] || ra.Data[i + 3] != db[i + 3] ||
                    ra.Data[i + 4] != db[i + 4] || ra.Data[i + 5] != db[i + 5] ||
                    ra.Data[i + 6] != db[i + 6] || ra.Data[i + 7] != db[i + 7])
                {
                    double before = BitConverter.ToDouble(ra.Data, i);
                    double after = BitConverter.ToDouble(db, i);
                    // 只保留"两边都是合理的小数/整数"的变化，过滤指针和垃圾
                    if (IsPlausibleNumber(before) && IsPlausibleNumber(after) &&
                        Math.Abs(before - after) > 1e-9)
                    {
                        Console.WriteLine($"  0x{ra.Base + i:X}  {before} → {after}");
                        if (++changed >= 300) { Console.WriteLine("  ...（已达 300 上限）"); return 0; }
                    }
                    i += 8;
                }
                else i += 8;
            }
        }
        Console.WriteLine($"共 {changed} 处 double 变化");
        return 0;
    }

    /// <summary>
    /// 在两个快照里查找特定数值转换（如 46 → 6）。
    /// 直接定位目标字段，避免被图形/计时器噪声淹没。
    /// </summary>
    private static int CmdFindChange(IntPtr h, string[] a)
    {
        // findchange <pid> <snapA> <snapB> <before> <after>
        if (a.Length < 6) { Console.Error.WriteLine("用法: findchange <pid> <snapA> <snapB> <before> <after>"); return 2; }
        double wantBefore = double.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture);
        double wantAfter = double.Parse(a[5], System.Globalization.CultureInfo.InvariantCulture);

        var regionsA = LoadSnap(a[2]);
        var dictB = LoadSnap(a[3]).ToDictionary(r => r.Base, r => r.Data);
        int hits = 0;

        foreach (var ra in regionsA)
        {
            if (!dictB.TryGetValue(ra.Base, out byte[] db)) continue;
            int n = Math.Min(ra.Data.Length, db.Length);

            for (int i = 0; i + 8 <= n; i += 4)   // 4 字节步进，兼容 float 对齐的数组成员
            {
                double before = BitConverter.ToDouble(ra.Data, i);
                if (Math.Abs(before - wantBefore) > 1e-9) continue;
                double after = BitConverter.ToDouble(db, i);
                if (Math.Abs(after - wantAfter) > 1e-9) continue;

                Console.WriteLine($"  0x{ra.Base + i:X}  {(i % 8 == 0 ? "8字节对齐" : "4字节对齐")}");
                hits++;
            }
        }
        Console.WriteLine($"匹配 {wantBefore} → {wantAfter} 共 {hits} 处");
        return 0;
    }

    private static bool IsPlausibleNumber(double v)
        => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) < 1e12 && (v == 0 || Math.Abs(v) > 1e-6);

    private static List<(long Base, byte[] Data)> LoadSnap(string path)
    {
        var list = new List<(long, byte[])>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var br = new BinaryReader(fs);
        while (fs.Position < fs.Length)
        {
            long b = br.ReadInt64();
            int len = br.ReadInt32();
            list.Add((b, br.ReadBytes(len)));
        }
        return list;
    }

    /// <summary>写入原始字节。带前值校验：只有当前值与 expect 一致才写。</summary>
    private static int CmdWrite(IntPtr h, string[] a)
    {
        // write <pid> <addr> <hexBytes> [expectHexBytes]
        if (a.Length < 4) { Console.Error.WriteLine("用法: write <pid> <addr> <hexBytes> [expectHex]"); return 2; }
        long addr = ParseAddr(a[2]);
        byte[] data = Convert.FromHexString(a[3].Replace(" ", ""));
        byte[] expect = a.Length > 4 ? Convert.FromHexString(a[4].Replace(" ", "")) : null;

        if (expect != null)
        {
            byte[] cur = new byte[expect.Length];
            if (!ReadProcessMemory(h, new IntPtr(addr), cur, expect.Length, out _))
            {
                Console.Error.WriteLine($"前值读取失败 (Win32 {Marshal.GetLastWin32Error()})");
                return 1;
            }
            if (!cur.AsSpan().SequenceEqual(expect))
            {
                Console.Error.WriteLine($"前值校验失败：期望 {Convert.ToHexString(expect)} 实际 {Convert.ToHexString(cur)}，拒绝写入");
                return 3;
            }
        }

        if (!VirtualProtectEx(h, new IntPtr(addr), (IntPtr)data.Length, 0x04 /*PAGE_READWRITE*/, out uint old))
        {
            Console.Error.WriteLine($"VirtualProtectEx 失败 (Win32 {Marshal.GetLastWin32Error()})");
            return 1;
        }
        try
        {
            if (!WriteProcessMemory(h, new IntPtr(addr), data, (IntPtr)data.Length, out IntPtr written) || written.ToInt64() != data.Length)
            {
                Console.Error.WriteLine($"写入失败 (Win32 {Marshal.GetLastWin32Error()})");
                return 1;
            }
        }
        finally { VirtualProtectEx(h, new IntPtr(addr), (IntPtr)data.Length, old, out _); }

        // 读回确认
        byte[] verify = new byte[data.Length];
        ReadProcessMemory(h, new IntPtr(addr), verify, data.Length, out _);
        Console.WriteLine($"0x{addr:X} 写入 {Convert.ToHexString(data)} → 读回 {Convert.ToHexString(verify)}  {(verify.AsSpan().SequenceEqual(data) ? "OK" : "不一致!")}");
        return 0;
    }

    /// <summary>写入 double 值，带前值校验。</summary>
    private static int CmdWriteDouble(IntPtr h, string[] a)
    {
        // writedouble <pid> <addr> <value> [expectValue]
        if (a.Length < 4) { Console.Error.WriteLine("用法: writedouble <pid> <addr> <value> [expect]"); return 2; }
        long addr = ParseAddr(a[2]);
        double val = double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
        byte[] data = BitConverter.GetBytes(val);

        if (a.Length > 4)
        {
            double expect = double.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture);
            byte[] cur = new byte[8];
            if (!ReadProcessMemory(h, new IntPtr(addr), cur, 8, out _))
            {
                Console.Error.WriteLine("前值读取失败");
                return 1;
            }
            double curVal = BitConverter.ToDouble(cur, 0);
            if (Math.Abs(curVal - expect) > 1e-9)
            {
                Console.Error.WriteLine($"前值校验失败：期望 {expect} 实际 {curVal}，拒绝写入");
                return 3;
            }
        }

        VirtualProtectEx(h, new IntPtr(addr), (IntPtr)8, 0x04, out uint old);
        try
        {
            if (!WriteProcessMemory(h, new IntPtr(addr), data, (IntPtr)8, out _))
            {
                Console.Error.WriteLine($"写入失败 (Win32 {Marshal.GetLastWin32Error()})");
                return 1;
            }
        }
        finally { VirtualProtectEx(h, new IntPtr(addr), (IntPtr)8, old, out _); }

        byte[] verify = new byte[8];
        ReadProcessMemory(h, new IntPtr(addr), verify, 8, out _);
        Console.WriteLine($"0x{addr:X} {expect_or_dash(a)} → {val}  读回 {BitConverter.ToDouble(verify, 0)}");
        return 0;
    }

    private static string expect_or_dash(string[] a) => a.Length > 4 ? a[4] : "?";

    private static int Unknown()
    {
        Usage();
        return 2;
    }

    private static void Usage()
    {
        Console.WriteLine("""
用法:
  GtProbe dump        <pid> <addr> [bytes]        转储内存（十六进制 + ASCII）
  GtProbe qword       <pid> <addr>                读 8 字节并按多类型解释
  GtProbe find        <pid> <hexBytes>            全进程搜索字节序列
  GtProbe regions     <pid>                       列出可读内存区域
  GtProbe write       <pid> <addr> <hex> [expect] 写字节（可带前值校验）
  GtProbe writedouble <pid> <addr> <v> [expect]   写 double（可带前值校验）
""");
    }

    private static long ParseAddr(string s)
        => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(s[2..], 16)
            : long.Parse(s);

    /// <summary>转储内存，同时给出常见类型的解释，便于判断结构。</summary>
    private static int CmdDump(IntPtr h, string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("需要地址"); return 2; }
        long addr = ParseAddr(a[2]);
        int len = a.Length > 3 ? int.Parse(a[3]) : 128;

        byte[] buf = new byte[len];
        if (!ReadProcessMemory(h, new IntPtr(addr), buf, len, out IntPtr read))
        {
            Console.Error.WriteLine($"读取失败 (Win32 {Marshal.GetLastWin32Error()})");
            return 1;
        }

        Console.WriteLine($"地址 0x{addr:X}  读到 {read} 字节");
        for (int i = 0; i < read; i += 16)
        {
            var hex = new StringBuilder();
            var asc = new StringBuilder();
            for (int j = 0; j < 16 && i + j < read; j++)
            {
                byte b = buf[i + j];
                hex.Append(b.ToString("X2")).Append(' ');
                asc.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            Console.WriteLine($"  +{i:X4}  {hex,-48}  {asc}");
        }

        Console.WriteLine("\n按类型解释（偏移 0 处）:");
        if (read >= 8)
        {
            Console.WriteLine($"  double  = {BitConverter.ToDouble(buf, 0)}");
            Console.WriteLine($"  int64   = {BitConverter.ToInt64(buf, 0)}");
            Console.WriteLine($"  float   = {BitConverter.ToSingle(buf, 0)}");
            Console.WriteLine($"  int32   = {BitConverter.ToInt32(buf, 0)}");
            Console.WriteLine($"  ptr     = 0x{BitConverter.ToInt64(buf, 0):X}");
        }
        return 0;
    }

    private static int CmdQword(IntPtr h, string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("需要地址"); return 2; }
        long addr = ParseAddr(a[2]);
        byte[] buf = new byte[8];
        if (!ReadProcessMemory(h, new IntPtr(addr), buf, 8, out _))
        {
            Console.Error.WriteLine($"读取失败 (Win32 {Marshal.GetLastWin32Error()})");
            return 1;
        }
        Console.WriteLine($"0x{addr:X}  {Convert.ToHexString(buf)}");
        Console.WriteLine($"  double = {BitConverter.ToDouble(buf, 0)}");
        Console.WriteLine($"  int64  = {BitConverter.ToInt64(buf, 0)}");
        Console.WriteLine($"  float  = {BitConverter.ToSingle(buf, 0)}");
        Console.WriteLine($"  int32  = {BitConverter.ToInt32(buf, 0)}");
        return 0;
    }

    /// <summary>全进程搜索字节序列，返回所有命中地址。</summary>
    private static int CmdFind(IntPtr h, string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("需要十六进制字节串"); return 2; }
        byte[] pat = Convert.FromHexString(a[2].Replace(" ", ""));
        int found = 0;

        long addr = 0;
        while (true)
        {
            if (VirtualQueryEx(h, new IntPtr(addr), out MEMORY_BASIC_INFORMATION mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                break;

            long size = mbi.RegionSize.ToInt64();
            bool readable = mbi.State == MEM_COMMIT && (mbi.Protect & 0x100) == 0 &&
                            (mbi.Protect & 0xFF) is 0x02 or 0x04 or 0x20 or 0x40 or 0x08 or 0x80;

            if (readable && size > 0 && size < 512L * 1024 * 1024)
            {
                byte[] buf = new byte[size];
                if (ReadProcessMemory(h, mbi.BaseAddress, buf, (IntPtr)size, out IntPtr read))
                {
                    int n = (int)read;
                    for (int i = 0; i <= n - pat.Length; i++)
                    {
                        if (buf[i] != pat[0]) continue;
                        bool ok = true;
                        for (int j = 1; j < pat.Length; j++)
                            if (buf[i + j] != pat[j]) { ok = false; break; }
                        if (ok) { Console.WriteLine($"  0x{mbi.BaseAddress.ToInt64() + i:X}"); found++; if (found >= 200) { Console.WriteLine("  ...（已达 200 上限）"); return 0; } }
                    }
                }
            }
            long next = mbi.BaseAddress.ToInt64() + size;
            if (next <= addr) break;
            addr = next;
        }
        Console.WriteLine($"命中 {found} 处");
        return 0;
    }

    private static int CmdRegions(IntPtr h)
    {
        long addr = 0;
        while (true)
        {
            if (VirtualQueryEx(h, new IntPtr(addr), out MEMORY_BASIC_INFORMATION mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                break;
            long size = mbi.RegionSize.ToInt64();
            if (mbi.State == MEM_COMMIT)
                Console.WriteLine($"  0x{mbi.BaseAddress.ToInt64():X12}  {size / 1024,8} KB  Protect=0x{mbi.Protect:X}  Type=0x{mbi.Type:X}");
            long next = mbi.BaseAddress.ToInt64() + size;
            if (next <= addr) break;
            addr = next;
        }
        return 0;
    }
}
