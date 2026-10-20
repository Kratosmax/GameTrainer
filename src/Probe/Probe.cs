using System.Runtime.InteropServices;
using System.Text;

// 直接验证：哪个地址是靶进程的真状态块
internal static class Probe
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint a, bool i, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int pid = int.Parse(args[0]);
        IntPtr h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) { Console.WriteLine("OpenProcess 失败"); return; }

        foreach (string a in args.Skip(1))
        {
            long addr = Convert.ToInt64(a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? a[2..] : a,
                                        a.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
            byte[] buf = new byte[0x38];
            if (!ReadProcessMemory(h, new IntPtr(addr), buf, buf.Length, out IntPtr read))
            {
                Console.WriteLine($"0x{addr:X}: 读取失败");
                continue;
            }
            double gold = BitConverter.ToDouble(buf, 0x00);
            int level = BitConverter.ToInt32(buf, 0x08);
            int health = BitConverter.ToInt32(buf, 0x0C);
            int maxHp = BitConverter.ToInt32(buf, 0x10);
            float speed = BitConverter.ToSingle(buf, 0x18);
            long score = BitConverter.ToInt64(buf, 0x20);
            string anchor = string.Join(' ', buf.Skip(0x28).Take(8).Select(x => x.ToString("X2")));
            int tick = BitConverter.ToInt32(buf, 0x30);

            Console.WriteLine($"0x{addr:X}:");
            Console.WriteLine($"   Gold={gold}  Level={level}  Health={health}/{maxHp}  Speed={speed}  Score={score}");
            Console.WriteLine($"   Anchor={anchor}  Tick={tick}");
        }
        CloseHandle(h);
    }
}
