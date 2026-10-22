using System.Drawing;
using System.Drawing.Drawing2D;

namespace GameTrainer.UI;

/// <summary>
/// 数值卡片：界面上一个可修改的数值。
///
/// 按 style 呈现四种交互（规则由使用者定义）：
///   fill    主按钮「回满」，手动设置收在折叠区   —— 血量、能量累积
///   step    +/- 步进按钮，手动设置收在折叠区     —— 攻击、魔法、能量、背包
///   number  输入框 + 应用按钮                   —— 金币
///   toggle  开关，可选「持久」                  —— 护盾
///
/// 设计约束（来自 ui-design.md）：
///   - 行高固定，动态状态只改颜色与文字，不撑坏布局
///   - 折叠区展开时不改变卡片外框，只纵向生长
///   - 长文本尾部省略 + 悬停完整提示
/// </summary>
internal sealed class ValueCard : Panel
{
    /// <summary>交互样式。</summary>
    public enum Style { Fill, Step, Number, Toggle }

    /// <summary>用户操作意图。</summary>
    public event Action<string, double> ApplyRequested;      // 语义, 目标值
    public event Action<string> FillRequested;               // 语义（回满）
    public event Action<string, bool, bool> ToggleRequested; // 语义, 是否开启, 是否持久

    private readonly Label _name = new();
    private readonly Label _value = new();
    private readonly Label _hint = new();
    private readonly Label _warning = new();
    private readonly Panel _body = new();
    private readonly Panel _fold = new();
    private readonly TextBox _input = new();

    /// <summary>
    /// 主输入框的当前内容，供热键触发时读取目标值。
    /// 优先取 _numInput（number 样式的输入框），回退到 _input（其它样式）。
    /// </summary>
    public string CurrentInputText
    {
        get
        {
            string v = _numInput.Text.Trim();
            return v.Length > 0 ? v : _input.Text.Trim();
        }
    }
    private readonly FlatButton _applyBtn = new();
    private readonly FlatButton _foldBtn = new();
    private readonly FlatButton _fillBtn = new();
    private readonly TextBox _numInput = new();
    private readonly FlatButton _numApply = new();
    private readonly FlatButton _minusBtn = new();
    private readonly FlatButton _plusBtn = new();
    private readonly ToggleSwitch _toggle = new();
    private readonly CheckBox _persist = new();
    private readonly ToolTip _tip = new();

    private double _current;
    private bool _hasValue;
    private bool _expanded;
    private bool _busy;
    private bool _isOn;

    public string Semantic { get; }
    public Style CardStyle { get; }
    public double[] Range { get; }
    public double StepSize { get; }
    public string FillFrom { get; }
    public bool AllowPersist { get; }
    public double OnValue { get; }

    public ValueCard(string semantic, string displayName, Style style,
                     double[] range = null, double step = 1, string fillFrom = null,
                     bool allowPersist = false, double onValue = 1, string warning = null)
    {
        Semantic = semantic;
        CardStyle = style;
        Range = range;
        StepSize = step <= 0 ? 1 : step;
        FillFrom = fillFrom;
        AllowPersist = allowPersist;
        OnValue = onValue;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Tokens.Surface;
        Height = 150;

        _name.Text = displayName;
        _name.Font = Tokens.FontBodyStrong;
        _name.ForeColor = Tokens.TextPrimary;
        _name.AutoSize = false;
        _name.TextAlign = ContentAlignment.MiddleLeft;

        _value.Text = "—";
        _value.Font = Tokens.FontTitle;
        _value.ForeColor = Tokens.Accent;
        _value.AutoSize = false;
        _value.TextAlign = ContentAlignment.MiddleLeft;

        _hint.Font = Tokens.FontCaption;
        _hint.ForeColor = Tokens.TextSecondary;
        _hint.AutoSize = false;
        _hint.AutoEllipsis = true;
        _hint.TextAlign = ContentAlignment.MiddleLeft;

        _warning.Font = Tokens.FontCaption;
        _warning.ForeColor = Tokens.Warning;
        _warning.AutoSize = false;
        _warning.AutoEllipsis = true;
        _warning.TextAlign = ContentAlignment.MiddleLeft;
        _warning.Text = warning ?? "";
        _warning.Visible = !string.IsNullOrEmpty(warning);

        BuildBody();

        Controls.Add(_name);
        Controls.Add(_value);
        Controls.Add(_hint);
        Controls.Add(_warning);
        Controls.Add(_body);
        Controls.Add(_fold);
    }

