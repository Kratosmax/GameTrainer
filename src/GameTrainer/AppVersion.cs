using System.Reflection;

namespace GameTrainer;

/// <summary>
/// 版本唯一来源。
///
/// 真源是仓库根的 Directory.Build.props 里的 GameTrainerVersion，
/// 它同时注入程序集版本，所以运行时读程序集即可，不在这里写死字符串。
/// 显示时只给 SemVer 三段，不显示四段程序集版本。
/// </summary>
internal static class AppVersion
{
    /// <summary>内部完整版本（可能含修订号）。</summary>
    public static Version Full { get; } = ReadVersion();

    /// <summary>对外 SemVer：Major.Minor.Patch。</summary>
    public static string Display => $"{Full.Major}.{Full.Minor}.{(Full.Build < 0 ? 0 : Full.Build)}";

    private static Version ReadVersion()
    {
        Assembly asm = typeof(AppVersion).Assembly;

        // InformationalVersion 可能带 +commit 后缀，先切掉
        string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int cut = info.IndexOf('+');
            if (cut > 0) info = info[..cut];
            if (Version.TryParse(info, out Version parsed)) return parsed;
        }

        return asm.GetName().Version ?? new Version(0, 0, 0);
    }
}
