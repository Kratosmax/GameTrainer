using System.Diagnostics;

namespace GameTrainer.Adapter;

/// <summary>
/// 游戏启动器：替使用者带上调试参数启动目标，消除「每次要记 --remote-debugging-port」的摩擦。
///
/// 这是 skill 第零节「问题 4」的方案 A：不动游戏、不写死路径，
/// 只是把「发现工具」的运行依赖藏到工具内部。
///
/// 设计约束：
///   - 不自动杀进程。已存在同名进程时只做提示与引导，不替使用者做决定。
///   - 启动失败要给出可操作的原因（路径找不到 / 权限不足 / 参数被吞）。
///   - 端口等待有超时，超时后明确告知「进程起来了但端口没通」的区分。
/// </summary>
internal static class GameLauncher
{
    public sealed record Result(
        bool Ok,
        string Message,
        int? Pid = null,
        bool AlreadyRunning = false,
        bool CdpReady = false);

    /// <summary>
    /// 启动游戏并等待 CDP 就绪。
    /// </summary>
    /// <param name="adapter">适配包，提供 exe 名、安装目录、端口与额外参数。</param>
    /// <param name="noWait">只启动不等端口（用于端口本就不可用、只想跑游戏的情形）。</param>
    public static Result Launch(GameAdapter adapter, bool noWait = false, Action<string> progress = null)
    {
        void P(string s) => progress?.Invoke(s);

        int port = adapter.Connection.DebugPort;

        // 1) 已有进程在跑：不自动杀，交给使用者决定
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(adapter.Exe));
        if (running.Length > 0)
        {
            var first = running.First();
            return new Result(false,
                $"游戏已在运行（PID {first.Id}），但它不是由本工具启动的，没有调试端口。\n" +
                $"请手动关闭游戏后再执行 launch，或继续用 probe/sem 前先自己带 --remote-debugging-port={port} 启动。",
                first.Id, AlreadyRunning: true);
        }

        // 2) 定位 exe
        string exePath = ResolveExe(adapter, P);
        if (exePath == null)
        {
            return new Result(false,
                $"找不到 {adapter.Exe}。\n" +
                $"请在适配包里补 connection.installDir（游戏安装目录），例如：\n" +
                $"  \"connection\": {{ \"installDir\": \"E:\\\\Games\\\\<游戏名>\" }}");
        }
        P($"    exe: {exePath}");

        // 3) 组装参数
        var args = new List<string> { $"--remote-debugging-port={port}" };
        args.AddRange(adapter.Connection.LaunchArgs);

        // 4) 启动
        //
        // 关键：必须用 UseShellExecute = true，理由有两条实测教训：
        //   a) false + 继承标准输出时，子进程的输出会被父进程接管，父进程因此不返回，
        //      命令行看起来像卡死；而且杀掉父进程会连带杀掉游戏。
        //   b) 游戏是长期运行的程序，生命周期不该绑定在一次 CLI 调用上。
        // 代价是 ArgumentList 不可用，需要手工拼参数字符串并加引号。
        P($"    启动中：{Path.GetFileName(exePath)} {string.Join(' ', args)}");
        Process proc;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true,
                Arguments = string.Join(' ', args.Select(QuoteArg))
            };
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            return new Result(false, $"启动失败：{ex.Message}\n" +
                $"常见原因：权限不足、exe 被占用、或杀软拦截。");
        }

        if (noWait)
            return new Result(true, $"已启动（PID {proc.Id}），未等待调试端口。", proc.Id);

        // 5) 等端口
        P($"    等待 CDP 端口 {port} 就绪（最多 {adapter.Connection.LaunchTimeoutSec} 秒）…");
        bool ready = WaitForCdp(port, adapter.Connection.LaunchTimeoutSec, out string lastError);

        if (ready)
            return new Result(true, $"启动成功，CDP 端口 {port} 已就绪。", proc.Id, CdpReady: true);

        // 端口没通：区分「进程还活着」与「进程已退出」
        bool alive = !proc.HasExited;
        string detail = alive
            ? $"进程仍在运行（PID {proc.Id}），但 {adapter.Connection.LaunchTimeoutSec} 秒内端口未就绪。\n" +
              $"可能原因：\n" +
              $"  · 游戏启动较慢，可调大 connection.launchTimeoutSec\n" +
              $"  · 启动器忽略了 --remote-debugging-port（部分游戏的 launcher 会二次转发并丢掉参数）"
            : $"进程已退出（退出码 {(proc.HasExited ? proc.ExitCode.ToString() : "?")}）。\n" +
              $"可能原因：缺少运行库、游戏自身报错、或该 exe 不是主入口。";

        return new Result(false, detail + $"\n最后一次探测错误：{lastError}", proc.Id);
    }

    /// <summary>
    /// 定位 exe 完整路径。优先级：installDir → 已运行进程路径 → 常见盘符的 Games 目录。
    /// </summary>
    private static string ResolveExe(GameAdapter adapter, Action<string> P)
    {
        // a) 适配包指定了安装目录
        if (!string.IsNullOrWhiteSpace(adapter.Connection.InstallDir))
        {
            string p = Path.Combine(adapter.Connection.InstallDir, adapter.Exe);
            if (File.Exists(p)) return p;
            P($"    警告：installDir 下没有找到 {adapter.Exe}");
        }

        // b) 适配包目录的兄弟位置（适配包自带启动器时的常见布局）
        string appRoot = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            string p = Path.Combine(appRoot, adapter.Exe);
            if (File.Exists(p)) return p;
            var parent = Directory.GetParent(appRoot);
            if (parent == null) break;
            appRoot = parent.FullName;
        }

        // c) 常见盘符的 Games 目录下按名字找（只找一层，不做全盘扫描）
        foreach (string drive in new[] { "C:", "D:", "E:", "F:" })
        {
            string games = Path.Combine(drive + "\\", "Games");
            if (!Directory.Exists(games)) continue;
            try
            {
                foreach (string dir in Directory.GetDirectories(games))
                {
                    string p = Path.Combine(dir, adapter.Exe);
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* 无权访问的目录跳过 */ }
        }

        return null;
    }

    /// <summary>轮询 CDP 端口直到就绪或超时。</summary>
    private static bool WaitForCdp(int port, int timeoutSec, out string lastError)
    {
        lastError = null;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = http.GetStringAsync($"http://127.0.0.1:{port}/json/version")
                                  .GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(json)) return true;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>
    /// 单个命令行参数加引号。UseShellExecute=true 时不能用 ArgumentList，
    /// 含空格的参数必须自己加引号，否则会被拆成多个参数。
    /// </summary>
    private static string QuoteArg(string a)
        => string.IsNullOrEmpty(a) || a.Contains(' ') || a.Contains('\t')
            ? "\"" + a.Replace("\"", "\\\"") + "\""
            : a;

    /// <summary>只检查端口是否已就绪，不启动任何东西。</summary>
    public static bool IsCdpReady(int port, out string detail)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        try
        {
            string json = http.GetStringAsync($"http://127.0.0.1:{port}/json/version").GetAwaiter().GetResult();
            detail = json.Length > 200 ? json[..200] + "..." : json;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }
}