    private void BuildBody()
    {
        _body.BackColor = Tokens.Surface;
        _body.Height = 30;

        switch (CardStyle)
        {
            case Style.Fill:
                _fillBtn.Text = "回满";
                _fillBtn.Kind = ButtonKind.Primary;
                _fillBtn.Width = 76;
                _fillBtn.Click += (_, _) => { if (!_busy) FillRequested?.Invoke(Semantic); };
                _body.Controls.Add(_fillBtn);
                break;

            case Style.Step:
                _minusBtn.Text = "−";
                _minusBtn.Width = 36;
                _minusBtn.Click += (_, _) => StepBy(-StepSize);
                _plusBtn.Text = "+";
                _plusBtn.Width = 36;
                _plusBtn.Click += (_, _) => StepBy(+StepSize);
                _body.Controls.Add(_minusBtn);
                _body.Controls.Add(_plusBtn);
                break;

            case Style.Toggle:
                _toggle.Width = 46;
                _toggle.Height = 24;
                _toggle.CheckedChanged += (_, _) =>
                {
                    if (_busy) return;
                    _isOn = _toggle.Checked;
                    ToggleRequested?.Invoke(Semantic, _isOn, _persist.Checked);
                    UpdateHint();
                };
                _body.Controls.Add(_toggle);

                if (AllowPersist)
                {
                    _persist.Text = "持久";
                    _persist.Font = Tokens.FontCaption;
                    _persist.ForeColor = Tokens.TextSecondary;
                    _persist.AutoSize = true;
                    _persist.BackColor = Tokens.Surface;
                    _persist.Margin = new Padding(Tokens.S2, 0, 0, 0);
                    _persist.CheckedChanged += (_, _) =>
                    {
                        // 开关未开时勾选/取消「持久」只记录意图，不触发写入——
                        // 否则会走 ToggleValue(semantic, false, persist) 把游戏值立即写成 0，
                        // 用户「先勾持久再打开关」的常见顺序会瞬间清空护盾
                        if (_busy || !_isOn) return;
                        ToggleRequested?.Invoke(Semantic, _isOn, _persist.Checked);
                        UpdateHint();
                    };
                    _body.Controls.Add(_persist);
                }
                break;

            case Style.Number:
            default:
                // number 样式：主体区直接放输入框 + 应用按钮（不折叠）。
                // 金币、上限这类值没有「回满」或「步进」的自然语义，手输最直接。
                _numInput.Font = Tokens.FontBody;
                _numInput.BorderStyle = BorderStyle.None;
                // 必须给不同底色，否则 BorderStyle=None 的输入框在白卡片上完全看不见（实测踩过）
                _numInput.BackColor = Tokens.SurfaceSunken;
                _numInput.ForeColor = Tokens.TextPrimary;
                _numInput.TextAlign = HorizontalAlignment.Center;
                _numApply.Text = "应用";
                _numApply.Kind = ButtonKind.Primary;
                _numApply.Width = 68;
                _numApply.Click += (_, _) => ApplyNumberInput();
                _body.Controls.Add(_numInput);
                _body.Controls.Add(_numApply);
                break;
        }

        // 折叠按钮：Fill / Step / Toggle 都有（Toggle 只有持久选项时不显示）
        if (CardStyle != Style.Number)
        {
            _foldBtn.Text = "手动设置 ▾";
            _foldBtn.Kind = ButtonKind.Ghost;
            _foldBtn.Width = 104;
            _foldBtn.Click += (_, _) =>
            {
                _expanded = !_expanded;
                _foldBtn.Text = _expanded ? "收起 ▴" : "手动设置 ▾";
                _fold.Visible = _expanded;
                Relayout();
            };
            _body.Controls.Add(_foldBtn);
        }

        // 折叠区：输入框 + 应用
        _fold.BackColor = Tokens.SurfaceSunken;
        _fold.Visible = false;
        _fold.Height = 38;

        _input.Font = Tokens.FontBody;
        _input.BorderStyle = BorderStyle.None;
        _input.BackColor = Tokens.Surface;
        _input.ForeColor = Tokens.TextPrimary;
        _input.TextAlign = HorizontalAlignment.Center;

        _applyBtn.Text = "应用";
        _applyBtn.Kind = ButtonKind.Primary;
        _applyBtn.Width = 68;
        _applyBtn.Click += (_, _) => ApplyInput();

        _fold.Controls.Add(_input);
        _fold.Controls.Add(_applyBtn);
    }

