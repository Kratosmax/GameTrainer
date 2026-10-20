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
        foreach (string drv in KnownDrivers)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{drv}");
                if (key != null) found.Add($"驱动服务: {drv}");
            }
            catch { }
        }

        if (found.Count == 0) return new Result(false, "未检测到已知反作弊。", found);

        string reason = "检测到反作弊组件，已强制切换为只读模式，拒绝一切内存写入：\n  - "
                      + string.Join("\n  - ", found.Distinct());
        return new Result(true, reason, found.Distinct().ToList());
    }
}
