namespace GameTrainer.UI;

/// <summary>
/// 程序图标。
///
/// 直接从正在运行的 exe 里提取（ApplicationIcon 已嵌入），
/// 不做成外部 .ico 文件——那样打包时要多带一个文件，
/// 而且用户移动/改名 exe 后图标会丢。
///
/// 多尺寸需求：托盘要求 16x16，标题栏 32x32，任务栏可能 48x48。
/// 用 Icon.ExtractAssociatedIcon 只能拿到一种尺寸，所以按需从
/// exe 资源的图标组里挑最接近的。
/// </summary>
internal static class AppIcon
{
    private static Icon _cached;

    /// <summary>
    /// 取指定尺寸的图标。拿不到时回退到系统通用图标，
    /// 保证不会因为图标问题让窗口创建失败。
    /// </summary>
    public static Icon Get(int size = 32)
    {
        if (_cached != null && _cached.Width >= size)
            return _cached;

        try
        {
            string exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                // ExtractAssociatedIcon 返回的是 exe 里最大的那个图标帧
                Icon full = Icon.ExtractAssociatedIcon(exe);
                if (full != null)
                {
                    _cached = full;
                    return full;
                }
            }
        }
        catch { /* 提取失败不致命 */ }

        _cached = SystemIcons.Application;
        return _cached;
    }

    /// <summary>装到窗体上（标题栏 + 任务栏 + Alt+Tab）。</summary>
    public static void Apply(Form form)
    {
        if (form == null) return;
        try
        {
            Icon icon = Get(32);
            if (icon != null) form.Icon = icon;
        }
        catch { }
    }
}
