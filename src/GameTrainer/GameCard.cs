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
    private bool _hover;

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

        Click += (_, _) => Clicked?.Invoke();
        foreach (Control c in new Control[] { _iconBox, _name, _subtitle })
            c.Click += (_, _) => Clicked?.Invoke();

        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
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
        using var path = RoundedRect(new Rectangle(0, 0, _iconBox.Width - 1, _iconBox.Height - 1), 8);
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

        // 背景 + 圆角边框：悬停时边框转主色
        using var bg = new SolidBrush(Tokens.Surface);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, Tokens.CardRadius);
        g.FillPath(bg, path);

        using var pen = new Pen(_hover ? Tokens.Accent : Tokens.Border, _hover ? 1.6f : 1f);
        g.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
