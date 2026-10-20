using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameTrainer.Native;

internal static class NativeMethods
{
    // ---- 进程访问权限 ----
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_VM_OPERATION = 0x0008;

    // ---- 内存区域状态 ----
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_PRIVATE = 0x20000;
    public const uint MEM_IMAGE = 0x1000000;
    public const uint MEM_MAPPED = 0x40000;
    public const uint PAGE_GUARD = 0x100;
    public const uint PAGE_NOACCESS = 0x01;

    // ---- 页保护属性（Windows SDK winnt.h）----
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    public const uint PAGE_WRITECOPY = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(
        SafeProcessHandle hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(
        SafeProcessHandle hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualQueryEx(
        SafeProcessHandle hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualProtectEx(
        SafeProcessHandle hProcess, IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    /// <summary>读取进程创建时间，作为跨打开稳定的身份凭证。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(
        SafeProcessHandle hProcess,
        out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    public static bool IsReadable(uint protect)
    {
        if ((protect & PAGE_GUARD) != 0) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        uint baseProtect = protect & 0xFF;
        return baseProtect is PAGE_READONLY or PAGE_READWRITE or PAGE_EXECUTE_READ
            or PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY or PAGE_WRITECOPY;
    }

    public static bool IsWritable(uint protect)
    {
        if ((protect & PAGE_GUARD) != 0) return false;
        if ((protect & PAGE_NOACCESS) != 0) return false;
        uint baseProtect = protect & 0xFF;
        return baseProtect is PAGE_READWRITE or PAGE_EXECUTE_READWRITE or PAGE_WRITECOPY
            or PAGE_EXECUTE_WRITECOPY;
    }

    /// <summary>保护属性的可读名称，用于审计日志。</summary>
    public static string ProtectName(uint protect)
    {
        uint p = protect & 0xFF;
        string name = p switch
        {
            PAGE_NOACCESS => "NOACCESS",
            PAGE_READONLY => "R",
            PAGE_READWRITE => "RW",
            PAGE_WRITECOPY => "WC",
            PAGE_EXECUTE_READ => "XR",
            PAGE_EXECUTE_READWRITE => "XRW",
            PAGE_EXECUTE_WRITECOPY => "XWC",
            _ => $"0x{p:X}"
        };
        if ((protect & PAGE_GUARD) != 0) name += "|GUARD";
        return name;
    }
}
