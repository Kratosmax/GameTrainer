using GameTrainer.Adapter;
using Item = GameTrainer.Adapter.AdapterRunner.CollectionItem;

namespace GameTrainer.UI;

/// <summary>
/// 集合卡片：一张卡片展示「一个集合语义」的全部条目，每行一个条目。
///
/// 与 ValueCard 的区别：ValueCard 是「一个语义 = 一个数值」，
/// 而员工好感度这类数据是「一个语义 = N 个条目，N 会随游戏进度变化」。
/// 塞进 ValueCard 只能显示"11 个条目"这样的汇总，用户拿不到具体数值。
///
/// 布局：每行 = 条目名 + 当前值 + 输入框 + 应用按钮。
/// 条目增减时整张卡片重建，不需要改适配包。
/// </summary>
internal sealed class CollectionCard : Panel
{
    /// <summary>用户点了某一行的「应用」：条目ID + 新值。</summary>
    public event Action<string, int, int> ApplyRequested;

    private readonly string _semantic;
    private readonly Label _title = new();
    private readonly Label _summary = new();
    private readonly Panel _rows = new();
    private readonly List<(int Id, TextBox Input)> _inputs = new();

    public CollectionCard(string semantic, string displayName)
    {
        _semantic = semantic;
        BackColor = Tokens.Surface;
        Padding = new Padding(Tokens.S4);
        Margin = new Padding(Tokens.S2);

        // 用 Top 而非 Fill：Fill 会强制把 Height 撑满单元格，
        // 导致 SetItems 算出的行高被覆盖，10 个员工只显示 3 行。
        // 宽度靠表格列宽自动拉伸（Anchor 左右），高度由内容决定。
        Dock = DockStyle.None;

        _title.Text = displayName;
        _title.Font = Tokens.FontSection;
        _title.ForeColor = Tokens.TextPrimary;
        _title.AutoSize = true;
        _title.Location = new Point(Tokens.S4, Tokens.S3);

        _summary.Font = Tokens.FontCaption;
        _summary.ForeColor = Tokens.TextMuted;
        _summary.AutoSize = true;
        _summary.Location = new Point(Tokens.S4, Tokens.S3 + 26);

        _rows.Location = new Point(Tokens.S4, Tokens.S3 + 50);
        _rows.BackColor = Tokens.Surface;
        // 行容器随卡片宽度伸缩；高度由 SetItems 按条目数计算。
        _rows.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        Controls.Add(_title);
        Controls.Add(_summary);
        Controls.Add(_rows);

        // 卡片宽度由表格列宽决定，行容器要跟着变；同时把行内按钮重新定位到右侧。
        Resize += (_, _) => RelayoutRows();
    }

