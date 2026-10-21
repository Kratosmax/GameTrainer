using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GameTrainer.Native;

namespace GameTrainer.Memory;

/// <summary>
/// 目标进程的稳定身份。PID 会被复用——只凭 PID 做后续操作会产生 TOCTOU 窗口。
/// 因此一次打开句柄，并在同一句柄上读取创建时间作为身份凭证。
/// </summary>
internal sealed record ProcessIdentity(int Pid, long CreateTimeUtcTicks, string ExecutablePath)
{
    /// <summary>用同一句柄重新确认身份是否仍一致。不一致说明 PID 已被复用。</summary>
    public bool StillValid(SafeProcessHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed) return false;
        if (!NativeMethods.GetProcessTimes(handle, out long create, out _, out _, out _)) return false;
        return create == CreateTimeUtcTicks;
    }

    public override string ToString() => $"PID {Pid} @ {CreateTimeUtcTicks}";
}

/// <summary>
/// 一个目标进程的读写句柄。
///
/// 安全约定：
///  - 句柄用 SafeProcessHandle 包装，所有路径（含异常）都由 using/Dispose 关闭
///  - 打开时记录创建时间作为身份，后续操作前用 StillValid 校验
///  - 只申请完成动作所需的最小访问权限
/// </summary>
internal sealed class ProcessMemory : IDisposable
{
    public SafeProcessHandle Handle { get; }
    public int Pid => Identity.Pid;
    public ProcessIdentity Identity { get; }
    public Process Process { get; }

    private readonly List<(IntPtr Base, long Size, uint Protect, uint Type)> _regions = new();
    public IReadOnlyList<(IntPtr Base, long Size, uint Protect, uint Type)> Regions => _regions;

    private bool _disposed;

    private ProcessMemory(Process p, SafeProcessHandle handle, ProcessIdentity identity)
    {
        Process = p;
        Handle = handle;
        Identity = identity;
        EnumerateRegions();
    }

    /// <summary>
    /// 附加到进程。返回的实例持有句柄，调用方负责 Dispose。
    /// </summary>
    public static ProcessMemory Attach(int pid)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { throw new InvalidOperationException($"进程 {pid} 不存在。"); }

        // 最小权限：读 + 查询。写权限在需要时另行申请，避免长期持有可写句柄。
        uint access = NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ;

