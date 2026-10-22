namespace GameTrainer.UI;

/// <summary>
/// 全局动画驱动器。
///
/// 为什么不是每个控件各开一个 Timer：
/// WinForms 的 Timer 精度约 15ms，多个 Timer 各自触发会造成
/// 帧时间不均匀——表现出来就是"有的动画顺、有的顿"。
/// iOS 的流畅来自统一渲染循环，这里用一个 16ms 的定时器驱动全部动画，
/// 每帧把 t∈[0,1] 分发给所有活跃动画，帧率稳定。
///
/// 性能约束：
///   - 没有活跃动画时定时器停掉，不空转
///   - 每个动画只在自己那一帧 Invalidate 需要的区域（由调用方控制）
///   - 同一目标的重复动画会替换旧的，不会堆叠
/// </summary>
internal static class Animator
{
    /// <summary>约 60fps。WinForms Timer 实际精度 15~16ms，正好接近。</summary>
    private const int FrameMs = 16;

    private sealed class Track
    {
        public object Owner;
        public string Key;
        public float Duration;
        public float Elapsed;
        public Func<float, bool> OnFrame;   // 返回 false 表示提前结束
    }

    private static readonly List<Track> _tracks = new();
    private static readonly System.Windows.Forms.Timer _timer = new() { Interval = FrameMs };
    private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private static long _lastTickMs;

    static Animator()
    {
        _timer.Tick += OnTick;
    }

    /// <summary>当前是否有动画在跑（供调试与性能观测）。</summary>
    public static int ActiveCount => _tracks.Count;

    /// <summary>
    /// 启动一个动画。
    /// </summary>
    /// <param name="owner">动画宿主控件，用于去重与生命周期判断。</param>
    /// <param name="key">同一 owner 下的唯一标识，同名动画会替换旧的。</param>
    /// <param name="durationMs">时长（毫秒）。iOS 常用 200~350ms。</param>
    /// <param name="onFrame">
    /// 每帧回调，参数是已缓动的进度 0..1。
    /// 返回 false 可提前终止（例如控件已被释放）。
    /// </param>
    public static void Run(Control owner, string key, float durationMs, Func<float, bool> onFrame)
    {
        if (owner == null || owner.IsDisposed || onFrame == null) return;

        // 同目标同 key 的旧动画直接丢弃，避免叠加
        _tracks.RemoveAll(t => ReferenceEquals(t.Owner, owner) && t.Key == key);

        _tracks.Add(new Track
        {
            Owner = owner,
            Key = key,
            Duration = Math.Max(1f, durationMs),
            Elapsed = 0f,
            OnFrame = onFrame
        });

        if (!_timer.Enabled)
        {
            _lastTickMs = _clock.ElapsedMilliseconds;
            _timer.Start();
        }
    }

    /// <summary>取消某宿主上的全部动画（控件即将销毁时调用）。</summary>
    public static void Cancel(Control owner)
    {
        if (owner == null) return;
        _tracks.RemoveAll(t => ReferenceEquals(t.Owner, owner));
        if (_tracks.Count == 0) _timer.Stop();
    }

    private static void OnTick(object sender, EventArgs e)
    {
        long now = _clock.ElapsedMilliseconds;
        float deltaMs = now - _lastTickMs;
        _lastTickMs = now;

        // 掉帧保护：窗口被拖动/系统卡顿时 delta 会很大，
        // 直接夹到 2 帧，避免动画瞬间跳到终点（视觉上像闪一下）。
        if (deltaMs <= 0) deltaMs = FrameMs;
        if (deltaMs > FrameMs * 2) deltaMs = FrameMs * 2;

        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            Track t = _tracks[i];

            // 宿主已销毁：静默丢弃，不让已释放控件继续收帧
            if (t.Owner is Control c && (c.IsDisposed || !c.IsHandleCreated))
            {
                _tracks.RemoveAt(i);
                continue;
            }

            t.Elapsed += deltaMs;
            float raw = Math.Clamp(t.Elapsed / t.Duration, 0f, 1f);

            bool keep;
            try
            {
                keep = t.OnFrame(raw);
            }
            catch
            {
                // 动画回调异常不该拖垮整个 UI 循环
                keep = false;
            }

            if (!keep || raw >= 1f) _tracks.RemoveAt(i);
        }

        if (_tracks.Count == 0) _timer.Stop();
    }
}
