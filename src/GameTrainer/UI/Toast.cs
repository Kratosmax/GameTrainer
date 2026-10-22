using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GameTrainer.UI;

/// <summary>
/// 浮在窗口底部的轻量提示条。
///
/// 为什么需要它：
/// 之前所有失败都只写进日志抽屉，而日志默认是收起的。
/// 用户点了「启动游戏」，什么都没发生 —— 因为失败原因在看不见的地方。
/// 操作必须当场给出反馈，不能要求用户去翻日志。
///
/// 设计约束：
///   - 不抢焦点（不 Activate、不 Focus），不打断用户当前操作
///   - 自动消失，成功 3 秒、失败 6 秒（失败留久一点，要看清）
///   - 同一时刻只显示一条，新提示顶掉旧的
///   - 淡入淡出，不做位移，避免盖住正在看的内容
/// </summary>
internal sealed class Toast : Control
{
    private enum Kind { Info, Success, Warning, Error }

    private Kind _kind = Kind.Info;
    private string _text = "";
    private float _t;            // 0..1 透明度
    private bool _visible;       // 逻辑可见（与 Control.Visible 分开，便于淡出）
    private int _holdMs;         // 保持不透明的时间
    private DateTime _shownAt;
    private readonly System.Windows.Forms.Timer _timer;

    private const int PadX = 20;
    private const int PadY = 14;
    private const int MaxWidth = 560;

    public Toast()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Visible = false;
        Enabled = false;

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>显示一条提示。重复调用会顶掉当前提示。</summary>
    public void Show(string text, bool isError = false, bool isWarning = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _text = text.Trim();
        _kind = isError ? Kind.Error : isWarning ? Kind.Warning : Kind.Success;
        _holdMs = isError ? 6000 : 3000;
        _shownAt = DateTime.UtcNow;
        _visible = true;

        // 先按文本算尺寸，再定位
        Size = Measure();
        Reposition();
        Visible = true;

        _timer.Start();
        Invalidate();
    }

    /// <summary>立即收起。</summary>
    public void Dismiss()
    {
        _visible = false;
        _timer.Stop();
        Visible = false;
        _t = 0;
    }

    /// <summary>父容器尺寸变化时重新贴底居中。</summary>
    public void Reposition()
    {
        if (Parent == null) return;

        Size = Measure();

        // 居中要以「可见区」为准，不是 ClientSize：
        // 容器出现纵向滚动条时，ClientSize.Width 仍含滚动条宽度，
        // 直接拿它算会让提示整体偏左约半个滚动条。
        int visibleW = Parent.ClientSize.Width;
        if (Parent is ScrollableControl sc && sc.VerticalScroll.Visible)
            visibleW -= SystemInformation.VerticalScrollBarWidth;

        int x = (visibleW - Width) / 2;
        int y = Parent.ClientSize.Height - Height - 24;

        // 底部有状态栏时再往上抬一点，别压在状态栏上
        if (Parent.Controls.Find("statusBar", true).Length > 0) y -= 8;

        Location = new Point(Math.Max(8, x), Math.Max(8, y));
    }

    private Size Measure()
    {
        int maxTextW = MaxWidth - PadX * 2;
        Size textSize = TextRenderer.MeasureText(_text, Tokens.FontBody,
            new Size(maxTextW, int.MaxValue), TextFormatFlags.WordBreak);

        return new Size(
            Math.Min(MaxWidth, textSize.Width + PadX * 2),
            textSize.Height + PadY * 2);
    }

    private void Tick()
    {
        var elapsed = (DateTime.UtcNow - _shownAt).TotalMilliseconds;

        const int fadeIn = 120;
        const int fadeOut = 200;

        if (!_visible)
        {
            // 已经在淡出
        }

        if (elapsed < fadeIn)
        {
            _t = (float)(elapsed / fadeIn);
        }
        else if (elapsed < fadeIn + _holdMs)
        {
            _t = 1f;
        }
        else if (elapsed < fadeIn + _holdMs + fadeOut)
        {
            _t = 1f - (float)((elapsed - fadeIn - _holdMs) / fadeOut);
        }
        else
        {
            _t = 0f;
            _timer.Stop();
            Visible = false;
            return;
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_t <= 0.01f) return;

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 背景色按类型区分。失败用 Danger，警告用 Warning，成功用 Success。
        Color bg = _kind switch
        {
            Kind.Error => Tokens.Danger,
            Kind.Warning => Tokens.Warning,
            _ => Tokens.Success
        };

        int alpha = (int)Math.Clamp(_t * 255, 0, 255);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Draw.RoundedRect(rect, Tokens.ControlRadius);

        using (var b = new SolidBrush(Color.FromArgb(alpha, bg)))
            g.FillPath(b, path);

        // 文字
        int textAlpha = alpha;
        using var tb = new SolidBrush(Color.FromArgb(textAlpha, Color.White));

        var textRect = new Rectangle(PadX, PadY,
            Width - PadX * 2, Height - PadY * 2);

        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        g.DrawString(_text, Tokens.FontBody, tb, textRect, fmt);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }
}