    private void StepBy(double delta)
    {
        if (_busy) { ShowBusy(); return; }
        if (!_hasValue) { ShowNoValue(); return; }
        double target = _current + delta;
        if (Range is { Length: 2 })
            target = Math.Max(Range[0], Math.Min(Range[1], target));
        ApplyRequested?.Invoke(Semantic, target);
    }

    /// <summary>number 样式的应用按钮：读专用输入框。</summary>
    private void ApplyNumberInput()
    {
        if (_busy) { ShowBusy(); return; }
        if (!double.TryParse(_numInput.Text.Trim(), out double v))
        {
            ShowInputError(_numInput.Text);
            return;
        }
        if (Range is { Length: 2 } && (v < Range[0] || v > Range[1]))
        {
            _hint.Text = $"超出范围 {Range[0]}–{Range[1]}";
            _hint.ForeColor = Tokens.Danger;
            return;
        }
        ApplyRequested?.Invoke(Semantic, v);
    }

    private void ApplyInput()
    {
        if (_busy) { ShowBusy(); return; }
        if (!double.TryParse(_input.Text.Trim(), out double v))
        {
            ShowInputError(_input.Text);
            return;
        }
        if (Range is { Length: 2 } && (v < Range[0] || v > Range[1]))
        {
            _hint.Text = $"超出范围 {Range[0]}–{Range[1]}";
            _hint.ForeColor = Tokens.Danger;
            return;
        }
        ApplyRequested?.Invoke(Semantic, v);
    }

    // ---- V6：静默失败改为可见反馈 ----
    // 原先 _busy / 解析失败 / 无值三种情况都直接 return，用户点了没反应，
    // 容易误判成"工具坏了"然后反复点击。这里统一给 hint 一句明确原因。

    /// <summary>
    /// 忙碌提示。
    ///
    /// 说明：_busy 只在 GUI 线程同步执行 SetValue 期间为 true（毫秒级），
    /// 用户点击落不进那个窗口，所以这条分支**实际不可达**。
    /// 保留它是因为写真实异步写入后它会立刻变可达——那时不需要回来补代码。
    /// 真正的"写入进行中"反馈在 GuiForm 的 _writing 防重入里（走 Log）。
    /// </summary>
    private void ShowBusy()
    {
        _hint.Text = "处理中，请稍候…";
        _hint.ForeColor = Tokens.TextSecondary;
    }

    private void ShowInputError(string raw)
    {
        _hint.Text = string.IsNullOrWhiteSpace(raw) ? "请先输入数值" : $"「{raw}」不是有效数字";
        _hint.ForeColor = Tokens.Danger;
    }

    private const string NoValueSuffix = "（先刷新再操作）";

