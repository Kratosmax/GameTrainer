using GameTrainer.Adapter;
using Item = GameTrainer.Adapter.AdapterRunner.CollectionItem;

namespace GameTrainer.UI;

/// <summary>
/// 集合卡片：一张卡片展示「一个集合语义」的全部条目，每行一个条目。
///
/// 与 ValueCard 的区别：ValueCard 是「一个语义 = 一个数值」，
/// 而员工好感度这类数据是「一个语义 = N 个条目，N 会随游戏进度变化」。
///
/// 布局用 TableLayoutPanel 而不是手工计算 Left/Width：
/// 手工定位有两个实测缺陷——
///   1. SetItems 时控件宽度还是 0，RelayoutRows 提前返回，按钮留在硬编码坐标被裁掉
///   2. 窗口拉伸时每帧重算全部控件位置，肉眼可见卡顿
/// TableLayoutPanel 用列约束自动分配宽度，两个问题都不存在。
/// </summary>
internal sealed class CollectionCard : Panel
{
    /// <summary>双击条目名称，请求打开该条目的补充设置。</summary>
    public event Action<string, int, string> EntrySettingsRequested;

    /// <summary>用户点了某一行的「应用」：语义 + 条目ID + 新值。</summary>
    public event Action<string, int, int> ApplyRequested;

    private readonly string _semantic;
    private readonly Label _title = new();
    private readonly Label _summary = new();
    private readonly TableLayoutPanel _rows = new();
    private readonly List<(int Id, TextBox Input)> _inputs = new();

    private const int RowHeight = 34;
    private const int ColValue = 64;
    private const int ColInput = 72;
    private const int ColButton = 56;

