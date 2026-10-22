using System.Drawing.Drawing2D;
using GameTrainer.Adapter;
using GameTrainer.UI;
using Item = GameTrainer.Adapter.AdapterRunner.CollectionItem;

namespace GameTrainer.Pages;

/// <summary>
/// 游戏修改页 —— OriginOS 风格，单控件全自绘。
///
/// 替代旧的三层结构（CardPanel → CollectionCard / ValueCard → 若干 Label/TextBox/Button）。
/// 旧结构的问题：
///   - 一次刷新创建 40+ 个控件，每个独立重绘，动画掉帧
///   - 应用按钮靠手工算坐标，窗口变窄时被裁掉（已踩）
///   - 集合卡片的行高被 TableLayoutPanel 反控，10 个员工只显示 3 行（已踩）
///
/// 单控件方案把这些问题一次性消掉：布局、命中、绘制都在一处，
/// 没有父容器钳制子控件这回事。
/// </summary>
internal sealed class GameView : Control
{
    // ---------- 数据模型 ----------

    private sealed class Row
    {
        public int Id;              // 条目 ID（集合语义）或 0（单值语义）
        public string Name = "";    // 显示名
        public string Value = "";   // 当前值文本
        public string Input = "";   // 输入框内容
        public Rectangle Bounds;
        public Rectangle InputRect;
        public Rectangle ApplyRect;
        public float AppearT;
        public float HoverT;
        public bool Editing;        // 输入框是否聚焦
        public int CaretPos;
    }

    private sealed class Block
    {
        public string Semantic = "";
        public string Title = "";
        public bool IsCollection;
        public bool Ok;
        public string Error;
        public List<Row> Rows = new();
        public Rectangle Bounds;
        public float AppearT;
        public bool Collapsed;
    }

    private readonly List<Block> _blocks = new();
    private float _pageT;
    private int _hoverRow = -1;
    private Block _hoverBlock;
    private float _scrollY, _scrollTarget;

    // 焦点与输入
    private Block _focusBlock;
    private Row _focusRow;
    private string _buffer = "";
    private int _caret;

    // ---------- 事件 ----------

    /// <summary>单值写入：语义 + 新值。</summary>
    public event Action<string, string> ValueSubmitted;

    /// <summary>集合条目写入：语义 + 条目ID + 新值。</summary>
    public event Action<string, int, string> EntrySubmitted;

    /// <summary>双击某行，请求打开该项设置。</summary>
    public event Action<string, int, string> SettingsRequested;

