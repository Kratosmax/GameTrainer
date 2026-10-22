using System.Drawing.Drawing2D;
using GameTrainer.Adapter;
using GameTrainer.UI;

namespace GameTrainer.Pages;

/// <summary>
/// 游戏库首页 —— OriginOS 风格，单控件全自绘。
///
/// 为什么不用子控件拼：
/// 旧 HomePage 用 FlowLayoutPanel + 每张卡一个 GameCard 控件，
/// 一次刷新要创建 4~8 个控件，每个控件各自重绘、各自响应鼠标，
/// 动画时帧时间不均匀（实测拖动窗口明显掉帧）。
///
/// 单控件方案：一个 Graphics 上下文，一帧内画完全部卡片，
/// 命中测试自己算。代价是要手写布局，收益是动画稳、代码集中。
/// </summary>
internal sealed class HomeView : Control
{
    // ---------- 数据 ----------

    private sealed class Item
    {
        public string Id = "";
        public string Name = "";
        public string Sub = "";
        public string Engine = "";
        public int ValueCount;
        public bool Installed;
        public Image Icon;          // null 时用首字占位
        public Color Tint;
        public AdapterRepository.RemoteAdapter Remote;   // 未安装时的下载源

        /// <summary>
        /// 小写搜索键，加载时算一次。
        /// 不在每次按键时对每个卡片做 ToLower——那会在输入过程中
        /// 反复分配字符串，卡片一多就明显卡顿。
        /// </summary>
        public string SearchKey = "";
        public GameAdapter Adapter;                       // 已安装时的适配包

        // 运行时状态（每帧更新）
        public Rectangle Bounds;
        public float AppearT;
        public float HoverT;
        public float PressT;
    }

    private readonly List<Item> _items = new();
    private float _pageT;              // 页面进入进度
    private int _hover = -1;
    private int _press = -1;
    private float _scrollY;            // 滚动偏移（用于超出一屏时）

    /// <summary>上一次绘制时的内容总高度。滚动上限靠它算。</summary>
    private int _contentH;

    /// <summary>搜索词（已转小写）。空串表示不过滤。</summary>
    private string _query = "";

    /// <summary>
    /// 过滤结果缓存。只有 _query 或 _items 变化时才重算，
    /// 绘制与滚动时直接复用，避免每帧都跑一遍过滤。
    /// </summary>
    private List<Item> _filtered = new();

    /// <summary>过滤结果是否还有效。</summary>
    private bool _filterDirty = true;

    /// <summary>搜索输入框。作为真实子控件存在，才能接收键盘与输入法。</summary>
    private TextBox _searchBox;
    private float _scrollTarget;
    private string _remoteError;

    // ---------- 事件 ----------

    public event Action<GameAdapter> GameSelected;
    public event Action<AdapterRepository.RemoteAdapter> DownloadRequested;
    public event Action<string> UninstallRequested;

    /// <summary>右键「设置游戏路径」：参数是适配包 id。</summary>
    public event Action<string> LocateExeRequested;

    public HomeView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Tokens.PageBack;
    
        // 搜索框用原生 TextBox，自绘的拿不到输入法、也没法选中文字。
        // 放在头部右上角，宽度固定，不随卡片列数变化。
        _searchBox = new TextBox
        {
            Font = Tokens.FontBody,
            BorderStyle = BorderStyle.None,
            BackColor = Tokens.Surface,
            ForeColor = Tokens.TextPrimary,
            Width = 220,
            Height = 26,
            PlaceholderText = "搜索游戏名…"
        };

        _searchBox.TextChanged += (_, _) => SetQuery(_searchBox.Text);
        _searchBox.KeyDown += (_, e) =>
        {
            // Esc 清空搜索
            if (e.KeyCode == Keys.Escape)
            {
                _searchBox.Text = "";
                e.SuppressKeyPress = true;
            }
        };

        Controls.Add(_searchBox);

