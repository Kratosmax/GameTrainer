using GameTrainer.Adapter;
using GameTrainer.Core;
using GameTrainer.Core.Updates;
using GameTrainer.Engine;
using GameTrainer.Profiles;

namespace GameTrainer.Services;

/// <summary>
/// 游戏会话 —— 唯一状态源。
///
/// 为什么需要它：
/// 旧 GuiForm 把「当前适配包、卡片表、写入中标记、监控器、日志」全塞在自己身上，
/// 50 个方法互相调用，任何一个改动都要穿过 2000 行。
///
/// 这里把业务状态与业务操作收拢，界面只订阅事件、只读属性：
///   - 状态只有一个来源，不可能出现「顶栏说已连接、卡片说读取失败」
///   - 页面之间不互相引用，加一个页面不需要改其它页面
///   - 读写、监控、日志都能脱离窗体单独测试
///
/// 线程约定：所有事件都在调用方的线程上触发。
/// UI 调用时必须保证在 UI 线程（WinForms 控件不允许跨线程访问）。
/// </summary>
internal sealed class GameSession : IDisposable
{
    private readonly string _skillRoot;
    private readonly Dictionary<string, PersistentWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _writing = new(StringComparer.OrdinalIgnoreCase);

    public GameSession(string skillRoot)
    {
        _skillRoot = skillRoot;
        Prefs = Prefs.Load();
    }

    // ==================== 状态 ====================

    /// <summary>用户偏好（快捷键、代理、折叠）。</summary>
    public Prefs Prefs { get; }

    /// <summary>当前选中的适配包。null 表示在首页或未选择。</summary>
    public GameAdapter Adapter { get; private set; }

    /// <summary>兼容旧 profile 模式。适配包模式下为 null。</summary>
    public GameProfile Profile { get; private set; }

    /// <summary>当前游戏运行状态。界面唯一依据。</summary>
    public GameState State { get; private set; } = GameState.Idle;

    /// <summary>最近一次读取的原始结果，供界面渲染卡片。</summary>
    public IReadOnlyDictionary<string, AdapterRunner.ReadResult> LastRead => _lastRead;

    private readonly Dictionary<string, AdapterRunner.ReadResult> _lastRead =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否正在刷新（防止并发）。</summary>
    public bool Refreshing { get; private set; }

    /// <summary>是否有待查看的更新。</summary>
    public UpdateInfo PendingUpdate { get; private set; }

    // ==================== 事件 ====================

    /// <summary>状态变化。界面据此更新状态胶囊与状态栏。</summary>
    public event Action<GameState> StateChanged;

    /// <summary>切换了适配包（含首次选中）。界面据此重建卡片。</summary>
    public event Action<GameAdapter> AdapterChanged;

    /// <summary>读取完成。参数是完整结果集。</summary>
    public event Action<IReadOnlyDictionary<string, AdapterRunner.ReadResult>> ReadCompleted;

    /// <summary>需要输出一行日志。</summary>
    public event Action<string> Logged;

    /// <summary>发现新版本。</summary>
    public event Action<UpdateInfo> UpdateFound;

    // ==================== 生命周期 ====================

    public void Log(string message) => Logged?.Invoke(message);