    private void ShowNoValue()
    {
        // 必须幂等：连续点击步进按钮会反复调用这里，而 _hint.Text 是自己上一次的产物。
        // 若不做判断，每点一次就追加一层后缀——实测连点会变成
        // 「不在此场景（先刷新再操作）（先刷新再操作）（先刷新再操作）…」把提示撑爆。
        string why = _hint.Text ?? "";
        if (why.EndsWith(NoValueSuffix, StringComparison.Ordinal))
        {
            // 已经是这条提示了，不重复追加
            _hint.ForeColor = Tokens.Warning;
            return;
        }

        _hint.Text = string.IsNullOrWhiteSpace(why)
            ? "当前无可用值，请先刷新"
            : why + NoValueSuffix;
        _hint.ForeColor = Tokens.Warning;
    }

    /// <summary>设置当前值。成功时更新显示，失败时显示错误。</summary>
    /// <summary>
    /// 设置卡片显示。
    /// </summary>
    /// <param name="neutral">
    /// true 表示这不是"出错"，只是当前条件不满足（如游戏没启动）。
    /// 用灰色而非橙色：橙/红会让整屏看起来像崩了，而用户只是还没开游戏。
    /// </param>
    public void SetValue(string raw, bool ok, string error = null, bool neutral = false)
    {
        _busy = true;
        try
        {
            if (!ok)
            {
                // 用弱化的「—」占住数值位，把原因放在下面小字里。
                // 一开始把「读取失败」写成大号红字，10 张卡片同时变红非常刺眼，
                // 而且把使用者最想看的数值位置占满了（实测截图确认）。
                _value.Text = "—";
                _value.ForeColor = Tokens.TextDisabled;
                _hasValue = false;
                _hint.Text = error ?? "无法读取";
                _hint.ForeColor = neutral ? Tokens.TextMuted : Tokens.Warning;
                SetControlsEnabled(false);
                return;
            }

            _value.ForeColor = Tokens.Accent;

            if (double.TryParse(raw, out double d))
            {
                _current = d;
                _hasValue = true;
                _value.Text = FormatValue(d);
                if (CardStyle == Style.Number) _numInput.Text = FormatValue(d);
                SetControlsEnabled(true);
            }
            else
            {
                _hasValue = false;
                _value.Text = raw;
                SetControlsEnabled(true);
            }

            if (CardStyle == Style.Toggle)
            {
                _isOn = _hasValue && Math.Abs(_current - OnValue) < 1e-9;
                _toggle.Checked = _isOn;
            }

            UpdateHint();
        }
        finally { _busy = false; }
    }

    private static string FormatValue(double d)
        => Math.Abs(d - Math.Round(d)) < 1e-9 ? ((long)Math.Round(d)).ToString() : d.ToString("0.##");

    private void SetControlsEnabled(bool on)
    {
        foreach (Control c in _body.Controls) c.Enabled = on;
        _applyBtn.Enabled = on;
        _input.Enabled = on;
    }

    private void UpdateHint()
    {
        if (!_hasValue) return;
        _hint.ForeColor = Tokens.TextSecondary;

        switch (CardStyle)
        {
            case Style.Toggle:
                if (_isOn && _persist.Checked)
                    _hint.Text = "已套盾 · 持久模式（盾消失会自动补回，退出对局自动停止）";
                else if (_isOn)
                    _hint.Text = "已套盾";
                else
                    _hint.Text = "未套盾";
                break;

            case Style.Fill when FillFrom != null:
                _hint.Text = $"回满会把该值补到 {FillFrom} 的当前值";
                break;

            case Style.Step when Range is { Length: 2 }:
                _hint.Text = $"范围 {Range[0]}–{Range[1]}";
                break;

            default:
                _hint.Text = "";
                break;
        }
    }

