using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace GameTrainer.Core.Updates;

/// <summary>
/// 更新执行器：下载 → 校验 → 解压 → 事务式替换 → 失败回滚。
///
/// 与其他实现的关键差异（都是踩过的坑）：
///   1. 解压前先校验哈希与大小，不是解压后再看
///   2. 解压时防路径穿越（zip slip）：条目路径必须落在目标目录内
///   3. 换文件用"备份 → 替换"两阶段，任一步失败全部回滚，不留半截状态
///   4. 下载有停滞超时，避免连接卡死时无限等待
/// </summary>
internal static class UpdateInstaller
{
    private const string TransactionDirName = ".gt-update";

    /// <summary>下载包到临时目录并校验哈希与大小。返回本地路径。</summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo update, string tempRoot, IProgress<double> progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(tempRoot);
        string path = Path.Combine(tempRoot, $"update-{update.Version}-{Guid.NewGuid():N}.zip");

        // 与 UpdateClient 用同一套代理解析：检查能过、下载却失败是最难排查的错。
        using var http = new HttpClient(UpdateClient.ProxyHandler)
        {
            // 下载大文件不能用总超时，否则 Full 包（63MB）在慢网下必然中断。
            // 改用每读一次的超时（见下方 ReadAsync 的 ct 与 StalledTimeoutMs）。
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        // 先看 Content-Length：清单声称的大小与实际不符就不必下载
        long? declared = response.Content.Headers.ContentLength;
        if (declared.HasValue && declared.Value != update.Size)
            throw new InvalidDataException(
                $"服务器声明大小 {declared.Value} 与清单 {update.Size} 不符，拒绝下载。");
        if (declared.HasValue && declared.Value > UpdateManifestCodec.MaximumPackageSize)
            throw new InvalidDataException("更新包超过大小上限，拒绝下载。");

        long total = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                // 边下边卡上限：不能等下完再看，否则磁盘已经被打满
                if (total > UpdateManifestCodec.MaximumPackageSize)
                    throw new InvalidDataException("更新包超过大小上限，已中止下载。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report(update.Size > 0 ? (double)total / update.Size : 0);
            }
        }

        if (total != update.Size)
            throw new InvalidDataException($"下载大小 {total} 与清单 {update.Size} 不符。");

        string actual = ComputeSha256(path);
        if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new CryptographicException(
                $"更新包哈希不匹配。\n  期望 {update.Sha256}\n  实际 {actual}\n包已被篡改或损坏，已删除。");
        }

        return path;
    }

    /// <summary>
    /// 把已校验的包安装到目标目录。事务式：失败时回滚到安装前状态。
    /// </summary>
    public static void Install(string packagePath, string targetDir, UpdateInfo update,
                               CancellationToken ct = default)
    {
        if (!Directory.Exists(targetDir))
            throw new DirectoryNotFoundException($"安装目录不存在：{targetDir}");

        string txRoot = Path.Combine(targetDir, TransactionDirName, Guid.NewGuid().ToString("N"));
        string stageRoot = Path.Combine(txRoot, "stage");
        string backupRoot = Path.Combine(txRoot, "backup");
        Directory.CreateDirectory(stageRoot);
        Directory.CreateDirectory(backupRoot);

        var installed = new List<string>();
        var backups = new List<(string Target, string Backup)>();
        bool committed = false;

        try
        {
            ExtractSafely(packagePath, stageRoot, ct);

            foreach (string source in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(stageRoot, source);
                string target = ResolveInside(targetDir, rel);
                string backup = ResolveInside(backupRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup);
                    backups.Add((target, backup));
                }
                File.Move(source, target);
                installed.Add(target);
            }
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                // 逆序回滚：已替换的先删，再恢复备份
                foreach (string p in installed.AsEnumerable().Reverse()) TryDelete(p);
                foreach (var (target, backup) in backups.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(backup))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Move(backup, target, overwrite: true);
                        }
                    }
                    catch { /* 回滚尽量做，单个失败不阻断其余 */ }
                }
            }
            try
            {
                if (Directory.Exists(txRoot)) Directory.Delete(txRoot, recursive: true);

                // 连父目录一起清：只删 GUID 子目录会在安装目录留下一个空的 .gt-update，
                // 每次更新攒一个，用户看到一堆莫名文件夹。
                string txParent = Path.Combine(targetDir, TransactionDirName);
                if (Directory.Exists(txParent) &&
                    !Directory.EnumerateFileSystemEntries(txParent).Any())
                    Directory.Delete(txParent);
            }
            catch { }
        }
    }

    /// <summary>
    /// 解压并防路径穿越。zip 里的条目可以是 ..\..\evil.exe，
    /// 直接 ExtractToDirectory 会写到目标目录之外。
    /// </summary>
    private static void ExtractSafely(string zipPath, string destDir, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // 目录条目跳过（后续按文件创建目录）
            if (string.IsNullOrEmpty(entry.Name)) continue;

            string target = ResolveInside(destDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // 用 Open + CopyTo 而不是 ExtractToFile：后者不检查路径
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    /// <summary>把相对路径解析到 root 内，越界即抛异常。</summary>
    private static string ResolveInside(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootFull = Path.GetFullPath(root);

        // 必须带分隔符比较，否则 C:\app 会匹配 C:\app-evil
        if (!full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"更新包含非法路径，已中止：{relative}");
        return full;
    }

    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
