using GameTrainer.Core;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 审计日志的并发安全。
///
/// 曾经是实例级锁：MemoryBackend 每次操作都 new 一个 AuditLog（同一路径），
/// 各实例的锁互相独立，等于完全没有互斥。并发写同一文件会抛 IOException
/// 或写出交错的半行 JSON，导致日志不可解析。
/// </summary>
public class AuditLogConcurrencyTests
{
    [Fact]
    public void 多实例并发写入_不抛异常且每行完整()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gt-audit-test-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "audit.log");
        Directory.CreateDirectory(dir);

        try
        {
            const int writers = 8;
            const int perWriter = 50;
            var errors = new List<Exception>();

            // 每个线程都 new 自己的 AuditLog 实例——复现"多实例"这个关键前提
            Parallel.For(0, writers, i =>
            {
                try
                {
                    var log = new AuditLog(path);
                    for (int k = 0; k < perWriter; k++)
                        log.Record("concurrent", new { writer = i, seq = k });
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            });

            Assert.Empty(errors);

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(writers * perWriter, lines.Length);

            // 每一行都必须是完整可解析的 JSON
            foreach (string line in lines)
            {
                Assert.False(string.IsNullOrWhiteSpace(line));
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                Assert.Equal("concurrent", doc.RootElement.GetProperty("action").GetString());
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 并发写入_行数等于调用次数()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gt-audit-test-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(dir, "audit.log");
        Directory.CreateDirectory(dir);

        try
        {
            var log1 = new AuditLog(path);
            var log2 = new AuditLog(path);
            var log3 = new AuditLog(path);

            Parallel.Invoke(
                () => { for (int i = 0; i < 100; i++) log1.Record("a", new { i }); },
                () => { for (int i = 0; i < 100; i++) log2.Record("b", new { i }); },
                () => { for (int i = 0; i < 100; i++) log3.Record("c", new { i }); });

            Assert.Equal(300, File.ReadAllLines(path).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
