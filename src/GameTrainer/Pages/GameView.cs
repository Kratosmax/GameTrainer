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
        public float HoverT;
        public bool Editing;        // 输入框是否聚焦
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
        // 与 HomeView 完全一致的样式组合。
        // 这里刻意不加 Selectable / TabStop：它们会让控件进入可聚焦分支，
        // 在 PrintWindow / DrawToBitmap 下出现"布局正确但完全不绘制"的现象。
        // 键盘输入靠 ProcessCmdKey 处理，不依赖控件焦点。
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Tokens.PageBack;
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

    /// <summary>
    /// 取某语义第一个已填写的输入框内容（热键触发时用）。
    /// 没有则返回空串。
    /// </summary>
    public string FirstInputText(string semantic)
    {
        var b = _blocks.FirstOrDefault(x =>
            string.Equals(x.Semantic, semantic, StringComparison.OrdinalIgnoreCase));
        if (b == null) return "";

        // 优先返回正在编辑的缓冲内容
        if (b == _focusBlock && _focusRow != null && _buffer.Length > 0)
            return _buffer.Trim();

        return b.Rows.Select(r => r.Input.Trim()).FirstOrDefault(v => v.Length > 0) ?? "";
    }

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

    /// <summary>OnPaint 调用次数。用于诊断"内容区空白"是没绘制还是画错了。</summary>
    public int PaintCount { get; private set; }

    // ---------- 动画 ----------

    public void PlayEnter()
    {
        // 起始 0.35 而非 0：动画不推进时界面仍必须可读（理由见 HomeView.PlayEnter）
        _pageT = 0.35f;
        foreach (var b in _blocks) b.AppearT = 0.35f;

        int total = Tokens.AnimNormal + _blocks.Count * Tokens.AnimStagger;

        Animator.Run(this, "enter", total, raw =>
        {
            if (IsDisposed) return false;

            _pageT = Math.Max(0.35f, raw);
            float elapsed = raw * total;

            for (int i = 0; i < _blocks.Count; i++)
            {
                float local = Math.Clamp((elapsed - i * Tokens.AnimStagger) / Tokens.AnimNormal, 0f, 1f);
                _blocks[i].AppearT = Math.Max(0.35f, Easing.EaseOut(local));
            }

            Invalidate();
            return true;
        });
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintCount++;

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
        int gap = Tokens.S3;

        // 多列网格：列数按可用宽度自适应，卡片等分填满。
        //
        // 之前是单列全宽——宽窗口下一张卡横跨 1400px，
        // 右侧大片空白，空间利用率极低（用户反馈"挤在一起、拉不开"）。
        //
        // 最小列宽 420：集合卡片一行要放「名字 + 数值 + 输入框 + 应用」。
        // 380 时名字会被省略号吃掉（实测"罗伯特·戴维斯"变成"罗伯…"）。
        const int MinColW = 420;
        int avail = Width - pad * 2;
        int cols = Math.Max(1, (avail + gap) / (MinColW + gap));
        int colW = (avail - gap * (cols - 1)) / cols;

        // 卡片各自按内容定高，只对齐行顶部。
        //
        // 之前让整行等高，结果单值卡片（总资产，2 行）被集合卡片（6 行）撑高，
        // 中间留一大片空白，视觉上很怪。设置卡片高度不同是正常的。
        int y = pad - (int)_scrollY;

        for (int i = 0; i < _blocks.Count; i += cols)
        {
            int rowMaxH = 0;

            for (int c = 0; c < cols && i + c < _blocks.Count; c++)
            {
                var b = _blocks[i + c];
                int h = MeasureBlock(b);
                if (h > rowMaxH) rowMaxH = h;

                int x = pad + c * (colW + gap);
                b.Bounds = new Rectangle(x, y, colW, h);
                PaintBlock(g, b, b.Bounds);
            }

            // 下一行从本行最高的卡片底部开始，避免重叠
            y += rowMaxH + gap;
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
        if (t <= 0.001f) t = 0.35f;   // 兜底：任何情况下卡片都要可见

        int dy = (int)(14 * (1 - t));
        var r = new Rectangle(rc.X, rc.Y + dy, rc.Width, rc.Height);

        // 阴影只在动画结束后画
        if (t >= 0.99f) Draw.Shadow(g, r, Tokens.RadiusCard);

        Draw.Fill(g, r, Tokens.RadiusCard, Tokens.Surface);

        int p = Tokens.CardPadding;

        // ---- 标题行 ----
        var titleRect = new Rectangle(r.X + p, r.Y + Tokens.S3, r.Width - p * 2 - 100, 26);
        // 标题始终全不透明：它是定位信息，不能随动画淡入。
        Draw.TextLeft(g, b.Title, Tokens.FontSection, Tokens.TextPrimary, titleRect);

        // 右上状态
        var stateRect = new Rectangle(r.Right - p - 96, r.Y + Tokens.S3, 96, 26);
        string stateText;
        Color stateColor;
        if (!b.Ok) { stateText = "未运行"; stateColor = Tokens.TextMuted; }
        else if (b.IsCollection) { stateText = $"{b.Rows.Count} 个条目"; stateColor = Tokens.TextMuted; }
        else { stateText = "已读取"; stateColor = Tokens.Success; }

        Draw.TextRight(g, stateText, Tokens.FontCaption, stateColor, stateRect);

        // ---- 错误提示 ----
        if (!b.Ok)
        {
            var errRect = new Rectangle(r.X + p, r.Y + 38, r.Width - p * 2, 26);
            Draw.TextLeft(g, ShortenError(b.Error), Tokens.FontCaption, Tokens.TextMuted, errRect);
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

        // 行内四段的宽度分配：名称吃剩余、数值按内容、输入框与按钮固定。
        //
        // 之前 inputW=110 在 420px 列里放不下「应用」两字（被截断），
        // 现在输入框 96、按钮 64，合计 160 + 间距，给名称留足空间。
        int inputW = 96;
        int btnW = 64;
        int valueW = Math.Max(70,
            Draw.Measure(g, row.Value ?? "", Tokens.FontValue).Width + Tokens.S3);

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
            Draw.TextLeft(g, "新值", Tokens.FontCaption, Tokens.TextDisabled, textRect);
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
        // 空状态不依赖进入动画的进度。
        //
        // 实测教训：SetEmpty 后 _pageT 可能一直是 0（动画驱动器在某些时序下
        // 不会推进它），而用 _pageT 算 alpha 会让整块文字完全透明，
        // 表现为"内容区一片空白，但日志显示确实调用了 SetEmpty"。
        // 空状态是必须让人看见的信息，不能挂在可能不跑的动画上。
        const float t = 1f;

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

        // 不调 Focus()：控件没有 Selectable 样式，Focus 会失败。
        // 键盘输入改由父窗体 ProcessCmdKey 转发到 HandleKey / HandleChar，
        // 这样不依赖控件焦点，也不会因为抢焦点打断用户在其他窗口的操作。

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
    //
    // 控件没有 Selectable 样式，不接收焦点，所以键盘事件由父窗体
    // ProcessCmdKey / 主窗体的 KeyPress 转发到下面两个方法。
    // 好处：不依赖焦点，不会因为点了输入框就把焦点从用户正在用的
    // 其他窗口抢过来。

    /// <summary>是否有行处于编辑状态。父窗体据此决定要不要拦截按键。</summary>
    public bool IsEditing => _focusRow != null;

    /// <summary>
    /// 处理一个字符输入。返回 true 表示已消费，调用方应吞掉该按键。
    /// </summary>
    public bool HandleKeyChar(char ch)
    {
        if (_focusRow == null) return false;

        if (ch == '\b')
        {
            if (_caret > 0)
            {
                _buffer = _buffer.Remove(_caret - 1, 1);
                _caret--;
            }
        }
        else if (ch == '\r')
        {
            CommitEdit();
        }
        else if (!char.IsControl(ch))
        {
            _buffer = _buffer.Insert(_caret, ch.ToString());
            _caret++;
        }

        Invalidate();
        return true;
    }

    /// <summary>
    /// 处理功能键（方向、Esc、回车）。返回 true 表示已消费。
    /// </summary>
    public bool HandleKeyDown(Keys key)
    {
        if (_focusRow == null)
        {
            // 无编辑时：上下滚动内容区
            if (key == Keys.Down) { _scrollTarget = Math.Min(MaxScroll, _scrollTarget + 60); ScrollTo(); return true; }
            if (key == Keys.Up) { _scrollTarget = Math.Max(0, _scrollTarget - 60); ScrollTo(); return true; }
            return false;
        }

        switch (key)
        {
            case Keys.Left:
                _caret = Math.Max(0, _caret - 1);
                Invalidate();
                return true;
            case Keys.Right:
                _caret = Math.Min(_buffer.Length, _caret + 1);
                Invalidate();
                return true;
            case Keys.Escape:
                EndEdit();
                return true;
            case Keys.Enter:
                CommitEdit();
                return true;
            default:
                return false;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta, 0, MaxScroll);
        ScrollTo();
    }

    /// <summary>内容总高度（用于滚动上限）。</summary>
    private int ContentHeight
    {
        get
        {
            int pad = Tokens.S5;
            int total = pad;
            foreach (var b in _blocks) total += MeasureBlock(b) + Tokens.S3;
            return total + pad;
        }
    }

    /// <summary>可滚动的最大偏移。内容不超一屏时为 0。</summary>
    private int MaxScroll => Math.Max(0, ContentHeight - Height);

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