    /// <summary>重新排布子控件。卡片宽度由外层网格决定。</summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    private void Relayout()
    {
        int w = Width - Tokens.S4 * 2;
        if (w < 60) return;

        const int pad = Tokens.S4;
        int y = Tokens.S3;

        _name.SetBounds(pad, y, w, 20);
        y += 20;
        _value.SetBounds(pad, y, w, 30);
        y += 34;

        // 主体控件（按钮/开关）
        _body.SetBounds(pad, y, w, 32);
        LayoutBody();
        y += 36;

        if (_warning.Visible)
        {
            _warning.SetBounds(pad, y, w, 18);
            y += 20;
        }

        _hint.SetBounds(pad, y, w, 34);
        y += 34;

        if (_expanded)
        {
            _fold.SetBounds(pad, y, w, 38);
            _input.SetBounds(0, 4, Math.Max(40, w - 76), 30);
            _applyBtn.SetBounds(w - 72, 3, 68, 32);
            y += 42;
        }

        // 底部留白 + 固定最小高度：同一排卡片视觉对齐，
        // 不因有无 warning / hint 而参差（ui-design.md：动态状态不得改变行高）
        Height = Math.Max(y + Tokens.S3, 168);

        // 悬停提示必须在这里挂：只有布局完成、控件拿到最终宽度后，
        // 才能判断文本是否真的被省略号截断。放在构造期会因宽度未定而误判。
        RefreshToolTips();
    }

    /// <summary>
    /// 给可能被 Ellipsis 截断的文本挂 ToolTip。
    ///
    /// 挂载对象只有 hint 与 warning：
    ///   - _name 是固定短标签，不会截断
    ///   - _value 是数值，不会截断
    ///   - _hint / _warning 都是 AutoEllipsis = true，且 warning 来自适配包
    ///     b.Warning（作者自定义，长度不受控，实际就有 20+ 字的）
    ///
    /// 只在真的被截断时才挂：否则每张卡片悬停都弹提示，反而干扰。
    /// </summary>
    private void RefreshToolTips()
    {
        AttachTipIfTruncated(_hint);
        AttachTipIfTruncated(_warning);
    }

    private void AttachTipIfTruncated(Label label)
    {
        if (label == null) return;
        string text = label.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _tip.SetToolTip(label, null);
            return;
        }

        // TextRenderer.MeasureText 按控件实际宽度与字体测量：
        // 超过可用宽度才认为会被省略号吃掉。
        Size proposed = new(label.ClientSize.Width, int.MaxValue);
        Size measured = TextRenderer.MeasureText(text, label.Font, proposed,
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        bool truncated = measured.Height > label.ClientSize.Height;

        _tip.SetToolTip(label, truncated ? text : null);
    }

    private void LayoutBody()
    {
        int x = 0;
        foreach (Control c in _body.Controls)
        {
            switch (c)
            {
                case FlatButton b when b == _minusBtn:
                    b.SetBounds(0, 0, 36, 30); x = 40; break;
                case FlatButton b when b == _plusBtn:
                    b.SetBounds(x, 0, 36, 30); x += 44; break;
                case FlatButton b when b == _fillBtn:
                    b.SetBounds(0, 0, 76, 30); break;
                case FlatButton b when b == _foldBtn:
                    b.SetBounds(_body.Width - b.Width, 0, b.Width, 30); break;
                case ToggleSwitch t:
                    t.SetBounds(0, 3, 46, 24); break;
                case CheckBox cb:
                    cb.Left = 56; cb.Top = 6; break;
                case TextBox tb when tb == _numInput:
                    tb.SetBounds(0, 4, Math.Max(40, _body.Width - 76), 24); break;
                case FlatButton nb when nb == _numApply:
                    nb.SetBounds(Math.Max(44, _body.Width - 72), 0, 68, 30); break;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Draw.ClearToParent(this, g, Tokens.PageBack);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.Card(g, rect, Tokens.Surface, Tokens.Border);

        // 底部一条极细的强调线，用语义色区分「局内/持久」
        if (Semantic is "gold_total")
        {
            using var pen = new Pen(Tokens.Warning, 2f);
            g.DrawLine(pen, Tokens.ControlRadius, Height - 1, Width - Tokens.ControlRadius, Height - 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
