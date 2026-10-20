using System.Diagnostics;
using Microsoft.Win32;

namespace GameTrainer.Core;

/// <summary>
/// 用户级开机自启。
/// 用 HKCU\...\Run，无需管理员。值必须是带引号的绝对路径，不能拼成待 shell 解析的命令。
/// 必须支持查询、启用、更新、禁用；程序移动后能检测旧路径并修复。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GameTrainer";
    private const string BackgroundArg = "--background";

    public static string CurrentExecutablePath
    {
        get
        {
            // 单文件发布时 Assembly.Location 为空，必须用 ProcessPath
            string p = Environment.ProcessPath;
            return string.IsNullOrEmpty(p) ? Process.GetCurrentProcess().MainModule?.FileName : p;
        }
    }

    private static string DesiredValue => $"\"{CurrentExecutablePath}\" {BackgroundArg}";

    public enum Status { NotRegistered, Registered, PathStale, Error }

    public sealed record Info(Status Status, string RegisteredValue, string ExpectedValue, string Error)
    {
        public bool IsEnabled => Status == Status.Registered;
        public string Describe() => Status switch
        {
            Status.Registered => "已启用",
            Status.NotRegistered => "未启用",
            Status.PathStale => "已启用，但指向旧路径（需要修复）",
            Status.Error => "无法读取注册表",
            _ => "未知"
        };
    }

    public static Info Query()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            string existing = key?.GetValue(ValueName) as string;

            if (string.IsNullOrEmpty(existing))
                return new Info(Status.NotRegistered, null, DesiredValue, null);

            // 路径不一致说明程序被移动或升级过，需要修复
            if (!string.Equals(existing, DesiredValue, StringComparison.OrdinalIgnoreCase))
                return new Info(Status.PathStale, existing, DesiredValue, null);

            return new Info(Status.Registered, existing, DesiredValue, null);
        }
        catch (Exception ex)
        {
            return new Info(Status.Error, null, DesiredValue, ex.Message);
        }
    }

    /// <summary>启用或修复。写入失败不影响用户已有值。</summary>
    public static bool Enable(out string error)
    {
        error = null;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) { error = "无法打开开机自启注册表键。"; return false; }
            key.SetValue(ValueName, DesiredValue, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = $"写入注册表失败：{ex.Message}";
            return false;
        }
    }

    public static bool Disable(out string error)
    {
        error = null;
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return true;
            if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = $"删除注册表值失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>卸载清理用。与 Disable 等价，语义分开便于以后扩展。</summary>
    public static bool CleanupOnUninstall(out string error) => Disable(out error);
}
