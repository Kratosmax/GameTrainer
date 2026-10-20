using System.Runtime.InteropServices;

namespace GameTrainer.Native;

/// <summary>
/// DWM 窗口材质。按 desktop-tool-ui-release / windows-native.md 的基线实现：
///
///  1. 先按 Windows 构建号做前置能力判断，但最终以每次调用的 HRESULT 为准。
///  2. DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)
///     启用系统 Acrylic 类材质。
///  3. DwmExtendFrameIntoClientArea 四边 -1，把 DWM frame 延伸到整个客户区，
///     否则材质只出现在标题栏。
///  4. DWMWA_WINDOW_CORNER_PREFERENCE 请求系统圆角。
///  5. 任一步失败立即完整回退：backdrop 设回 DWMSBT_NONE、margins 归零，
///     不能留下「标题栏有材质 + 客户区不透明」的混合状态。
///
/// 常量全部附官方枚举名，不在业务代码里散落魔法数字。
/// 来源：Windows SDK dwmapi.h / Microsoft Learn DwmSetWindowAttribute。
/// </summary>
internal static class WindowMaterial
{
    // DWMWINDOWATTRIBUTE（dwmapi.h）
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // DWM_WINDOW_CORNER_PREFERENCE
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    // DWM_SYSTEMBACKDROP_TYPE
    private const int DWMSBT_AUTO = 0;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMSBT_TABBEDWINDOW = 4;

    /// <summary>DWMSBT_TRANSIENTWINDOW 需要 Windows 11 build 22621+。</summary>
    private const int MinBackdropBuild = 22621;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// <summary>
    /// 材质应用结果。调用方必须据此决定用透明根表面还是不透明回退，
    /// 不能只凭系统版本号宣称成功。
    /// </summary>
    public readonly record struct Result(
        bool Applied,
        bool BackdropApplied,
        bool FrameExtended,
        bool CornerApplied,
        string Detail)
    {
        public static Result Fallback(string why) => new(false, false, false, false, why);
    }

    /// <summary>当前系统构建号。仅作前置判断，不作为成功依据。</summary>
    public static int CurrentBuild()
    {
        // Environment.OSVersion 在 Windows 上报告真实构建号（manifest 声明 supportedOS 后不撒谎）
        return Environment.OSVersion.Version.Build;
    }

    public static bool BackdropSupported => CurrentBuild() >= MinBackdropBuild;

    /// <summary>
    /// 在 HWND 创建之后调用。必须在 Handle 可用后执行（Form.OnHandleCreated 或更晚）。
    /// </summary>
    public static Result ApplyGlass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return Result.Fallback("窗口句柄未创建");

        if (!BackdropSupported)
        {
            // Windows 10 或更早：明确回退，不尝试调用不存在的属性
            return Result.Fallback($"系统构建 {CurrentBuild()} 低于 {MinBackdropBuild}，使用不透明表面");
        }

        // 1) 系统背景材质：Acrylic/Transient。HRESULT 为负即失败。
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        int hrBackdrop = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE,
            ref backdrop, sizeof(int));
        if (hrBackdrop < 0)
            return Result.Fallback($"DwmSetWindowAttribute(SYSTEMBACKDROP_TYPE) HRESULT=0x{hrBackdrop:X8}");

        // 2) 把 frame 延伸到整个客户区，否则材质只覆盖标题栏
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        int hrFrame = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (hrFrame < 0)
        {
            // 关键：backdrop 已生效但客户区没延伸，会得到"半透明标题栏 + 不透明正文"的混合态。
            // 必须撤销 backdrop，整体走回退。
            int none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            return Result.Fallback($"DwmExtendFrameIntoClientArea HRESULT=0x{hrFrame:X8}");
        }

        // 3) 系统圆角。失败不致命——圆角只是装饰，不影响可读性，降级为无圆角继续。
        int corner = DWMWCP_ROUND;
        int hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
            ref corner, sizeof(int));
        bool cornerOk = hrCorner >= 0;

        // 4) 强制浅色标题栏，与当前浅色设计一致；失败同样只是降级。
        int lightMode = 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref lightMode, sizeof(int));

        string detail = cornerOk
            ? "backdrop=Acrylic, frame=客户区, corner=系统圆角"
            : $"backdrop=Acrylic, frame=客户区, corner 降级 HRESULT=0x{hrCorner:X8}";

        return new Result(true, true, true, cornerOk, detail);
    }

    /// <summary>
    /// 强制回退：撤销材质与 frame 扩展。用于关闭材质、切换主题或诊断开关。
    /// </summary>
    public static Result RevertToOpaque(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Result.Fallback("窗口句柄未创建");

        int none = DWMSBT_NONE;
        int hr1 = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));

        var zero = new MARGINS();
        int hr2 = DwmExtendFrameIntoClientArea(hwnd, ref zero);

        int noRound = DWMWCP_DEFAULT;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));

        bool ok = hr1 >= 0 && hr2 >= 0;
        return new Result(false, false, false, false,
            ok ? "已回退为不透明表面" : $"回退部分失败 HRESULT=0x{hr1:X8}/0x{hr2:X8}");
    }
}
