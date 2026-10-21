using System.Diagnostics;
using System.Globalization;
using GameTrainer.Core.Updates;

namespace GameTrainer.Updater;

/// <summary>
/// 独立更新器。
///
/// 为什么必须是独立进程：Windows 上运行中的 exe 不能被替换。
/// 主程序点了「立即更新」后只能把自己要替换的文件交给别人做，
/// 然后退出——由这个进程等它退出、替换文件、再重启。
///
/// 安全边界：不信任调用方传来的任何东西。
/// 清单自己用内置公钥重验一遍，通道从目标目录的 channel.txt 读，
/// 而不是从命令行参数接受——否则等于把验签交给调用方。
/// </summary>
internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameTrainer",
        "update.log");

    private static int Main(string[] args)
    {
        try
        {
            var opt = ParseOptions(args);
            string packagePath = Require(opt, "package");
            string manifestPath = Require(opt, "manifest");
            string targetDir = Require(opt, "target");
            int pid = int.Parse(Require(opt, "pid"), CultureInfo.InvariantCulture);

            WriteLog("=== 更新器启动 ===");
            WriteLog($"目标目录 {targetDir}");

            if (!File.Exists(packagePath)) return Fail($"找不到更新包 {packagePath}");
            if (!File.Exists(manifestPath)) return Fail($"找不到清单 {manifestPath}");
            if (!Directory.Exists(targetDir)) return Fail($"目标目录不存在 {targetDir}");

            WriteLog($"等待主程序退出（PID {pid}）");
            if (!WaitForExit(pid, TimeSpan.FromSeconds(60)))
                return Fail("主程序在 60 秒内未退出，已放弃更新。请关闭 GameTrainer 后重试。");

            WriteLog("主程序已退出");

            // 通道从安装目录读，不从参数接受：参数是调用方给的，不可信
            string channel = ReadChannel(targetDir);
            WriteLog($"通道 {channel}");

            string json = File.ReadAllText(manifestPath);
            UpdateInfo update = UpdateManifestCodec.ParseAndVerify(json, Trust.PublicKeyPem, channel);
            WriteLog($"清单验签通过：{update.Version} {update.Channel}");

            // 包大小与哈希再核一次：文件可能在等待期间被改动
            var fi = new FileInfo(packagePath);
            if (fi.Length != update.Size)
                return Fail($"包大小不符：清单 {update.Size}，实际 {fi.Length}");

            string actualSha = UpdateInstaller.ComputeSha256(packagePath);
            if (!string.Equals(actualSha, update.Sha256, StringComparison.OrdinalIgnoreCase))
                return Fail($"包哈希不符：清单 {update.Sha256}，实际 {actualSha}");

            WriteLog("哈希校验通过，开始安装");
            UpdateInstaller.Install(packagePath, targetDir, update);
            WriteLog("安装完成");

            // 清理下载的临时包
            try { File.Delete(packagePath); } catch { }

            string exe = Path.Combine(targetDir, "GameTrainer.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = targetDir
                });
                WriteLog("已重新启动 GameTrainer");
            }
            else
            {
                WriteLog("[警告] 安装后找不到 GameTrainer.exe，未自动重启");
            }

            WriteLog("=== 更新成功 ===");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLog($"[失败] {ex}");
            return 1;
        }
    }

    /// <summary>通道从安装目录的 channel.txt 读，缺失按 lite 处理。</summary>
    private static string ReadChannel(string targetDir)
    {
        try
        {
            string f = Path.Combine(targetDir, "channel.txt");
            if (File.Exists(f))
            {
                string v = File.ReadAllText(f).Trim().ToLowerInvariant();
                if (v is "lite" or "full") return v;
            }
        }
        catch { }
        return "lite";
    }

    /// <summary>等进程退出。进程已不存在时立即返回 true。</summary>
    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return true; }   // 已经退出了

        using (p)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (p.HasExited) return true;
                Thread.Sleep(200);
            }
            return false;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"参数格式无效：{args[i]}");
            d[args[i][2..]] = args[i + 1];
        }
        return d;
    }

    private static string Require(IReadOnlyDictionary<string, string> opt, string name)
        => opt.TryGetValue(name, out string v) && !string.IsNullOrWhiteSpace(v)
            ? v : throw new ArgumentException($"缺少参数 --{name}");

    private static int Fail(string msg)
    {
        WriteLog("[失败] " + msg);
        Console.Error.WriteLine(msg);
        return 1;
    }

    private static void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // 超过 256KB 轮转一份，避免长期积累
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 256 * 1024)
                File.Move(LogPath, LogPath + ".old", overwrite: true);

            File.AppendAllText(LogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