    public GameView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = Tokens.PageBack;
        TabStop = true;
    }

    // ---------- 数据填充 ----------

    /// <summary>
    /// 用会话读取结果重建内容。
    ///
    /// 只重建「结构」——标题、行数、顺序；值变化也重建，
    /// 因为集合的条目数会随游戏进度增减，没有更省的更新方式。
    /// </summary>
    public void SetData(GameAdapter adapter, IReadOnlyDictionary<string, AdapterRunner.ReadResult> results)
    {
        // 记住正在编辑的目标，重建后恢复焦点
        string focusSemantic = _focusBlock?.Semantic;
        int focusId = _focusRow?.Id ?? -1;
        string focusBuffer = _buffer;
        int focusCaret = _caret;

        _blocks.Clear();

        foreach (var b in adapter.Bindings)
        {
            var block = new Block
            {
                Semantic = b.Semantic,
                Title = DisplayName(b.Semantic),
                IsCollection = string.Equals(b.Kind, "memory_collection", StringComparison.OrdinalIgnoreCase)
                               && !b.OwnerField
            };

            if (results != null && results.TryGetValue(b.Semantic, out var r))
            {
                block.Ok = r.Ok;
                block.Error = r.Error;

                if (block.IsCollection && r.Items != null)
                {
                    foreach (var it in r.Items)
                        block.Rows.Add(new Row { Id = it.Id, Name = it.Name, Value = it.Value });
                }
                else if (r.Ok)
                {
                    block.Rows.Add(new Row { Id = 0, Name = "", Value = r.Value });
                }
            }

            // 折叠状态来自用户偏好
            block.Collapsed = adapter != null && _collapsedLookup?.Invoke(b.Semantic) == true;

            _blocks.Add(block);
        }

        // 恢复焦点
        if (focusSemantic != null)
        {
            var fb = _blocks.FirstOrDefault(x => x.Semantic == focusSemantic);
            var fr = fb?.Rows.FirstOrDefault(x => x.Id == focusId);
            if (fr != null)
            {
                _focusBlock = fb;
                _focusRow = fr;
                fr.Editing = true;
                fr.Input = focusBuffer;
                _buffer = focusBuffer;
                _caret = focusCaret;
            }
        }

        PlayEnter();
    }

    /// <summary>折叠状态查询回调（由外部注入，避免直接依赖 Prefs）。</summary>
    private Func<string, bool> _collapsedLookup;

    public void SetCollapsedLookup(Func<string, bool> lookup) => _collapsedLookup = lookup;

    private static string DisplayName(string semantic) => semantic switch
    {
        "gold_total" => "总资产",
        "employee_good" => "员工好感度",
        "employee_bad" => "员工猥琐值",
        "employee_medical" => "员工医术",
        "health" => "生命值",
        "health_max" => "生命上限",
        "gold" => "金币",
        "level" => "等级",
        "energy" => "能量",
        "attack" => "攻击",
        "defense" => "防御",
        _ => Semantics.Display(semantic)
    };

    /// <summary>没有数据时的中性提示（游戏未运行）。</summary>
    public void SetEmpty(string heading, string detail)
    {
        _blocks.Clear();
        _emptyHeading = heading;
        _emptyDetail = detail;
        PlayEnter();
    }

    private string _emptyHeading;
    private string _emptyDetail;

    // ---------- 动画 ----------

    public void PlayEnter()
    {
        _pageT = 0f;
        foreach (var b in _blocks) b.AppearT = 0f;

        int total = Tokens.AnimNormal + _blocks.Count * Tokens.AnimStagger;

        Animator.Run(this, "enter", total, raw =>
        {
            if (IsDisposed) return false;

            _pageT = raw;
            float elapsed = raw * total;

            for (int i = 0; i < _blocks.Count; i++)
            {
                float local = Math.Clamp((elapsed - i * Tokens.AnimStagger) / Tokens.AnimNormal, 0f, 1f);
                _blocks[i].AppearT = Easing.EaseOut(local);
            }

            Invalidate();
            return true;
        });
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bg = new SolidBrush(Tokens.PageBack))
            g.FillRectangle(bg, ClientRectangle);

        if (_blocks.Count == 0)
        {
            PaintEmpty(g);
            return;
        }

        int pad = Tokens.S5;
        int y = pad - (int)_scrollY;

        foreach (var b in _blocks)
        {
            int h = MeasureBlock(b);
            b.Bounds = new Rectangle(pad, y, Width - pad * 2, h);
            PaintBlock(g, b, b.Bounds);
            y += h + Tokens.S3;
        }
    }

    /// <summary>按行数算块高度。折叠时只留标题。</summary>
    private int MeasureBlock(Block b)
    {
        int headerH = 58;   // 标题 + 状态

        if (b.Collapsed) return headerH;
        if (!b.Ok) return headerH + 34;            // 错误提示行
        if (b.Rows.Count == 0) return headerH + 34;

        int rowH = b.IsCollection ? 46 : 56;
        return headerH + b.Rows.Count * rowH + Tokens.S2;
    }

    private void PaintBlock(Graphics g, Block b, Rectangle rc)
    {
        float t = b.AppearT;
        if (t <= 0.001f) return;

        int dy = (int)(14 * (1 - t));
        var r = new Rectangle(rc.X, rc.Y + dy, rc.Width, rc.Height);

        // 阴影只在动画结束后画
        if (t >= 0.99f) Draw.Shadow(g, r, Tokens.RadiusCard);

        Draw.Fill(g, r, Tokens.RadiusCard, Tokens.Surface);

        int p = Tokens.CardPadding;

        // ---- 标题行 ----
        var titleRect = new Rectangle(r.X + p, r.Y + Tokens.S3, r.Width - p * 2 - 100, 26);
        Draw.TextLeft(g, b.Title, Tokens.FontSection,
            Easing.WithAlpha(Tokens.TextPrimary, t), titleRect);

        // 右上状态
        var stateRect = new Rectangle(r.Right - p - 96, r.Y + Tokens.S3, 96, 26);
        string stateText;
        Color stateColor;
        if (!b.Ok) { stateText = "未运行"; stateColor = Tokens.TextMuted; }
        else if (b.IsCollection) { stateText = $"{b.Rows.Count} 个条目"; stateColor = Tokens.TextMuted; }
        else { stateText = "已读取"; stateColor = Tokens.Success; }

        Draw.TextRight(g, stateText, Tokens.FontCaption,
            Easing.WithAlpha(stateColor, t), stateRect);

        // ---- 错误提示 ----
        if (!b.Ok)
        {
            var errRect = new Rectangle(r.X + p, r.Y + 38, r.Width - p * 2, 26);
            Draw.TextLeft(g, ShortenError(b.Error), Tokens.FontCaption,
                Easing.WithAlpha(Tokens.TextMuted, t), errRect);
            return;
        }

        if (b.Collapsed || b.Rows.Count == 0) return;

        // ---- 行 ----
        int rowH = b.IsCollection ? 46 : 56;
        int rowY = r.Y + 58;

        foreach (var row in b.Rows)
        {
            var rowRect = new Rectangle(r.X + p, rowY, r.Width - p * 2, rowH);
            row.Bounds = rowRect;
            PaintRow(g, b, row, rowRect);
            rowY += rowH;
        }
    }

    private void PaintRow(Graphics g, Block b, Row row, Rectangle r)
    {
        // 悬停底
        if (row.HoverT > 0.01f)
        {
            Draw.Fill(g, new Rectangle(r.X - 4, r.Y + 2, r.Width + 8, r.Height - 4),
                Tokens.RadiusControl,
                Easing.WithAlpha(Tokens.SurfaceHover, row.HoverT * 0.7f));
        }

        int inputW = 84;
        int btnW = 64;
        int valueW = 72;

        // 左侧：名称（集合）或标签（单值）
        int nameW = r.Width - inputW - btnW - valueW - Tokens.S3 * 3;
        var nameRect = new Rectangle(r.X, r.Y, Math.Max(60, nameW), r.Height);

        string label = b.IsCollection
            ? (string.IsNullOrEmpty(row.Name) ? $"#{row.Id}" : row.Name)
            : "当前值";

        Draw.TextLeft(g, label, Tokens.FontBody,
            b.IsCollection ? Tokens.TextPrimary : Tokens.TextSecondary, nameRect);

        // 中间：当前值
        var valRect = new Rectangle(nameRect.Right + Tokens.S3, r.Y, valueW, r.Height);
        Draw.TextRight(g, row.Value, b.IsCollection ? Tokens.FontBodyStrong : Tokens.FontValue,
            Tokens.Accent, valRect);

        // 输入框
        row.InputRect = new Rectangle(valRect.Right + Tokens.S3, r.Y + (r.Height - 34) / 2, inputW, 34);
        PaintInput(g, row);

        // 应用按钮
        row.ApplyRect = new Rectangle(row.InputRect.Right + Tokens.S2, r.Y + (r.Height - 34) / 2, btnW, 34);
        bool canApply = row.Input.Length > 0;
        Draw.Fill(g, row.ApplyRect, Tokens.RadiusPill,
            canApply ? Tokens.AccentSoft : Tokens.SurfaceAlt);
        Draw.TextCenter(g, "应用", Tokens.FontCaption,
            canApply ? Tokens.Accent : Tokens.TextDisabled, row.ApplyRect);
    }

    private void PaintInput(Graphics g, Row row)
    {
        Draw.Fill(g, row.InputRect, Tokens.RadiusControl, Tokens.SurfaceAlt);

        if (row.Editing)
            Draw.Stroke(g, row.InputRect, Tokens.RadiusControl, Tokens.Accent, 1.6f);
        else
            Draw.Stroke(g, row.InputRect, Tokens.RadiusControl, Tokens.BorderSoft, 1f);

        string text = row.Editing ? _buffer : row.Input;
        var textRect = new Rectangle(row.InputRect.X + 10, row.InputRect.Y,
            row.InputRect.Width - 20, row.InputRect.Height);

        if (string.IsNullOrEmpty(text) && !row.Editing)
        {
            Draw.TextLeft(g, "输入新值", Tokens.FontCaption, Tokens.TextDisabled, textRect);
            return;
        }

        Draw.TextLeft(g, text, Tokens.FontBody, Tokens.TextPrimary, textRect);

        // 光标
        if (row.Editing)
        {
            int textW = Draw.Measure(g, text[..Math.Clamp(_caret, 0, text.Length)], Tokens.FontBody).Width;
            int cx = row.InputRect.X + 10 + textW;
            using var pen = new Pen(Tokens.Accent, 1.6f);
            g.DrawLine(pen, cx, row.InputRect.Y + 9, cx, row.InputRect.Bottom - 9);
        }
    }

    private void PaintEmpty(Graphics g)
    {
        float t = Easing.EaseOut(Math.Clamp(_pageT * 2f, 0f, 1f));

        string heading = _emptyHeading ?? "游戏未运行";
        string detail = _emptyDetail ?? "先启动游戏，再点「刷新数值」读取存档";

        Draw.TextCenter(g, heading, Tokens.FontSection,
            Easing.WithAlpha(Tokens.TextSecondary, t),
            new Rectangle(0, Height / 2 - 50, Width, 30));

        Draw.TextCenter(g, detail, Tokens.FontCaption,
            Easing.WithAlpha(Tokens.TextMuted, t),
            new Rectangle(0, Height / 2 - 16, Width, 24));
    }

    private static string ShortenError(string err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "读取失败";
        if (err.Contains("找不到进程")) return "游戏未启动";
        if (err.Contains("无实例")) return "不在当前场景";
        string one = err.Split('\n')[0].Trim();
        return one.Length > 40 ? one[..40] + "…" : one;
    }

    // ---------- 交互 ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var (block, row) = HitTest(e.Location);

        if (row != _focusRow || block != _focusBlock)
        {
            // 悬停高亮
            if (_hoverRow >= 0 && _hoverBlock != null)
            {
                var old = _hoverBlock.Rows.FirstOrDefault(x => x.Id == _hoverRow);
                if (old != null) AnimateRowHover(old, 0f);
            }

            _hoverBlock = block;
            _hoverRow = row?.Id ?? -1;

            if (row != null) AnimateRowHover(row, 1f);
        }

        Cursor = row != null ? Cursors.Hand : Cursors.IBeam;
    }

    private void AnimateRowHover(Row row, float target)
    {
        float from = row.HoverT;
        Animator.Run(this, "rowhover" + row.Id, Tokens.AnimFast, raw =>
        {
            if (IsDisposed) return false;
            row.HoverT = Easing.Lerp(from, target, Easing.EaseOut(raw));
            Invalidate();
            return true;
        });
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        Focus();

        var (block, row) = HitTest(e.Location);
        if (row == null) { EndEdit(); return; }

        // 点应用按钮
        if (row.ApplyRect.Contains(e.Location))
        {
            CommitEdit();
            return;
        }

        // 点输入框 → 进入编辑
        if (row.InputRect.Contains(e.Location))
        {
            BeginEdit(block, row);
            return;
        }

        EndEdit();
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);

        var pos = PointToClient(Cursor.Position);
        var (block, row) = HitTest(pos);
        if (block == null || row == null) return;

        SettingsRequested?.Invoke(block.Semantic, row.Id, block.Title);
    }

    private void BeginEdit(Block block, Row row)
    {
        EndEdit();

        _focusBlock = block;
        _focusRow = row;
        row.Editing = true;
        _buffer = row.Input;
        _caret = _buffer.Length;
        Invalidate();
    }

    private void EndEdit()
    {
        if (_focusRow != null)
        {
            _focusRow.Editing = false;
            _focusRow = null;
            _focusBlock = null;
        }
        _buffer = "";
        _caret = 0;
        Invalidate();
    }

    private void CommitEdit()
    {
        if (_focusBlock == null || _focusRow == null) return;

        string value = _buffer.Trim();
        if (value.Length == 0) return;

        var block = _focusBlock;
        var row = _focusRow;

        row.Input = value;
        EndEdit();

        if (block.IsCollection) EntrySubmitted?.Invoke(block.Semantic, row.Id, value);
        else ValueSubmitted?.Invoke(block.Semantic, value);
    }

    // ---------- 键盘 ----------

    protected override bool IsInputKey(Keys keyData)
    {
        // 让方向键与字符键进入 OnKeyDown 而不是被父容器吃掉
        return true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);

        if (_focusRow == null) return;

        if (e.KeyChar == '\b')
        {
            if (_caret > 0)
            {
                _buffer = _buffer.Remove(_caret - 1, 1);
                _caret--;
            }
        }
        else if (e.KeyChar == '\r')
        {
            CommitEdit();
        }
        else if (!char.IsControl(e.KeyChar))
        {
            _buffer = _buffer.Insert(_caret, e.KeyChar.ToString());
            _caret++;
        }

        e.Handled = true;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_focusRow == null)
        {
            // 无编辑时：上下滚动
            if (e.KeyCode == Keys.Down) { _scrollTarget += 60; ScrollTo(); }
            else if (e.KeyCode == Keys.Up) { _scrollTarget = Math.Max(0, _scrollTarget - 60); ScrollTo(); }
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Left:
                _caret = Math.Max(0, _caret - 1);
                e.Handled = true;
                Invalidate();
                break;
            case Keys.Right:
                _caret = Math.Min(_buffer.Length, _caret + 1);
                e.Handled = true;
                Invalidate();
                break;
            case Keys.Escape:
                EndEdit();
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollTarget = Math.Max(0, _scrollTarget - e.Delta);
        ScrollTo();
    }

    private void ScrollTo()
    {
        float from = _scrollY;
        Animator.Run(this, "scroll", Tokens.AnimFast, raw =>
        {
            if (IsDisposed) return false;
            _scrollY = Easing.Lerp(from, _scrollTarget, Easing.EaseOut(raw));
            Invalidate();
            return true;
        });
    }

    /// <summary>命中测试：返回命中的块与行。</summary>
    private (Block, Row) HitTest(Point p)
    {
        foreach (var b in _blocks)
        {
            if (!b.Bounds.Contains(p)) continue;
            foreach (var r in b.Rows)
                if (r.Bounds.Contains(p)) return (b, r);
            return (b, null);
        }
        return (null, null);
    }
}
