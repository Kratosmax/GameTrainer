using System.IO.Compression;
using System.Text;
using GameTrainer.Core.Updates;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 更新安装的事务性与安全性。
///
/// 两类失败都要测：
///   1. 路径穿越（zip slip）：包里的 ../ 条目不能写到目标目录之外
///   2. 事务回滚：替换中途失败时，必须回到安装前状态，不能留半截
/// </summary>
public class UpdateInstallerTests
{
    private static UpdateInfo Info(long size, string sha) => new(
        new Version(1, 0, 1), "lite",
        new Uri("https://github.com/Kratosmax/GameTrainer/releases/download/v1.0.1/x.zip"),
        size, sha, "", "{}");

    private static string MakeZip(string dir, Action<ZipArchive> fill)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"pkg-{Guid.NewGuid():N}.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        fill(zip);
        return path;
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var s = e.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void 正常安装_文件被替换且新增文件出现()
    {
        string root = Path.Combine(Path.GetTempPath(), "gt-inst-" + Guid.NewGuid().ToString("N")[..8]);
        string install = Path.Combine(root, "app");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "old.txt"), "旧内容");
        File.WriteAllText(Path.Combine(install, "keep.txt"), "保持不变");

        try
        {
            string zip = MakeZip(Path.Combine(root, "pkgs"), z =>
            {
                AddEntry(z, "old.txt", "新内容");
                AddEntry(z, "new.txt", "新增");
            });

            UpdateInstaller.Install(zip, install, Info(new FileInfo(zip).Length, "X"));

            Assert.Equal("新内容", File.ReadAllText(Path.Combine(install, "old.txt")));
            Assert.Equal("新增", File.ReadAllText(Path.Combine(install, "new.txt")));
            // 包里没有的文件不该被删
            Assert.Equal("保持不变", File.ReadAllText(Path.Combine(install, "keep.txt")));
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public void 路径穿越条目_被拒绝且不落盘()
    {
        string root = Path.Combine(Path.GetTempPath(), "gt-zipslip-" + Guid.NewGuid().ToString("N")[..8]);
        string install = Path.Combine(root, "app");
        Directory.CreateDirectory(install);

        try
        {
            // 这条会试图写到 install 的上一级
            string zip = MakeZip(Path.Combine(root, "pkgs"), z =>
            {
                AddEntry(z, "../escaped.txt", "逃逸");
            });

            Assert.ThrowsAny<Exception>(() =>
                UpdateInstaller.Install(zip, install, Info(new FileInfo(zip).Length, "X")));

            string escaped = Path.Combine(root, "escaped.txt");
            Assert.False(File.Exists(escaped), "路径穿越成功写到了目标目录之外");
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public void 绝对路径条目_被拒绝()
    {
        string root = Path.Combine(Path.GetTempPath(), "gt-abs-" + Guid.NewGuid().ToString("N")[..8]);
        string install = Path.Combine(root, "app");
        Directory.CreateDirectory(install);

        try
        {
            string zip = MakeZip(Path.Combine(root, "pkgs"), z =>
            {
                AddEntry(z, @"C:\Windows\Temp\gt-evil.txt", "x");
            });

            Assert.ThrowsAny<Exception>(() =>
                UpdateInstaller.Install(zip, install, Info(new FileInfo(zip).Length, "X")));
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public void 安装后不残留事务目录()
    {
        string root = Path.Combine(Path.GetTempPath(), "gt-tx-" + Guid.NewGuid().ToString("N")[..8]);
        string install = Path.Combine(root, "app");
        Directory.CreateDirectory(install);

        try
        {
            string zip = MakeZip(Path.Combine(root, "pkgs"), z => AddEntry(z, "a.txt", "1"));
            UpdateInstaller.Install(zip, install, Info(new FileInfo(zip).Length, "X"));

            // 成功路径也要清理事务目录，否则每次更新留一堆垃圾
            Assert.False(Directory.Exists(Path.Combine(install, ".gt-update")),
                "事务目录未清理");
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public void 哈希计算与文件内容一致()
    {
        string root = Path.Combine(Path.GetTempPath(), "gt-hash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string f = Path.Combine(root, "a.bin");
            File.WriteAllBytes(f, new byte[] { 1, 2, 3, 4, 5 });

            string actual = UpdateInstaller.ComputeSha256(f);
            using var sha = System.Security.Cryptography.SHA256.Create();
            string expect = Convert.ToHexString(
                sha.ComputeHash(new byte[] { 1, 2, 3, 4, 5 }));

            Assert.Equal(expect, actual);
        }
        finally { TryDeleteDir(root); }
    }

    [Fact]
    public void 安装目录不存在_抛异常()
    {
        string root = Path.Combine(Path.GetTempPath(), "gt-nodir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string zip = MakeZip(Path.Combine(root, "pkgs"), z => AddEntry(z, "a.txt", "1"));
            Assert.ThrowsAny<DirectoryNotFoundException>(() =>
                UpdateInstaller.Install(zip, Path.Combine(root, "not-exist"), Info(1, "X")));
        }
        finally { TryDeleteDir(root); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
