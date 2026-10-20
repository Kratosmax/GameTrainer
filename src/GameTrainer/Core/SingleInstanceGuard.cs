using System.Runtime.InteropServices;

namespace GameTrainer.Core;

/// <summary>
/// 单实例守卫。用当前用户作用域的命名 Mutex，不使用全局命名——
/// 本工具无需跨会话共享，全局命名反而扩大权限面。
///
/// 第二实例不静默退出：它会把已有窗口激活到前台再退出。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const int ASFW_ANY = -1;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    private readonly Mutex _mutex;
    public bool IsFirstInstance { get; }
    /// <summary>已存在实例的窗口标题（用于激活）。</summary>
    public string ExistingWindowTitle { get; }

    public SingleInstanceGuard(string appId, string windowTitle = null)
    {
        ExistingWindowTitle = windowTitle;
        // Local\ 前缀 = 当前会话作用域，足够且不越权
        _mutex = new Mutex(initiallyOwned: true, name: @"Local\" + appId, out bool createdNew);
        IsFirstInstance = createdNew;
    }

    /// <summary>把已有实例的窗口调到前台。返回是否成功。</summary>
    public bool TryActivateExisting(string mainWindowTitle)
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);
            IntPtr hwnd = FindWindow(null, mainWindowTitle);
            if (hwnd == IntPtr.Zero) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            return SetForegroundWindow(hwnd);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try
        {
            if (IsFirstInstance) _mutex.ReleaseMutex();
        }
        catch { }
        _mutex.Dispose();
    }
}
