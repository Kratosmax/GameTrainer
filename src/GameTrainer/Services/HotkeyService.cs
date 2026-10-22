using GameTrainer.Adapter;
using GameTrainer.Core;

namespace GameTrainer.Services;

/// <summary>
/// 修改项热键服务。
///
/// 从 GuiForm 抽出来（原 InitHotkeys / RebuildHotkeys / TriggerEntryHotkey 三个方法）。
/// 职责：按偏好注册全局热键、热键触发时回调业务动作。
///
/// 不直接引用窗体：触发时通过 TriggerRequested 事件把语义抛出去，
/// 由主窗体决定怎么取值、怎么写入。这样热键服务可以独立测试。
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly Prefs _prefs;
    private HotkeyManager _manager;
    private readonly List<string> _registered = new();
    private IntPtr _windowHandle;

    /// <summary>热键触发：参数是要修改的语义。</summary>
    public event Action<string> TriggerRequested;

    /// <summary>注册结果变化：参数是成功注册的数量。</summary>
    public event Action<int> CountChanged;

    /// <summary>需要输出日志。</summary>
    public event Action<string> Logged;

    public HotkeyService(Prefs prefs)
    {
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
    }

    /// <summary>已注册的热键数量。</summary>
    public int Count { get; private set; }

    /// <summary>
    /// 绑定窗口句柄。必须在窗口 Handle 创建后调用。
    /// 重复调用会重建管理器（窗口重建时会走这条路径）。
    /// </summary>
    public void Attach(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        _manager?.Dispose();
        _windowHandle = handle;
        _manager = new HotkeyManager(handle);
    }

    /// <summary>
    /// 按当前适配包与偏好重建全部热键。
    ///
    /// 每次切换游戏、修改偏好后都要调用：
    /// 热键是「适配包 + 偏好」的产物，任一变化都会让旧注册失效。
    /// </summary>
    public void Rebuild(GameAdapter adapter)
    {
        if (_manager == null) return;

        if (adapter == null)
        {
            UnregisterAll();
            return;
        }

        var items = new List<(string Key, string Spec, Action Action)>();

        foreach (var b in adapter.Bindings)
        {
            var ep = _prefs.ForEntry(adapter.Id, b.Semantic);
            if (string.IsNullOrWhiteSpace(ep.Hotkey)) continue;

            string semantic = b.Semantic;
            items.Add((
                Prefs.EntryKey(adapter.Id, semantic),
                ep.Hotkey,
                () => TriggerRequested?.Invoke(semantic)));
        }

        if (items.Count == 0)
        {
            UnregisterAll();
            return;
        }

        _registered.Clear();
        _registered.AddRange(items.Select(x => x.Key));

        if (_manager.RegisterAll(items, out List<string> errors))
        {
            Count = items.Count;
            CountChanged?.Invoke(Count);
            Logged?.Invoke($"[热键] 已注册 {items.Count} 个修改项快捷键");
        }
        else
        {
            Count = 0;
            CountChanged?.Invoke(0);
            foreach (string e in errors) Logged?.Invoke("[警告] 热键：" + e);
        }
    }

    /// <summary>清空全部热键。</summary>
    public void UnregisterAll()
    {
        if (_manager != null)
        {
            // HotkeyManager 没有 UnregisterAll，按登记过的 key 逐个注销
            foreach (string key in _registered) _manager.Unregister(key);
        }
        _registered.Clear();

        Count = 0;
        CountChanged?.Invoke(0);
    }

    /// <summary>
    /// 把窗口消息交给热键管理器。
    /// 主窗体的 WndProc 里调用。
    /// </summary>
    public void HandleMessage(int msg, IntPtr wParam)
    {
        _manager?.HandleMessage(msg, wParam);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        _manager = null;
        Count = 0;
    }
}
