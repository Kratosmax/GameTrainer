namespace GameTrainer.Core;

/// <summary>
/// 反作弊闸。
///
/// 硬规则：检测到已知反作弊模块加载 → 强制只读，拒绝一切写入，
/// 并（若此前已启用）立即回滚已写入的值。
///
/// 注意：这里只做"识别并阻断"，绝不包含任何绕过、隐藏、伪装或校验伪造逻辑。
/// 本工具不试图与反作弊共存。
/// </summary>
internal static class AntiCheatGuard
{
    /// <summary>常见反作弊模块名。命中任一即视为活跃。</summary>
    private static readonly string[] KnownModules =
    {
        // Easy Anti-Cheat
        "EasyAntiCheat.exe", "EasyAntiCheat_EOS.exe", "EasyAntiCheat.sys",
        "EasyAntiCheat_EOS.sys", "EasyAntiCheat_x64.dll", "EasyAntiCheat_EOS_x64.dll",
        // BattlEye
        "BEService.exe", "BEService_x64.exe", "BEClient_x64.dll", "BEClient.dll",
        "BEService.sys", "bedaisy.sys",
        // Riot Vanguard
        "vgc.exe", "vgk.sys", "vgtray.exe",
        // Denuvo Anti-Cheat / nProtect / XignCode / GameGuard
        "denuvo-anti-cheat.exe", "DenuvoAC.sys", "XNina.xem", "xigncode.exe",
        "GameGuard.des", "npggNT.des", "npggNT64.des",
        // ESEA / FACEIT / Ricochet / VAC 相关进程
        "ESEADriver2.sys", "faceit-ac.sys", "Ricochet.sys",
        // PunkBuster
        "PnkBstrA.exe", "PnkBstrB.exe", "PnkBstrK.sys",
    };

    /// <summary>已知反作弊的驱动/服务名（用于更早发现）。</summary>
    private static readonly string[] KnownDrivers =
    {
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEDaisy", "BEService",
        "vgk", "vgc", "DenuvoAC", "PnkBstrK", "XNina",
    };

    public sealed record Result(bool Blocked, string Reason, IReadOnlyList<string> Found);

    /// <summary>
    /// 检查目标进程内外是否存在反作弊。
    /// 覆盖三层：目标进程已加载模块、同机运行的反作弊进程、内核驱动服务。
    /// </summary>
    public static Result Check(Memory.ProcessMemory target)
    {
        var found = new List<string>();

        // 1) 目标进程内已加载的模块
        if (target != null)
        {
            try
            {
                foreach (System.Diagnostics.ProcessModule m in target.Process.Modules)
                {
                    foreach (string k in KnownModules)
                        if (string.Equals(m.ModuleName, k, StringComparison.OrdinalIgnoreCase))
                            found.Add($"进程内模块: {m.ModuleName}");
                }
            }
            catch { /* 模块枚举失败不阻断，但会在下面继续检查其他层 */ }
        }

        // 2) 系统上正在运行的反作弊进程
        foreach (string k in KnownModules.Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            string name = k[..^4];
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
                    found.Add($"运行中进程: {k}");
            }
            catch { }
        }

        // 3) 内核驱动服务（反作弊通常以驱动形态存在）
        //
        // 关键：只认「服务存在」会大面积误报——
        // 装过任意 Riot 游戏的机器上 vgk 服务键永远存在，即使游戏没开。
        // 必须同时确认服务处于运行态（Start=0/1 是引导/系统启动，且当前 State 为 RUNNING）。
        foreach (string drv in KnownDrivers)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{drv}");
                if (key == null) continue;

                // Start: 0=Boot, 1=System, 2=Auto, 3=Manual, 4=Disabled
                object startObj = key.GetValue("Start");
                int start = startObj is int si ? si : -1;
                if (start == 4) continue;   // 已禁用，不算活跃

                // 再看服务当前是否真的在跑
                string state = ReadServiceState(drv);
                if (state == "RUNNING")
                    found.Add($"驱动服务运行中: {drv}");
            }
            catch { }
        }

        if (found.Count == 0) return new Result(false, "未检测到已知反作弊。", found);

        string reason = "检测到反作弊组件，已强制切换为只读模式，拒绝一切内存写入：\n  - "
                      + string.Join("\n  - ", found.Distinct());
        return new Result(true, reason, found.Distinct().ToList());
    }

    // ---------------- 服务状态查询（SCM） ----------------

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int SC_STATUS_PROCESS_INFO = 0;
    private const uint SERVICE_RUNNING = 0x00000004;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref SERVICE_STATUS status);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>
    /// 查询服务当前状态。返回 "RUNNING" / "STOPPED" / null（查不到）。
    /// 只读查询，不做任何服务控制操作。
    /// </summary>
    private static string ReadServiceState(string serviceName)
    {
        IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
        try
        {
            scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return null;

            svc = OpenServiceW(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return null;

            var st = new SERVICE_STATUS();
            if (!QueryServiceStatus(svc, ref st)) return null;

            return st.dwCurrentState == SERVICE_RUNNING ? "RUNNING" : "STOPPED";
        }
        catch
        {
            return null;
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }
}