        // 尺寸变化时把搜索框贴到右上角
        Resize += (_, _) => LayoutSearchBox();
}

    // ---------- 数据填充 ----------

    /// <summary>
    /// 填充内容。已安装的排前面，可下载的排后面。
    /// 每次填充会重放进入动画——数据变化本身就是一次「新内容出现」。
    /// </summary>
    public void Populate(
        List<GameAdapter> installed,
        List<AdapterRepository.RemoteAdapter> remote,
        string remoteError)
    {
        _items.Clear();
        _remoteError = remoteError;

        foreach (var a in installed)
        {
            _items.Add(new Item
            {
                Id = a.Id,
                Name = a.Name,
                Sub = $"{a.Engine} · {a.Bindings.Count} 个数值",
                Engine = a.Engine,
                ValueCount = a.Bindings.Count,
                Installed = true,
                Icon = TryLoadIcon(a),
                Tint = TintFor(a.Id),
                Adapter = a,
                SearchKey = (a.Name + " " + a.Id + " " + a.Engine).ToLowerInvariant()
            });
        }

        var installedIds = installed.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remote != null)
        {
            foreach (var r in remote.Where(r => !installedIds.Contains(r.Id)))
            {
                _items.Add(new Item
                {
                    Id = r.Id,
                    Name = string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name,
                    Sub = "未安装 · 点击下载",
                    Installed = false,
                    Tint = TintFor(r.Id),
                    Remote = r,
                    SearchKey = ((string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name)
                        + " " + r.Id).ToLowerInvariant()
                });
            }
        }

        _filterDirty = true;
        PlayEnter();
    }

    /// <summary>
    /// 设置搜索词。空串表示不过滤。
    ///
    /// 性能：只在词变化时把结果重算一次并缓存。
    /// 逐键过滤的成本是 O(n) 次子串匹配，n 是卡片数（几十个量级），
    /// 不做 ToLower 就不会有额外分配，逐键完全够用。
    /// 真正的风险是每帧绘制时重复过滤——所以缓存必须留在绘制路径之外。
    /// </summary>
    public void SetQuery(string query)
    {
        string q = (query ?? "").Trim().ToLowerInvariant();
        if (q == _query) return;

        _query = q;
        _filterDirty = true;
        _scrollY = 0;
        _scrollTarget = 0;
        Invalidate();
    }

    /// <summary>当前可见的卡片（过滤后）。结果缓存，脏了才重算。</summary>
    private List<Item> FilteredItems()
    {
        if (!_filterDirty && _filtered != null) return _filtered;

        if (_query.Length == 0)
        {
            _filtered = _items;
        }
        else
        {
            var list = new List<Item>(_items.Count);
            foreach (var it in _items)
            {
                if (it.SearchKey.Contains(_query, StringComparison.Ordinal))
                    list.Add(it);
            }
            _filtered = list;
        }

        _filterDirty = false;
        return _filtered;
    }

    /// <summary>
    /// 给每个游戏分配一个稳定的色相。
    /// 用 ID 的哈希而不是随机数：同一个游戏每次打开颜色一致，
    /// 用户靠颜色就能快速定位。
    /// </summary>
    private static Color TintFor(string id)
    {
        int h = 0;
        foreach (char c in id) h = (h * 31 + c) & 0x7FFFFFFF;
        float hue = (h % 360) / 360f;

        // 饱和度与亮度控制在 OriginOS 的甜区：够鲜明但不刺眼
        return FromHsl(hue, 0.62f, 0.56f);
    }

    private static Color FromHsl(float h, float s, float l)
    {
        if (s == 0) { int v = (int)(l * 255); return Color.FromArgb(v, v, v); }

        float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        float p = 2 * l - q;

        float r = HueToRgb(p, q, h + 1f / 3f);
        float g = HueToRgb(p, q, h);
        float b = HueToRgb(p, q, h - 1f / 3f);

        return Color.FromArgb(
            Math.Clamp((int)(r * 255), 0, 255),
            Math.Clamp((int)(g * 255), 0, 255),
            Math.Clamp((int)(b * 255), 0, 255));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1f / 6f) return p + (q - p) * 6 * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
        return p;
    }

    private static Image TryLoadIcon(GameAdapter a)
    {
        try
        {
            string exe = GameLauncher.ResolveExePath(a);
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
            using var ico = Icon.ExtractAssociatedIcon(exe);
            return ico?.ToBitmap();
        }
        catch { return null; }
    }

    // ---------- 动画 ----------

    /// <summary>页面进入：整体淡入 + 卡片错峰上移。</summary>
    public void PlayEnter()
    {
        // 起始不是 0 而是 0.35：动画没跑完时页面也必须是"可读"的。
        // 从 0 起会让首帧完全空白，若动画因任何原因不推进，
        // 用户看到的就是一个空窗口（实测踩过，代价很大）。
        _pageT = 0.35f;
        foreach (var it in _items) it.AppearT = 0.35f;

        int total = Tokens.AnimNormal + _items.Count * Tokens.AnimStagger;

        Animator.Run(this, "enter", total, raw =>
        {
            if (IsDisposed) return false;

            _pageT = Math.Max(0.35f, raw);

            float elapsedMs = raw * total;
            for (int i = 0; i < _items.Count; i++)
            {
                float startMs = i * Tokens.AnimStagger;
                float local = Math.Clamp((elapsedMs - startMs) / Tokens.AnimNormal, 0f, 1f);
                _items[i].AppearT = Math.Max(0.35f, Easing.EaseOut(local));
            }

            Invalidate();
            return true;
        });
    }

    private void AnimateHover(int index, float target)
    {
        if (index < 0 || index >= _items.Count) return;
        var it = _items[index];
        float from = it.HoverT;

        Animator.Run(this, "hover" + index, Tokens.AnimFast, raw =>
        {
            if (IsDisposed) return false;
            it.HoverT = Easing.Lerp(from, target, Easing.EaseOut(raw));
            Invalidate();
            return true;
        });
    }

    private void AnimatePress(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var it = _items[index];

        Animator.Run(this, "press" + index, Tokens.AnimFast, raw =>
        {
            if (IsDisposed) return false;
            // 前半程按下，后半程弹回
            it.PressT = raw < 0.5f
                ? Easing.EaseOut(raw * 2f)
                : 1f - Easing.EaseOut((raw - 0.5f) * 2f);
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

        int pad = Tokens.S5;
        int y = pad;

        y = PaintHeader(g, pad, y);

        // 过滤结果在这里算一次，供分区计数与卡片绘制共用。
        var visible = FilteredItems();
        var visibleInstalled = visible.Where(x => x.Installed).ToList();
        var downloadable = visible.Where(x => !x.Installed).ToList();

        // 计数必须用过滤后的数量。
        // 原来用 _items.Count(x => x.Installed)，搜索时标题写"已安装 (2)"
        // 下面却一张卡都没有，自相矛盾。
        y = PaintSection(g, pad, y, "已安装", visibleInstalled.Count);

        // 卡片网格：列数按可用宽度自适应，卡片宽度等分填满。
        //
        // 之前的实现是固定 300px 宽，宽窗口下右侧留大片空白、
        // 空间利用率很低（用户反馈）。现在改为等分：
        //   窗口宽 -> 列数多，每列仍等分，不留白
        //   窗口窄 -> 列数少，卡片自动变窄，不会挤成一条缝
        int cardH = 140;
        int gap = Tokens.S3 * 2;
        // 列数 = min(按宽度能放下的列数, 实际卡片数)。
        //
        // 只按宽度算列数是个陷阱：1480px 窗口算出 5 列，
        // 但只有 2 个游戏时，2 张卡各占 267px、总共 558px，
        // 右边 874px 全是空白——这正是用户说的"浪费空间"。
        // 用 min() 之后，2 个游戏就是 2 列、每列撑满一半宽度。
        const int MinCardW = 260;
        int colsByWidth = Math.Max(1, (Width - pad * 2 + gap) / (MinCardW + gap));
        int cols = Math.Min(colsByWidth, Math.Max(1, _items.Count));

        int rowY = y - (int)_scrollY;

        // 每个分区用独立索引。
        //
        // 真 bug：原来两个分区共用 index 连续累加，
        // 第二个分区的位置从 index 继续取模，
        // 结果第一张卡被放到第二行的第 N 列，左边空着——
        // 用户看到的就是"第二行左边没东西，内容跑到右边"。
        int installedCount = 0;
        foreach (var it in visibleInstalled)
        {
            LayoutAndPaint(g, it, installedCount++, pad, rowY, cardH, gap, cols);
            if (installedCount % cols == 0) rowY += cardH + gap;
        }
        if (installedCount % cols != 0) rowY += cardH + gap;
        if (downloadable.Count > 0)
        {
            rowY = PaintSection(g, pad, rowY, "可下载", downloadable.Count, ignoreScroll: true);

            int dlCount = 0;
            foreach (var it in downloadable)
            {
                LayoutAndPaint(g, it, dlCount++, pad, rowY, cardH, gap, cols);
                if (dlCount % cols == 0) rowY += cardH + gap;
            }
        }
        else if (!string.IsNullOrEmpty(_remoteError))
        {
            PaintHint(g, pad, rowY, "无法获取下载列表：" + _remoteError);
        }

        // 记录内容总高度，供滚动上限使用。
        // 这里 rowY 是最后一个内容块之后的位置（已含滚动偏移的抵消）。
        _contentH = rowY + (int)_scrollY;

        // 空状态。要区分"一个游戏都没有"和"搜索没匹配到"——
        // 后者提示"换个词试试"比"还没有任何游戏"有用得多。
        if (_items.Count == 0)
            PaintEmpty(g);
        else if (visible.Count == 0)
            PaintNoMatch(g);
    }

    /// <summary>标题 + 副标题 + 右侧主按钮。返回下一个可用 y。</summary>
    private int PaintHeader(Graphics g, int pad, int y)
    {
        float t = Easing.EaseOut(Math.Clamp(_pageT * 3f, 0f, 1f));
        int dy = (int)(10 * (1 - t));

        Draw.TextLeft(g, "游戏库", Tokens.FontTitle,
            Easing.WithAlpha(Tokens.TextPrimary, t),
            new Rectangle(pad, y + dy, Width - pad * 2 - 200, 40));

        Draw.TextLeft(g, "选择一个游戏开始修改。未安装的适配包点卡片下载。",
            Tokens.FontCaption,
            Easing.WithAlpha(Tokens.TextMuted, t),
            new Rectangle(pad, y + 38 + dy, Width - pad * 2 - 200, 22));

        // 右上角不再放"开始使用"按钮。
        // 它没有明确动作（点卡片就是开始），占着视觉重心反而让人以为要先点它。
        // 改成在右侧显示数量概览，信息密度更高也更诚实。
        int installed = _items.Count(x => x.Installed);
        int available = _items.Count - installed;
        if (_items.Count > 0)
        {
            string summary = available > 0
                ? $"已安装 {installed} · 可下载 {available}"
                : $"已安装 {installed}";
            // 数量概览放在搜索框下面一行，避免两者重叠。
            Draw.TextRight(g, summary, Tokens.FontCaption,
                Easing.WithAlpha(Tokens.TextMuted, t),
                new Rectangle(Width - pad - 260, y + 40 + dy, 260, 22));
        }

        return y + 76;
    }

    /// <summary>
    /// 把搜索框贴到头部右上角。
    ///
    /// 放在"数量概览"上方一行，避免和它抢同一块位置。
    /// 卡片区从 y=76 开始，搜索框底边不超过这条线。
    /// </summary>
    private void LayoutSearchBox()
    {
        if (_searchBox == null) return;

        const int pad = 28;
        int x = Math.Max(pad, Width - pad - _searchBox.Width - 12);

        // 与"游戏库"标题同一行（y=8..34），不压住下面的数量概览。
        _searchBox.Location = new Point(x, 10);
    }

    /// <summary>搜索无匹配时的提示。</summary>
    private void PaintNoMatch(Graphics g)
    {
        float t = Easing.EaseOut(Math.Clamp(_pageT * 3f, 0f, 1f));

        string msg = $"没有匹配「{_query}」的游戏";
        Draw.TextCenter(g, msg, Tokens.FontSection,
            Easing.WithAlpha(Tokens.TextSecondary, t),
            new Rectangle(0, Height / 2 - 40, Width, 32));

        Draw.TextCenter(g, "换个关键词试试，或清空搜索框。", Tokens.FontBody,
            Easing.WithAlpha(Tokens.TextMuted, t),
            new Rectangle(0, Height / 2 - 2, Width, 26));
    }

    private int PaintSection(Graphics g, int pad, int y, string title, int count,
                             bool ignoreScroll = false)
    {
        float t = Easing.EaseOut(Math.Clamp(_pageT * 3f, 0f, 1f));

        string text = count > 0 ? $"{title}（{count}）" : title;
        Draw.TextLeft(g, text, Tokens.FontSection,
            Easing.WithAlpha(Tokens.TextSecondary, t),
            new Rectangle(pad, y, Width - pad * 2, 26));

        return y + 34;
    }

    /// <summary>
    /// 布局单张卡片并绘制。
    ///
    /// 卡片宽度按可用空间等分，而不是固定 300px：
    /// 固定宽度在宽窗口下会在右侧留下大片空白（实测 1400px 窗口只用到 900px），
    /// 空间利用率很低。等分后窗口多宽卡片就多宽。
    /// </summary>
    private void LayoutAndPaint(Graphics g, Item it, int index, int pad, int rowY,
                                int cardH, int gap, int cols)
    {
        int col = index % cols;

        // 均分可用宽度，不再设下限兜底。
        // cols 已经不超过实际卡片数，所以这里均分不会把卡片压得过窄。
        int avail = Width - pad * 2;
        int cardW = (avail - gap * (cols - 1)) / cols;

        var rc = new Rectangle(pad + col * (cardW + gap), rowY, cardW, cardH);
        it.Bounds = rc;

        PaintCard(g, it, rc);
    }

    private void PaintCard(Graphics g, Item it, Rectangle rc)
    {
        float t = it.AppearT;
        if (t <= 0.001f) t = 0.35f;   // 兜底：任何情况下卡片都要可见

        // 进入动画：上移 + 淡入；按下时轻微下沉
        int enterDy = (int)(16 * (1 - t));
        int pressDy = (int)(it.PressT * 2f);
        var r = new Rectangle(rc.X, rc.Y + enterDy + pressDy, rc.Width, rc.Height);

        // 阴影只在动画结束后画（每帧重画阴影是自绘界面最容易卡的地方）
        if (t >= 0.99f)
            Draw.Shadow(g, r, Tokens.RadiusCard);

        Draw.Fill(g, r, Tokens.RadiusCard,
            Easing.LerpColor(Tokens.Surface, Tokens.SurfaceHover, it.HoverT * 0.5f));

        // 悬停时加一圈主色描边
        if (it.HoverT > 0.01f)
        {
            Draw.Stroke(g, r, Tokens.RadiusCard,
                Easing.WithAlpha(Tokens.Accent, it.HoverT * 0.6f), 1.6f);
        }

        int p = Tokens.CardPadding;

        // 图标块
        var iconRect = new Rectangle(r.X + p, r.Y + p, Tokens.IconSize, Tokens.IconSize);
        if (it.Icon != null)
        {
            Draw.Fill(g, iconRect, Tokens.RadiusIcon, Tokens.SurfaceAlt);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(it.Icon, iconRect);
        }
        else
        {
            // 无图标：用该游戏的色相做底 + 首字
            Draw.Fill(g, iconRect, Tokens.RadiusIcon,
                Easing.WithAlpha(it.Tint, 0.16f + 0.12f * it.HoverT));
            Draw.TextCenter(g, FirstChar(it.Name), Tokens.FontAt(17f, FontStyle.Bold),
                it.Tint, iconRect);
        }

        // 标题 + 副标题
        int textX = iconRect.Right + Tokens.S3;
        int textW = r.Right - textX - p;

        Draw.TextLeft(g, it.Name, Tokens.FontBodyStrong,
            Easing.WithAlpha(Tokens.TextPrimary, t),
            new Rectangle(textX, r.Y + p - 2, textW, 24));

        Draw.TextLeft(g, it.Sub, Tokens.FontCaption,
            Easing.WithAlpha(Tokens.TextMuted, t),
            new Rectangle(textX, r.Y + p + 22, textW, 20));

        // 底部状态区
        int footY = r.Bottom - p - 30;
        var footRect = new Rectangle(r.X + p, footY, r.Width - p * 2, 30);

        if (it.Installed)
        {
            // 状态点 + 文字
            var dotCenter = new Point(footRect.X + 5, footRect.Y + 15);
            Draw.Dot(g, dotCenter, 4, Tokens.Success, glow: true);
            Draw.TextLeft(g, "已安装", Tokens.FontCaption, Tokens.Success,
                new Rectangle(footRect.X + 16, footRect.Y, footRect.Width - 16, 30));
        }
        else
        {
            Draw.TextLeft(g, "未安装", Tokens.FontCaption, Tokens.TextMuted,
                new Rectangle(footRect.X, footRect.Y, footRect.Width - 84, 30));

            var dl = new Rectangle(footRect.Right - 76, footRect.Y + 3, 76, 26);
            Draw.Fill(g, dl, Tokens.RadiusPill, Tokens.AccentSoft);
            Draw.TextCenter(g, "下载", Tokens.FontCaption, Tokens.Accent, dl);
        }
    }

    private static string FirstChar(string s)
        => string.IsNullOrWhiteSpace(s) ? "?" : s.Trim()[..1].ToUpperInvariant();

    private void PaintHint(Graphics g, int pad, int y, string text)
    {
        Draw.TextLeft(g, text, Tokens.FontCaption, Tokens.Warning,
            new Rectangle(pad, y, Width - pad * 2, 24));
    }

    private void PaintEmpty(Graphics g)
    {
        // 空状态必须无条件可见，不挂动画进度（理由见 GameView.PaintEmpty）。
        const float t = 1f;
        var r = new Rectangle(0, Height / 2 - 60, Width, 40);

        Draw.TextCenter(g, "还没有安装任何适配包",
            Tokens.FontSection, Easing.WithAlpha(Tokens.TextSecondary, t), r);

        Draw.TextCenter(g, "连上网络后这里会列出可下载的游戏",
            Tokens.FontCaption, Easing.WithAlpha(Tokens.TextMuted, t),
            new Rectangle(0, Height / 2 - 20, Width, 24));
    }

    // ---------- 交互 ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int found = HitTest(e.Location);
        if (found == _hover) return;

        if (_hover >= 0) AnimateHover(_hover, 0f);
        _hover = found;
        if (_hover >= 0) AnimateHover(_hover, 1f);

        Cursor = found >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0) AnimateHover(_hover, 0f);
        _hover = -1;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int idx = HitTest(e.Location);
        if (idx < 0) return;

        _press = idx;
        AnimatePress(idx);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        // 右键已安装卡片 → 弹上下文菜单。
        //
        // 原来是直接卸载，没有确认、也没有其它选项——
        // 一次误触就把适配包删了。而且"设置游戏路径"这类操作
        // 也应该能从这里进，不该只有卸载一个动作。
        if (e.Button == MouseButtons.Right)
        {
            int ri = HitTest(e.Location);
            if (ri >= 0 && _items[ri].Installed)
                ShowCardMenu(_items[ri], e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        int idx = HitTest(e.Location);
        if (idx < 0 || idx != _press) { _press = -1; return; }
        _press = -1;

        var it = _items[idx];

        if (it.Installed) GameSelected?.Invoke(it.Adapter);
        else if (it.Remote != null) DownloadRequested?.Invoke(it.Remote);
    }

    /// <summary>
    /// 已安装卡片的右键菜单：设置路径 / 卸载。
    /// </summary>
    private void ShowCardMenu(Item item, Point at)
    {
        var menu = new ContextMenuStrip { Font = Tokens.FontBody };

        menu.Items.Add("设置游戏路径…", null, (_, _) =>
            LocateExeRequested?.Invoke(item.Id));

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("卸载适配包", null, (_, _) =>
            UninstallRequested?.Invoke(item.Id));

        menu.Show(this, at);
    }

    /// <summary>命中测试。自绘控件没有子控件，必须自己算。</summary>
    private int HitTest(Point p)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Bounds.Contains(p)) return i;
        return -1;
    }

    /// <summary>滚轮滚动（内容超出一屏时）。</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // 滚动必须有上限。
        //
        // 真 bug：原来只 Math.Max(0, ...) 夹了下界，没有上界，
        // 一直往下滚能把内容整个推出可视区——用户看到的就是"卡片上天了"，
        // 剩下一片空白，而且滚不回来（因为没有参照物）。
        //
        // 上限 = 内容总高 - 可视高度。内容不足一屏时上限为 0，完全不能滚。
        int maxScroll = Math.Max(0, _contentH - ClientSize.Height);
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta, 0, maxScroll);
        float from = _scrollY;

        Animator.Run(this, "scroll", Tokens.AnimFast, raw =>
        {
            if (IsDisposed) return false;
            _scrollY = Easing.Lerp(from, _scrollTarget, Easing.EaseOut(raw));
            Invalidate();
            return true;
        });
    }
}
