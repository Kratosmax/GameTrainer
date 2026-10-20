using System.Text;
using System.Text.Json;

namespace GameTrainer.Core;

/// <summary>
/// 审计日志。每一次写入、回滚、备份都落盘一行 JSON，可追溯、可逆序回滚。
/// 只追加，不覆盖。
/// </summary>
internal sealed class AuditLog
{
    private readonly string _path;
    private readonly object _lock = new();

    public AuditLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string Path_ => _path;

    public void Record(string action, object detail)
    {
        var entry = new
        {
            ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            action,
            detail
        };
        string line = JsonSerializer.Serialize(entry);
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public void Write(string action, string target, string field, string before, string after, int bytes)
        => Record(action, new { target, field, before, after, bytes, pid = Environment.ProcessId });
}

/// <summary>一次写入的可回滚记录：足够把内存改回原状。</summary>
internal sealed class WriteRecord
{
    public string ProfileId { get; init; }
    public string PatchId { get; init; }
    public string Target { get; init; }
    public long Address { get; init; }
    public byte[] Before { get; init; }
    public byte[] After { get; init; }
    public string ModuleName { get; init; }
    public DateTime At { get; init; } = DateTime.Now;
}

internal sealed class RollbackStack
{
    private readonly List<WriteRecord> _stack = new();
    public IReadOnlyList<WriteRecord> Entries => _stack;

    public void Push(WriteRecord r) => _stack.Add(r);

    /// <summary>逆序弹出全部记录，返回需要写回的原值。清空栈。</summary>
    public List<WriteRecord> Drain()
    {
        var copy = _stack.AsEnumerable().Reverse().ToList();
        _stack.Clear();
        return copy;
    }

    public void Clear() => _stack.Clear();
}
