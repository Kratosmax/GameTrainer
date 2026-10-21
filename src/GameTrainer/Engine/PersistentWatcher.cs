using GameTrainer.Adapter;

namespace GameTrainer.Engine;

/// <summary>
/// 持久监控器：把某个语义的值「钉住」，丢失就自动补回，退出对局时自动停止。
///
/// 存在理由：有些状态在游戏里会瞬间被消耗或重置（例如护盾被打掉），
/// 用户希望「一直有盾」。轮询是最直接可靠的做法——不做注入、不钩引擎事件。
///
/// 三条硬约束：
///   1. 必须能判断「是否仍在局内」。退出对局后立即停止，不把状态带到下一局。
///   2. 轮询失败不能拖垮 UI，也不能无限重试。
///   3. 只有用户显式勾选才启动，绝不自作主张。
/// </summary>
internal sealed class PersistentWatcher : IDisposable
{
    private readonly GameAdapter _adapter;
    private readonly string _semantic;
    private readonly double _target;
    private readonly int _intervalMs;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop;

    /// <summary>每次补写或状态变化时触发（供 UI 记日志）。</summary>
    public event Action<string> Notified;

    /// <summary>监控是否仍在运行。</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>停止原因（如「退出对局」）。</summary>
    public string StopReason { get; private set; }

    public PersistentWatcher(GameAdapter adapter, string semantic, double target, int intervalMs = 1000)
    {
        _adapter = adapter;
        _semantic = semantic;
        _target = target;
        _intervalMs = Math.Max(300, intervalMs);
    }

    public void Start()
    {
        if (IsRunning) return;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_intervalMs, ct);

                // 约束 1：先判断是否还在局内
                if (!IsInGame(ct))
                {
                    StopReason = "已退出对局";
                    Notified?.Invoke("检测到已退出对局，持久监控自动停止。");
                    return;
                }

                // 读当前值
                var reads = AdapterRunner.Read(_adapter, new[] { _semantic });
                var r = reads.FirstOrDefault();
                if (r == null || !r.Ok)
                {
                    // 约束 2：失败计数，连续失败太多就退出，不无限重试
                    if (++consecutiveFailures >= 10)
                    {
                        StopReason = "连续 10 次读取失败";
                        Notified?.Invoke("持久监控停止：连续读取失败（游戏可能已关闭）。");
                        return;
                    }
                    continue;
                }
                consecutiveFailures = 0;

                // 值丢失就补回
                if (!double.TryParse(r.Value, out double cur) || Math.Abs(cur - _target) > 1e-9)
                {
                    var w = AdapterRunner.Write(_adapter, _semantic, _target.ToString("0.##"), dryRun: false);
                    if (w.Ok)
                        Notified?.Invoke($"持久补写：{r.Value} → {w.After}");
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (++consecutiveFailures >= 10)
                {
                    StopReason = "连续异常：" + ex.Message;
                    Notified?.Invoke("持久监控停止：" + ex.Message);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 判断是否仍在局内。
    ///
    /// 优先用适配包配置的 inGameLayouts 白名单；未配置时退化为
    /// 「目标对象仍有实例」。两者都不依赖时间，避免误判。
    /// </summary>
    /// <remarks>内部全是同步 IO（CDP 是阻塞调用），因此不用 async，避免假异步。</remarks>
    private bool IsInGame(CancellationToken ct)
    {
        var layouts = _adapter.EngineOptions.InGameLayouts;
        if (layouts is { Count: > 0 })
        {
            try
            {
                string script = $$"""
(() => {
  try {
    const rt = {{_adapter.EngineOptions.RuntimePath}};
    if (!rt) return JSON.stringify({ layout: null });
    const lm = rt.GetLayoutManager();
    const cur = lm && lm.GetMainRunningLayout ? lm.GetMainRunningLayout() : null;
    return JSON.stringify({ layout: cur ? cur.GetName() : null });
  } catch (e) { return JSON.stringify({ layout: null, error: String(e) }); }
})()
""";
                using var cdp = CdpClient.Connect(_adapter.Connection.DebugPort, _adapter.Connection.TitleFilter);
                string raw = cdp.Evaluate(script, timeoutMs: 5000);
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("layout", out var l) &&
                    l.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string name = l.GetString();
                    return layouts.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // 退化：目标对象还有实例就认为在局内
        var b = _adapter.FindBinding(_semantic);
        if (b == null || string.IsNullOrWhiteSpace(b.ObjectName)) return true;
        var reads = AdapterRunner.Read(_adapter, new[] { _semantic });
        var r = reads.FirstOrDefault();
        return r is { Ok: true };
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