    /// <summary>填充/刷新条目。会重建所有行。</summary>
    public void SetItems(List<Item> items, bool ok, string error)
    {
        _rows.Controls.Clear();
        _inputs.Clear();

        if (!ok)
        {
            _summary.Text = error ?? "读取失败";
            _summary.ForeColor = Tokens.Danger;
            Height = 110;
            return;
        }

        _summary.Text = $"{items.Count} 个条目";
        _summary.ForeColor = Tokens.TextMuted;

        int y = 0;
        foreach (var it in items)
        {
            // 每行用「名字伸缩 + 值/输入/按钮右锚定」：
            // 卡片宽度由列宽决定（可能 300~500px 不等），固定坐标会错位。
            var name = new Label
            {
                Text = it.Name,
                Font = Tokens.FontBody,
                ForeColor = Tokens.TextPrimary,
                AutoSize = false,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(0, y),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var value = new Label
            {
                Text = it.Value,
                Font = Tokens.FontBodyStrong,
                ForeColor = Tokens.Accent,
                AutoSize = false,
                Width = 60,
                Height = 30,
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(134, y),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            var input = new TextBox
            {
                Width = 70,
                Location = new Point(200, y + 3),
                Text = "",
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            int capturedId = it.Id;
            var btn = new FlatButton
            {
                Text = "应用",
                Kind = ButtonKind.Secondary,
                Width = 56,
                Height = 28,
                Location = new Point(276, y + 1),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
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

            _rows.Controls.Add(name);
            _rows.Controls.Add(value);
            _rows.Controls.Add(input);
            _rows.Controls.Add(btn);
            _inputs.Add((it.Id, input));

            y += 34;
        }

        _rows.Height = Math.Max(y, 1);

        // 不要设 MinimumSize/MaximumSize，也不要依赖这里的 Height：
        // TableLayoutPanel 会在下一次布局把子控件高度改回行高，
        // 任何锁定都只会把卡片压在旧行高上（实测锁 174 后永远只显示 3 行）。
        // 真实需求通过 RequiredHeight 暴露给网格，由网格设行高，卡片自然撑开。
        Height = RequiredHeight - Tokens.S4;

        // 行控件建好后按当前宽度摆一次；之后宽度变化由 Resize 再摆。
        RelayoutRows();
    }

    /// <summary>
    /// 按当前卡片宽度重新摆放每一行的控件。
    ///
    /// 为什么不用 Anchor：TableLayoutPanel 会反复触发布局，
    /// Anchor 与手动 Location 混用时控件会跳到错误位置（实测按钮消失）。
    /// 改用纯手动计算，逻辑简单且可预测。
    /// </summary>
    private void RelayoutRows()
    {
        int w = ClientSize.Width - Padding.Horizontal;
        if (w <= 0) return;

        _rows.Width = w;

        // 紧凑排布：卡片宽度只有 280~400px，四个控件挤在一行，
        // 原来 input 70 + btn 56 会吃掉一半宽度，名字只剩 86px（显示成 #100）。
        // 收紧到 input 56 / btn 48 / value 48，把余量留给名字。
        const int btnW = 48, inputW = 56, valueW = 48, gap = 4;
        int btnX = w - btnW;
        int inputX = btnX - gap - inputW;
        int valueX = inputX - gap - valueW;

        foreach (Control c in _rows.Controls)
        {
            if (c is FlatButton b)
            {
                b.Left = btnX;
                b.Width = btnW;
            }
            else if (c is TextBox t)
            {
                t.Left = inputX;
                t.Width = inputW;
            }
            else if (c is Label l)
            {
                if (l.TextAlign == ContentAlignment.MiddleRight)
                {
                    l.Left = valueX;
                    l.Width = valueW;
                }
                else
                {
                    l.Left = 0;
                    l.Width = Math.Max(valueX - gap, 40);
                }
            }
        }
    }

    /// <summary>
    /// 卡片需要的高度（内容真实需求）。
    ///
    /// 不能直接读 Control.Height：TableLayoutPanel 的行高会反过来钳制子控件，
    /// 造成 Height 永远是上一次布局的值（实测算出 428，读回 174），
    /// 用它算行高就会永远差一截，表现为 10 个员工只显示 3 行。
    /// </summary>
    public int RequiredHeight =>
        _inputs.Count > 0 || _rows.Controls.Count > 0
            ? Tokens.S3 + 50 + _rows.Height + Tokens.S4 + 10
            : Height;

    /// <summary>写回成功后更新某行的显示值。</summary>
    public void UpdateValue(int id, int value)
    {
        foreach (Control c in _rows.Controls)
        {
            if (c is Label l && l.TextAlign == ContentAlignment.MiddleRight)
            {
                // 靠行位置匹配：名字 Label 与该值 Label 同一 y
                foreach (Control n in _rows.Controls)
                {
                    if (n is Label nl && nl.Location.Y == l.Location.Y && nl.TextAlign == ContentAlignment.MiddleLeft)
                    {
                        // 通过输入框反查是哪一行
                        break;
                    }
                }
            }
        }

        // 简化：整卡片重读更可靠，这里只把对应输入框清空表示已提交
        foreach (var (rowId, input) in _inputs)
            if (rowId == id) input.Text = "";
    }
}
