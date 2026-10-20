using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GtTestTarget;

/// <summary>
/// 自制靶进程：模拟一个"有游戏状态的原生程序"，用来验证修改器全链路。
///
/// 用 NativeMemory 分配一块非托管结构体，布局与真实游戏的角色属性块一致：
/// 数值字段连续排列、后面跟一段固定字节作 AOB 锚点。
/// 这样扫描、AOB、指针链、冻结、回滚都能在它身上真实跑一遍。
/// </summary>
internal static unsafe class Program
{
    /// <summary>与真实游戏"角色状态"同构的结构体。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PlayerState
    {
        public double Gold;        // +0x00
        public int Level;          // +0x08
        public int Health;         // +0x0C
        public int MaxHealth;      // +0x10
        public int _pad;           // +0x14
        public float Speed;        // +0x18
        public long Score;         // +0x20
        public fixed byte Anchor[8]; // +0x28  固定字节作 AOB 锚点
        public int Tick;           // +0x30
    }

    private static PlayerState* s_state;
    private static string _savePath;
    private static readonly byte[] AnchorBytes = { 0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56, 0x78 };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 分配非托管内存并初始化
        s_state = (PlayerState*)NativeMemory.AllocZeroed((nuint)sizeof(PlayerState));
        s_state->Gold = 100.0;
        s_state->Level = 1;
        s_state->Health = 50;
        s_state->MaxHealth = 100;
        s_state->Speed = 3.5f;
        s_state->Score = 1000;
        for (int i = 0; i < 8; i++) s_state->Anchor[i] = AnchorBytes[i];
        s_state->Tick = 0;

        _savePath = Path.Combine(Path.GetTempPath(), "gt_test_target_save.txt");
        if (args.Length >= 2 && args[0] == "--save") _savePath = args[1];

        Console.WriteLine($"靶进程 PID   : {Environment.ProcessId}");
        Console.WriteLine($"状态块地址   : 0x{((nint)s_state):X}");
        Console.WriteLine($"存档文件     : {_savePath}");
        Console.WriteLine();
        Console.WriteLine("初始状态:");
        Console.WriteLine($"  Gold(double)     @ +0x00 = {s_state->Gold}");
        Console.WriteLine($"  Level(int32)     @ +0x08 = {s_state->Level}");
        Console.WriteLine($"  Health(int32)    @ +0x0C = {s_state->Health}");
        Console.WriteLine($"  MaxHealth(int32) @ +0x10 = {s_state->MaxHealth}");
        Console.WriteLine($"  Speed(float)     @ +0x18 = {s_state->Speed}");
        Console.WriteLine($"  Score(int64)     @ +0x20 = {s_state->Score}");
        Console.WriteLine($"  Anchor(8 bytes)  @ +0x28 = {Hex(AnchorBytes)}");
        Console.WriteLine();
        Console.WriteLine("交互: g=金币+10  l=等级+1  h=生命+5  s=分数+100  q=退出");
        Console.WriteLine("（游戏每 40 tick 自动给 Health+1，用来验证 freeze 抢写）");

        WriteSave();

        var sw = Stopwatch.StartNew();
        long lastSave = 0;
        // 输出/输入被重定向时 Console.KeyAvailable 会抛异常，先探测一次
        bool canReadKeys = CanReadKeys();

        while (true)
        {
            s_state->Tick++;

            // 模拟游戏自身逻辑刷新字段——用来测 freeze 与游戏抢写
            if (s_state->Tick % 40 == 0 && s_state->Health < s_state->MaxHealth)
                s_state->Health++;

            if (sw.ElapsedMilliseconds - lastSave > 500)
            {
                lastSave = sw.ElapsedMilliseconds;
                WriteSave();
            }

            if (canReadKeys)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(true);
                        switch (char.ToLowerInvariant(k.KeyChar))
                        {
                            case 'g': s_state->Gold += 10; Console.WriteLine($"Gold = {s_state->Gold}"); break;
                            case 'l': s_state->Level++; Console.WriteLine($"Level = {s_state->Level}"); break;
                            case 'h': s_state->Health = Math.Min(s_state->Health + 5, s_state->MaxHealth); Console.WriteLine($"Health = {s_state->Health}"); break;
                            case 's': s_state->Score += 100; Console.WriteLine($"Score = {s_state->Score}"); break;
                            case 'p':
                                Console.WriteLine($"当前: Gold={s_state->Gold} Level={s_state->Level} "
                                                + $"Health={s_state->Health}/{s_state->MaxHealth} Speed={s_state->Speed} Score={s_state->Score}");
                                break;
                            case 'q': NativeMemory.Free(s_state); return 0;
                        }
                    }
                }
                catch (InvalidOperationException) { canReadKeys = false; }
            }

            Thread.Sleep(10);
        }
    }

    private static bool CanReadKeys()
    {
        try { _ = Console.KeyAvailable; return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static void WriteSave()
    {
        try
        {
            string text =
                "{\n" +
                $"  \"gold\": {s_state->Gold.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n" +
                $"  \"level\": {s_state->Level},\n" +
                $"  \"health\": {s_state->Health},\n" +
                $"  \"maxHealth\": {s_state->MaxHealth},\n" +
                $"  \"speed\": {s_state->Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n" +
                $"  \"score\": {s_state->Score}\n" +
                "}\n";
            string tmp = _savePath + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, _savePath, overwrite: true);
        }
        catch { }
    }

    private static string Hex(byte[] b) => string.Join(' ', b.Select(x => x.ToString("X2")));
}
