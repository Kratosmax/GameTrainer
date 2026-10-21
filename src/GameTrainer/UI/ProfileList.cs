using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameTrainer.UI;

/// <summary>侧栏里的一个游戏 profile 行：标题 + 副标题，两行自绘，选中态可辨识。</summary>
internal sealed class ProfileRow : Control
{
    private bool _selected;
    private bool _hover;

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>无效 profile 用危险色提示，不静默吞掉。</summary>
    public bool IsInvalid { get; set; }

    public ProfileRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        Height = 56;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    /// <summary>
    /// 侧栏宽度有限，长游戏名必然被省略号截断。
    /// 按 ui-design.md 要求，截断必须配悬停完整提示，否则用户无法确认是哪个游戏。
    /// </summary>
    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        string text = string.IsNullOrEmpty(Subtitle) ? Title : Title + Environment.NewLine + Subtitle;
        _tip.SetToolTip(this, text);
        base.OnMouseEnter(e);
    }

    private readonly ToolTip _tip = new();

    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.SidebarBack);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        if (_selected || _hover)
        {
            using var b = new SolidBrush(_selected ? Tokens.Surface : Tokens.RowHover);
            using var path = Draw.RoundedRect(rect, Tokens.ControlRadius);
            g.FillPath(b, path);
        }

        if (_selected)
        {
            // 选中行加一圈淡边界，白底压在侧栏灰底上更清楚
            using var pen = new Pen(Tokens.AccentSubtle, 1f);
            using var path = Draw.RoundedRect(rect, Tokens.ControlRadius);
            g.DrawPath(pen, path);

            var bar = new Rectangle(0, (Height - 20) / 2, 3, 20);
            using var accent = new SolidBrush(Tokens.Accent);
            using var barPath = Draw.RoundedRect(bar, 2);
            g.FillPath(accent, barPath);
        }

        int left = Tokens.S3 + (_selected ? 4 : 0);
        var titleRect = new Rectangle(left, 9, Width - left - Tokens.S2, 20);
        var subRect = new Rectangle(left, 29, Width - left - Tokens.S2, 18);

        Draw.Text(g, Title, Tokens.FontBodyStrong,
            _selected ? Tokens.TextPrimary : Tokens.TextPrimary, titleRect);

        Draw.Text(g, Subtitle, Tokens.FontCaption,
            IsInvalid ? Tokens.Danger : Tokens.TextSecondary, subRect);
    }
}

/// <summary>
/// 侧栏 profile 列表容器。负责滚动、选中互斥与键盘上下移动。
/// 不用 ListBox：系统 ListBox 无法做到两行布局与设计令牌一致的选中态。
/// </summary>
internal sealed class ProfileListBox : Panel
{
    private readonly List<ProfileRow> _rows = new();
    private int _selectedIndex = -1;

    public event EventHandler SelectionChanged;

    public ProfileListBox()
    {
        AutoScroll = true;
        BackColor = Color.Transparent;
        DoubleBuffered = true;
        TabStop = true;
    }

    public int Count => _rows.Count;

    public int SelectedIndex => _selectedIndex;

    public object SelectedTag => _selectedIndex >= 0 && _selectedIndex < _rows.Count
        ? _rows[_selectedIndex].Tag
        : null;

    /// <summary>
    /// 按 tag 选中某一行（首页点卡片进入游戏时用）。
    /// 找不到时不动选中态，返回 false 让调用方决定怎么办。
    /// </summary>
    public bool SelectTag(object tag)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (ReferenceEquals(_rows[i].Tag, tag) || Equals(_rows[i].Tag, tag))
            {
                _selectedIndex = i;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }
        return false;
    }

    public void Clear()
    {
        foreach (ProfileRow r in _rows) r.Dispose();
        _rows.Clear();
        Controls.Clear();
        _selectedIndex = -1;
    }

    public void Add(string title, string subtitle, object tag, bool invalid = false)
    {
        var row = new ProfileRow
        {
            Title = title,
            Subtitle = subtitle,
            Tag = tag,
            IsInvalid = invalid,
            Left = 0,
            Top = _rows.Count * 60,
            Width = ClientSize.Width
        };
        int index = _rows.Count;
        row.Click += (_, _) => Select(index);
        _rows.Add(row);
        Controls.Add(row);
        LayoutRows();
    }

    public void Select(int index)
    {
        if (index < 0 || index >= _rows.Count || index == _selectedIndex) return;
        _selectedIndex = index;
        for (int i = 0; i < _rows.Count; i++) _rows[i].Selected = i == index;
        ScrollControlIntoView(_rows[index]);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutRows();
    }

    private void LayoutRows()
    {
        int w = Math.Max(80, ClientSize.Width - (VerticalScroll.Visible ? 0 : 0));
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Left = 0;
            _rows[i].Top = i * 60;
            _rows[i].Width = w;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Down && _selectedIndex < _rows.Count - 1)
        {
            Select(_selectedIndex + 1);
            return true;
        }
        if (keyData == Keys.Up && _selectedIndex > 0)
        {
            Select(_selectedIndex - 1);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
