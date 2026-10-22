using GameTrainer.UI;

namespace GameTrainer;

/// <summary>
/// 首页的游戏卡片：图标 + 名称 + 副标题，悬停有反馈，点击进入。
///
/// 视觉参考 DisplayControlLab v3 的卡片语言：
/// 白底、10px 圆角、1px 边框、悬停时边框变主色并轻微上浮。
/// 不用图片封面，图标优先取 exe 关联图标，没有就用名字首字占位——
/// 这样新适配包不需要额外准备美术资源。
/// </summary>
internal sealed class GameCard : Panel
{
    public const int CardWidth = 260;
    public const int CardHeight = 136;

    /// <summary>每行卡片数，由 HomePage 按容器宽度写入，用于计算自身宽度。</summary>
    public static int CardsPerRow = 4;

    public event Action Clicked;
    public event Action UninstallClicked;

    private readonly Label _name = new();
    private readonly Label _subtitle = new();
    private readonly Panel _iconBox = new();
    private readonly FlatButton _uninstall = new();

    private Image _icon;
    private string _placeholder = "?";

    /// <summary>悬停进度 0..1，由 Animator 驱动。</summary>
    private float _hoverT;

    /// <summary>按下进度 0..1，用于点击时的轻微下沉反馈。</summary>
    private float _pressT;

    /// <summary>
    /// 悬停动画。只改一个 float 并重绘，不做布局，
    /// 所以即使卡片很多也不会掉帧。
    /// </summary>
    private void AnimateHover(float target)
    {
        float from = _hoverT;
        Animator.Run(this, "hover", Tokens.AnimFast, raw =>
        {
            if (IsDisposed) return false;
            _hoverT = Easing.Lerp(from, target, Easing.EaseOut(raw));
            Invalidate();
            return true;
        });
    }

    /// <summary>点击反馈：轻微下沉再回弹，参照 iOS 的 cell 高亮。</summary>
    private void AnimatePress()
    {
        Animator.Run(this, "press", Tokens.AnimFast, raw =>
        {
            if (IsDisposed) return false;
            // 前半程按下，后半程弹回
            _pressT = raw < 0.5f ? Easing.EaseOut(raw * 2f) : 1f - Easing.EaseOut((raw - 0.5f) * 2f);
            Invalidate();
            return true;
        });
    }

    public GameCard()
    {
        DoubleBuffered = true;
        BackColor = Tokens.Surface;
        Margin = new Padding(Tokens.S2);
        Cursor = Cursors.Hand;
        Height = CardHeight;

        _iconBox.Size = new Size(44, 44);
        _iconBox.Location = new Point(Tokens.S4, Tokens.S4);
        _iconBox.BackColor = Tokens.Surface;
        _iconBox.Paint += PaintIcon;

        _name.Font = Tokens.FontBodyStrong;
        _name.ForeColor = Tokens.TextPrimary;
        _name.AutoSize = false;
        _name.Height = 26;
        _name.Location = new Point(Tokens.S4 + 52, Tokens.S4 + 2);
        _name.AutoEllipsis = true;

        _subtitle.Font = Tokens.FontCaption;
        _subtitle.ForeColor = Tokens.TextMuted;
        _subtitle.AutoSize = false;
        _subtitle.Height = 20;
        _subtitle.Location = new Point(Tokens.S4 + 52, Tokens.S4 + 26);
        _subtitle.AutoEllipsis = true;

        _uninstall.Text = "卸载";
        _uninstall.Kind = ButtonKind.Secondary;
        _uninstall.Size = new Size(60, 26);
        _uninstall.Visible = false;
        _uninstall.Click += (_, _) => UninstallClicked?.Invoke();

        Controls.Add(_iconBox);
        Controls.Add(_name);
        Controls.Add(_subtitle);
        Controls.Add(_uninstall);

        Click += (_, _) => { AnimatePress(); Clicked?.Invoke(); };
        foreach (Control c in new Control[] { _iconBox, _name, _subtitle })
            c.Click += (_, _) => Clicked?.Invoke();

        // 悬停用 160ms 缓动，而不是瞬间变色。
        // iOS 的"顺滑"很大程度来自状态之间都有过渡，没有硬切。
        MouseEnter += (_, _) => AnimateHover(1f);
        MouseLeave += (_, _) => AnimateHover(0f);
    }

    public string AdapterName
    {
        set => _name.Text = value;
    }

    public string Subtitle
    {
        set => _subtitle.Text = value;
    }

    public Image IconImage
    {
        set { _icon = value; _iconBox.Invalidate(); }
    }

    public string PlaceholderText
    {
        set => _placeholder = value;
    }

    /// <summary>已安装的卡片才显示卸载按钮。</summary>
    public bool Installed
    {
        set
        {
            _uninstall.Visible = value;
            if (value) _uninstall.Location = new Point(Tokens.S4, CardHeight - 26 - Tokens.S3);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int textW = Width - (Tokens.S4 + 52) - Tokens.S4;
        if (textW > 40)
        {
            _name.Width = textW;
            _subtitle.Width = textW;
        }
    }

    /// <summary>
    /// 画图标：有真实图标就画图片，没有就画圆角占位块 + 首字。
    /// 占位块用主色的低饱和版本，避免和真实图标视觉冲突。
    /// </summary>
    private void PaintIcon(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (_icon != null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(_icon, new Rectangle(0, 0, _iconBox.Width, _iconBox.Height));
            return;
        }

        using var bg = new SolidBrush(Tokens.AccentSubtle);
        using var path = Draw.Round(new Rectangle(0, 0, _iconBox.Width - 1, _iconBox.Height - 1), Tokens.RadiusIcon);
        g.FillPath(bg, path);

        using var fg = new SolidBrush(Tokens.Accent);
        using var font = new Font(Tokens.FontBody.FontFamily, 16f, FontStyle.Bold);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(_placeholder, font, fg, new RectangleF(0, 0, _iconBox.Width, _iconBox.Height), sf);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 按下时整体下沉 1px，营造"被按到"的物理感
        int sink = (int)Math.Round(_pressT * 1.5f);
        var rect = new Rectangle(0, sink, Width - 1, Height - 1 - sink);

        using var path = Draw.Round(rect, Tokens.RadiusCard);

        // 悬停时背景从白渐变到极浅的主色，边框同步变主色并加粗。
        // 全部按 _hoverT 插值，所以过渡是连续的而不是两态跳变。
        Color bgColor = Easing.LerpColor(Tokens.Surface, Tokens.AccentSubtle, _hoverT * 0.55f);
        using var bg = new SolidBrush(bgColor);
        g.FillPath(bg, path);

        Color borderColor = Easing.LerpColor(Tokens.Border, Tokens.Accent, _hoverT);
        float borderW = 1f + _hoverT * 0.6f;
        using var pen = new Pen(borderColor, borderW);
        g.DrawPath(pen, path);
    }
}