    public CollectionCard(string semantic, string displayName)
    {
        _semantic = semantic;
        BackColor = Tokens.Surface;
        Padding = new Padding(Tokens.S4, Tokens.S3, Tokens.S4, Tokens.S3);
        Margin = new Padding(Tokens.S2);
        Dock = DockStyle.Fill;
        MinimumSize = new Size(300, 120);

        _title.Text = displayName;
        _title.Font = Tokens.FontSection;
        _title.ForeColor = Tokens.TextPrimary;
        _title.AutoSize = true;
        _title.Dock = DockStyle.Top;
        _title.Padding = new Padding(0, 0, 0, 2);

        _summary.Font = Tokens.FontCaption;
        _summary.ForeColor = Tokens.TextMuted;
        _summary.AutoSize = true;
        _summary.Dock = DockStyle.Top;
        _summary.Padding = new Padding(0, 0, 0, Tokens.S2);

        _rows.Dock = DockStyle.Top;
        _rows.AutoSize = true;
        _rows.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _rows.BackColor = Tokens.Surface;
        _rows.ColumnCount = 4;
        _rows.RowCount = 0;
        _rows.Margin = new Padding(0);
        _rows.Padding = new Padding(0);
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColValue));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColInput));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColButton));

        // Dock=Top 会按加入顺序自下而上堆叠，所以倒序加入
        Controls.Add(_rows);
        Controls.Add(_summary);
        Controls.Add(_title);
    }

    /// <summary>填充/刷新条目。会重建所有行。</summary>
    public void SetItems(List<Item> items, bool ok, string error)
    {
        _rows.SuspendLayout();
        try
        {
            _rows.Controls.Clear();
            _rows.RowStyles.Clear();
            _rows.RowCount = 0;
            _inputs.Clear();

            if (!ok)
            {
                _summary.Text = error ?? "读取失败";
                _summary.ForeColor = Tokens.Danger;
                return;
            }

            _summary.Text = $"{items.Count} 个条目";
            _summary.ForeColor = Tokens.TextMuted;

            foreach (var it in items)
            {
                int r = _rows.RowCount;
                _rows.RowCount = r + 1;
                _rows.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));

                var name = new Label
                {
                    Text = it.Name,
                    Font = Tokens.FontBody,
                    ForeColor = Tokens.TextPrimary,
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0, 0, Tokens.S2, 0),
                    Cursor = Cursors.Hand
                };
                name.DoubleClick += (_, _) => EntrySettingsRequested?.Invoke(_semantic, it.Id, it.Name);

                var value = new Label
                {
                    Text = it.Value,
                    Font = Tokens.FontBodyStrong,
                    ForeColor = Tokens.Accent,
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleRight,
                    Margin = new Padding(0, 0, Tokens.S2, 0)
                };

                var input = new TextBox
                {
                    Dock = DockStyle.Fill,
                    BorderStyle = BorderStyle.FixedSingle,
                    Margin = new Padding(0, 5, Tokens.S2, 5)
                };

                int capturedId = it.Id;
                var btn = new FlatButton
                {
                    Text = "应用",
                    Kind = ButtonKind.Secondary,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 3, 0, 3)
                };
                btn.Click += (_, _) =>
                {
                    if (!int.TryParse(input.Text.Trim(), out int v))
                    {
                        _summary.Text = "请输入整数";
                        _summary.ForeColor = Tokens.Danger;
                        return;
                    }
                    ApplyRequested?.Invoke(_semantic, capturedId, v);
                };

                _rows.Controls.Add(name, 0, r);
                _rows.Controls.Add(value, 1, r);
                _rows.Controls.Add(input, 2, r);
                _rows.Controls.Add(btn, 3, r);
                _inputs.Add((capturedId, input));
            }
        }
        finally
        {
            _rows.ResumeLayout(true);
        }

        AnimateRowsIn();
    }

    /// <summary>
    /// 行渐次淡入（staggered fade-in）。
    ///
    /// iOS 列表加载时的错峰出现让"数据到齐"这件事有过程感，
    /// 而不是一坨内容突然砸在屏幕上。这里每行延迟 18ms，总时长不超过 260ms。
    /// 只改前景色透明度对应的 alpha，不做布局，性能开销可忽略。
    /// </summary>
    private void AnimateRowsIn()
    {
        var labels = new List<Label>();
        foreach (Control c in _rows.Controls)
            if (c is Label l) labels.Add(l);

        if (labels.Count == 0) return;

        Color baseColor = Tokens.TextPrimary;
        Color mutedColor = Tokens.TextMuted;

        Animator.Run(this, "rows-in", Tokens.AnimNormal + labels.Count * 18, raw =>
        {
            if (IsDisposed) return false;

            float total = Tokens.AnimNormal + labels.Count * 18;

            for (int i = 0; i < labels.Count; i++)
            {
                // 每行自己的进度窗口
                float startMs = i * 18f;
                float local = Math.Clamp((raw * total - startMs) / Tokens.AnimNormal, 0f, 1f);
                float t = Easing.EaseOut(local);

                var l = labels[i];
                bool isValue = l.TextAlign == ContentAlignment.MiddleRight;
                Color from = isValue ? Tokens.Accent : baseColor;

                // 从 30% 不透明渐变到全不透明
                l.ForeColor = Color.FromArgb(
                    (int)(30 + 225 * t),
                    from.R, from.G, from.B);
            }

            return true;
        });
    }

    /// <summary>
    /// 卡片需要的高度（内容真实需求）。
    ///
    /// 不读 Control.Height：TableLayoutPanel 会反过来钳制子控件高度，
    /// 读到的永远是上一次布局的值（实测算出 428 读回 174），
    /// 用它设行高会永远差一截，表现为 10 个员工只显示 3 行。
    /// </summary>
    public int RequiredHeight
    {
        get
        {
            int content = _title.PreferredHeight + 2
                        + _summary.PreferredHeight + Tokens.S2
                        + _rows.RowCount * RowHeight;
            return Padding.Vertical + content + Tokens.S3;
        }
    }

    /// <summary>第一个输入框的当前内容（热键触发时取目标值）。</summary>
    public string FirstInputText =>
        _inputs.Count > 0 ? _inputs[0].Input.Text.Trim() : "";

    /// <summary>写回成功后清空该行的输入框。</summary>
    public void ClearInputFor(int id)
    {
        foreach (var (rowId, input) in _inputs)
            if (rowId == id) input.Text = "";
    }
}