        SafeProcessHandle h = NativeMethods.OpenProcess(access, false, pid);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            p.Dispose();
            throw new InvalidOperationException(
                $"OpenProcess 失败 (Win32 {err}: {new Win32Exception(err).Message})。"
                + "常见原因：目标是提权进程（需以管理员身份运行本工具），或目标进程已退出。");
        }

        if (!NativeMethods.GetProcessTimes(h, out long create, out _, out _, out _))
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            p.Dispose();
            throw new InvalidOperationException($"无法读取进程创建时间 (Win32 {err})，无法建立稳定身份，拒绝继续。");
        }

        string path = null;
        try { path = p.MainModule?.FileName; } catch { }

        var identity = new ProcessIdentity(pid, create, path);
        var pm = new ProcessMemory(p, h, identity);
        return pm;
    }

    public static ProcessMemory AttachByName(string processName)
    {
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4] : processName;

        Process[] all = Process.GetProcessesByName(name);
        if (all.Length == 0)
            throw new InvalidOperationException($"未找到进程 {name}.exe，请先启动游戏。");

        // NW.js / Chromium 类游戏有多个同名进程，取工作集最大的通常为主进程
        Process target = all.OrderByDescending(p => p.WorkingSet64).First();
        int pid = target.Id;
        foreach (Process other in all) other.Dispose();

        return Attach(pid);
    }

    /// <summary>每次危险操作前调用。身份不符直接抛异常，绝不继续。</summary>
    public void EnsureIdentity()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessMemory));
        if (!Identity.StillValid(Handle))
            throw new InvalidOperationException(
                $"目标进程身份已变化（{Identity}）。PID 可能已被系统复用，已中止操作以防误伤其他进程。");
    }

    public void RefreshRegions()
    {
        EnsureIdentity();
        EnumerateRegions();
    }

    private void EnumerateRegions()
    {
        _regions.Clear();
        IntPtr addr = IntPtr.Zero;
        long maxAddr = Environment.Is64BitProcess ? 0x7FFFFFFF_FFFF : 0x7FFF_FFFF;
        int mbiSize = Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION>();

        while (addr.ToInt64() < maxAddr)
        {
            IntPtr r = NativeMethods.VirtualQueryEx(Handle, addr, out var mbi, (IntPtr)mbiSize);
            if (r == IntPtr.Zero) break;

            long size = (long)mbi.RegionSize;
            if (size <= 0) break;

            if (mbi.State == NativeMethods.MEM_COMMIT)
                _regions.Add((mbi.BaseAddress, size, mbi.Protect, mbi.Type));

            long next = addr.ToInt64() + size;
            if (next <= addr.ToInt64()) break;
            addr = new IntPtr(next);
        }
    }

    public byte[] Read(IntPtr address, int size)
    {
        EnsureIdentity();
        byte[] buf = new byte[size];
        if (!NativeMethods.ReadProcessMemory(Handle, address, buf, (IntPtr)size, out IntPtr read)
            || read.ToInt64() != size)
            throw new InvalidOperationException(
                $"读取 0x{address.ToInt64():X} 失败（期望 {size} 字节，实际 {read.ToInt64()}）。");
        return buf;
    }

    public bool TryRead(IntPtr address, byte[] buf, out int read)
    {
        read = 0;
        if (_disposed || Handle.IsInvalid || Handle.IsClosed) return false;
        if (!Identity.StillValid(Handle)) return false;

        bool ok = NativeMethods.ReadProcessMemory(Handle, address, buf, (IntPtr)buf.Length, out IntPtr r);
        read = (int)r.ToInt64();
        return ok && read == buf.Length;
    }

    /// <summary>
    /// 写入。写前必须已通过签名/前值校验。
    ///
    /// 三道闸，任一不通过则不写：
    ///   1. 目标必须在已提交区域内
    ///   2. 【禁止改代码段】MEM_IMAGE 区域一律拒绝——即使能通过 VirtualProtect 改成可写，
    ///      覆盖的是游戏指令，会触发完整性校验且几乎无法恢复。
    ///      这条对应 skill 硬性红线「修改 .text 代码段」。
    ///   3. 页保护属性为 NOACCESS / GUARD 时拒绝（不是"改一下就能写"的场景）
    ///
    /// 第 2 条是关键：AOB 签名或 chain 偏移算错时，地址可能落进 .text。
    /// 没有这层拦截，工具会先把只读代码页改成可写再覆盖，后果不可逆。
    /// </summary>
    public void Write(IntPtr address, byte[] data)
    {
        EnsureIdentity();

        RegionInfo info = DescribeRegion(address, data.Length)
            ?? throw new InvalidOperationException($"0x{address.ToInt64():X} 不在任何已提交区域内，拒绝写入。");

        // 闸 2：代码段/映射镜像一律拒绝。TYPE 为 MEM_IMAGE 就是可执行映像（.text 在里头）。
        if (info.Type == NativeMethods.MEM_IMAGE)
            throw new InvalidOperationException(
                $"0x{address.ToInt64():X} 位于可执行映像（代码段），拒绝写入。\n" +
                "改代码段会触发完整性校验，且几乎无法恢复。请检查 AOB 签名或 chain 偏移是否算错。");

        // 闸 3：完全不可访问的页不做"强改保护再写"
        if ((info.Protect & NativeMethods.PAGE_NOACCESS) != 0 ||
            (info.Protect & NativeMethods.PAGE_GUARD) != 0)
            throw new InvalidOperationException(
                $"0x{address.ToInt64():X} 所在页保护属性为 {NativeMethods.ProtectName(info.Protect)}，拒绝写入。");

        // 临时申请写权限，用完立即释放，不长期持有。
        //
        // 关键：按 PID 重新打开会有 PID 复用窗口（校验通过 → 进程退出 → PID 被新进程占用）。
        // 因此打开后必须在**新句柄**上再确认一次身份，否则会写进无关进程。
        using SafeProcessHandle wh = OpenForWrite(Pid);
        if (wh.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"申请写权限失败 (Win32 {err}) 于 0x{address.ToInt64():X}。");
        }

        if (!Identity.StillValid(wh))
            throw new InvalidOperationException(
                $"写入句柄身份校验失败（{Identity}）。目标进程可能已退出或 PID 已被复用，已中止写入。");

        uint oldProtect = 0;
        bool changedProtect = false;

        if (!NativeMethods.IsWritable(info.Protect))
        {
            if (!NativeMethods.VirtualProtectEx(wh, address, (IntPtr)data.Length,
                    NativeMethods.PAGE_READWRITE, out oldProtect))
                throw new InvalidOperationException(
                    $"目标页不可写且改保护失败 (0x{info.Protect:X}) 于 0x{address.ToInt64():X}。");
            changedProtect = true;
        }

        try
        {
            if (!NativeMethods.WriteProcessMemory(wh, address, data, (IntPtr)data.Length, out IntPtr written))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"WriteProcessMemory 失败 (Win32 {err}) 于 0x{address.ToInt64():X}。");
            }
            if (written.ToInt64() != data.Length)
                throw new InvalidOperationException(
                    $"部分写入：期望 {data.Length}，实际 {written.ToInt64()} 于 0x{address.ToInt64():X}。");
        }
        finally
        {
            if (changedProtect)
                NativeMethods.VirtualProtectEx(wh, address, (IntPtr)data.Length, oldProtect, out _);
        }
    }

    private static SafeProcessHandle OpenForWrite(int pid)
        => NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_WRITE | NativeMethods.PROCESS_VM_OPERATION, false, pid);

    public RegionInfo DescribeRegion(IntPtr address, int size)
    {
        long a = address.ToInt64();
        long end = a + size;
        foreach (var r in _regions)
        {
            long rb = r.Base.ToInt64();
            long re = rb + r.Size;
            if (a >= rb && end <= re)
                return new RegionInfo(r.Base, r.Size, r.Protect, r.Type);
        }
        return null;
    }

    public IntPtr ModuleBase(string moduleName)
    {
        EnsureIdentity();
        foreach (ProcessModule m in Process.Modules)
        {
            if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                return m.BaseAddress;
        }
        throw new InvalidOperationException($"模块 {moduleName} 未加载于 PID {Pid}。");
    }

    public ProcessModule FindModule(string moduleName)
    {
        EnsureIdentity();
        foreach (ProcessModule m in Process.Modules)
            if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Handle?.Dispose();
        Process?.Dispose();
    }
}

internal sealed record RegionInfo(IntPtr Base, long Size, uint Protect, uint Type);
