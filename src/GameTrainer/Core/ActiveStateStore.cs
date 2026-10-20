using System.Text.Json;

namespace GameTrainer.Core;

/// <summary>
/// 激活状态持久化。
///
/// 目的：修改器崩溃或被强杀后，内存里的修改栈会丢失，但游戏进程中仍残留已改的值。
/// 因此启用/回滚时同步落盘"当前有哪些 patch 处于启用状态"。
/// 下次启动读取该文件，若发现残留则提示用户恢复——而不是假装什么都没发生。
/// </summary>
internal sealed class ActiveStateStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public ActiveStateStore(string skillRoot)
        => _path = Path.Combine(skillRoot, "snapshots", "active-state.json");

    public sealed class Entry
    {
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }
        public string Exe { get; set; }
        public string PatchId { get; set; }
        public string PatchLabel { get; set; }
        public int Pid { get; set; }
        public long ProcessCreateTime { get; set; }
        public string ActivatedAt { get; set; }
        /// <summary>该 patch 依赖的写入定义快照，供下次按 profile 语义补救。</summary>
        public bool CanRestoreByProfile { get; set; }
    }

    public sealed class State
    {
        public List<Entry> Active { get; set; } = new();
    }

    // 所有写操作都走同一把锁；Load/Save 内部不再加锁，避免 lock 重入。
    private State LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path)) return new State();
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            return s ?? new State();
        }
        catch { return new State(); }
    }

    private void SaveUnlocked(State s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                          new System.Text.UTF8Encoding(false));
    }

    /// <summary>读取当前状态（线程安全）。</summary>
    public State Load()
    {
        lock (_lock) return LoadUnlocked();
    }

    public void MarkActive(Entry e)
    {
        lock (_lock)
        {
            var s = LoadUnlocked();
            s.Active.RemoveAll(x => x.ProfileId == e.ProfileId && x.PatchId == e.PatchId);
            s.Active.Add(e);
            SaveUnlocked(s);
        }
    }

    public void MarkInactive(string profileId, string patchId)
    {
        lock (_lock)
        {
            var s = LoadUnlocked();
            s.Active.RemoveAll(x => x.ProfileId == profileId && x.PatchId == patchId);
            SaveUnlocked(s);
        }
    }

    public void ClearAll()
    {
        lock (_lock) SaveUnlocked(new State());
    }

    /// <summary>
    /// 检测上次是否留下未回滚的修改。
    /// 只有当记录里的进程身份仍然存活时才认为"可能仍有残留"。
    /// </summary>
    public List<(Entry Entry, bool ProcessStillAlive)> DetectResidue()
    {
        var result = new List<(Entry, bool)>();
        foreach (Entry e in Load().Active)
        {
            bool alive = false;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(e.Pid);
                alive = !p.HasExited;
            }
            catch { alive = false; }
            result.Add((e, alive));
        }
        return result;
    }
}