    private void SetState(GameState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    // ==================== 选择游戏 ====================

    /// <summary>
    /// 选中一个适配包。会清掉上一個游戏的定位缓存。
    ///
    /// 缓存必须清：里面存的是上个游戏的 Userdata 地址，
    /// 复用它读到的是另一个进程的随机内存，表现为「切回来就加载失败」。
    /// </summary>
    public void Select(GameAdapter adapter)
    {
        if (adapter == null) return;

        StopAllWatchers("切换游戏");
        SaveLocator.Invalidate();

        Adapter = adapter;
        Profile = null;
        _lastRead.Clear();

        AdapterChanged?.Invoke(adapter);
        Log($"已选择适配包：{adapter.Id}（{adapter.Bindings.Count} 个数值）");
    }

    /// <summary>清除当前选择（回首页时）。</summary>
    public void Clear()
    {
        StopAllWatchers("离开游戏页");
        SaveLocator.Invalidate();
        Adapter = null;
        Profile = null;
        _lastRead.Clear();
        SetState(GameState.Idle);
    }

    // ==================== 读取 ====================

    /// <summary>
    /// 读取全部语义。失败时自动重试一次（清缓存后重扫）。
    ///
    /// 为什么自动重试：切换游戏、读档、游戏重启都会让上一次的定位缓存失效。
    /// 清缓存重扫通常能直接恢复，不该让用户自己去点刷新。
    /// </summary>
    public async Task RefreshAsync()
    {
        if (Adapter == null || Refreshing) return;

        Refreshing = true;
        try
        {
            var adapter = Adapter;
            SaveLocator.Invalidate();

            Dictionary<string, AdapterRunner.ReadResult> results;
            try
            {
                results = await Task.Run(() => AdapterRunner.ReadAll(adapter));
            }
            catch (Exception ex)
            {
                Log("[错误] 读取失败：" + ex.Message.Replace("\n", " "));
                SetState(GameState.Failed);
                return;
            }

            // 全失败 → 清缓存重试一次
            if (results.Count > 0 && results.Values.All(x => !x.Ok))
            {
                Log("[重试] 全部读取失败，清定位缓存后重试一次…");
                SaveLocator.Invalidate();
                results = await Task.Run(() => AdapterRunner.ReadAll(adapter));
            }

            _lastRead.Clear();
            foreach (var kv in results) _lastRead[kv.Key] = kv.Value;

            // 状态判定：全部失败时区分「游戏没开」与「真的读不到」
            int ok = results.Values.Count(x => x.Ok);
            if (ok > 0)
            {
                SetState(GameState.Running);
            }
            else
            {
                bool notRunning = results.Values.Any(x =>
                    x.Error != null && x.Error.Contains("找不到进程"));
                SetState(notRunning ? GameState.NotRunning : GameState.Failed);
            }

            ReadCompleted?.Invoke(_lastRead);
        }
        finally
        {
            Refreshing = false;
        }
    }

    // ==================== 写入 ====================

    /// <summary>写单值语义。同一语义不允许并发写。</summary>
    public async Task<AdapterRunner.WriteResult> WriteAsync(string semantic, string raw)
    {
        if (Adapter == null) return null;
        if (!_writing.Add(semantic))
        {
            Log($"[提示] {semantic} 正在写入，请稍候");
            return null;
        }

        try
        {
            var adapter = Adapter;
            var result = await Task.Run(() => AdapterRunner.Write(adapter, semantic, raw, dryRun: false));
            if (result != null && result.Ok)
                Log($"{Semantics.Display(semantic)}: {result.Before} → {result.After}");
            else if (result != null)
                Log("[错误] " + result.Error);
            return result;
        }
        finally
        {
            _writing.Remove(semantic);
        }
    }

    /// <summary>写集合里的单个条目。</summary>
    public async Task<AdapterRunner.WriteResult> WriteEntryAsync(string semantic, int entryId, int value)
    {
        if (Adapter == null) return null;
        string key = $"{semantic}:{entryId}";
        if (!_writing.Add(key)) return null;

        try
        {
            var adapter = Adapter;
            var result = await Task.Run(() =>
                AdapterRunner.Write(adapter, semantic, $"{entryId}={value}", dryRun: false));

            if (result != null && result.Ok)
                Log($"{Semantics.Display(semantic)}：{result.Before} → {result.After}");
            else if (result != null)
                Log("[错误] " + result.Error);
            return result;
        }
        finally
        {
            _writing.Remove(key);
        }
    }

    // ==================== 持久监控 ====================

    /// <summary>启动某语义的持久补写（保持 target 值不被游戏改回）。</summary>
    public void StartWatcher(string semantic, double target)
    {
        if (Adapter == null) return;
        StopWatcher(semantic);

        var w = new PersistentWatcher(Adapter, semantic, target);
        _watchers[semantic] = w;
        w.Start();
    }

    public void StopWatcher(string semantic)
    {
        if (_watchers.TryGetValue(semantic, out var w))
        {
            w.Dispose();
            _watchers.Remove(semantic);
        }
    }

    public void StopAllWatchers(string reason)
    {
        foreach (var w in _watchers.Values) w.Dispose();
        if (_watchers.Count > 0) Log($"[持久] {reason}，已停止全部监控");
        _watchers.Clear();
    }

    public bool IsWatching(string semantic) => _watchers.ContainsKey(semantic);

    // ==================== 更新 ====================

    public void SetPendingUpdate(UpdateInfo info)
    {
        PendingUpdate = info;
        UpdateFound?.Invoke(info);
    }

    public void ClearPendingUpdate() => PendingUpdate = null;

    // ==================== 释放 ====================

    public void Dispose()
    {
        StopAllWatchers("退出程序");
        SaveLocator.Invalidate();
    }
}

/// <summary>
/// 游戏运行状态。界面唯一依据，替代旧代码里两个各自更新的徽章。
/// </summary>
internal enum GameState
{
    /// <summary>未选择游戏（首页）。</summary>
    Idle,
    /// <summary>已选择，尚未读取。</summary>
    Unknown,
    /// <summary>游戏进程未运行。</summary>
    NotRunning,
    /// <summary>读取成功，可以修改。</summary>
    Running,
    /// <summary>读取失败（进程在但读不到，或其它错误）。</summary>
    Failed
}
