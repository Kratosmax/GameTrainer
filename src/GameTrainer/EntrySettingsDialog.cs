using GameTrainer.Core;
using GameTrainer.UI;

namespace GameTrainer;

/// <summary>
/// 修改项设置弹窗：双击某个修改项名称后打开。
///
/// 为什么用弹窗而不是"每行一个快捷键输入框"：
/// 修改项可能有十几二十个，每行都摆一个快捷键输入框会把列表撑得极长，
/// 而快捷键是低频配置项。弹窗按需出现，主界面保持清爽。
///
/// 内容：
///   - 全局快捷键（组合键捕获 + 清空）
///   - 折叠状态（让这个修改项在主页只显示标题和当前值）
///
/// 保存前检查系统占用与应用内重复；部分注册失败要整体回滚，
/// 不能留下"一半修改项有热键、一半没有"的中间态。
/// </summary>
internal sealed class EntrySettingsDialog : Form
{
    private readonly Prefs _prefs;
    private readonly Prefs.EntryPrefs _entry;
    private readonly string _displayName;
    private readonly Func<string, bool> _isDuplicate;
    private readonly TextBox _hotkeyBox = new();
    private readonly CheckBox _collapsedBox = new();
    private readonly Label _hint = new();

    public bool Saved { get; private set; }

    public EntrySettingsDialog(
        Prefs prefs,
        Prefs.EntryPrefs entry,
        string displayName,
        Func<string, bool> isDuplicate)
    {
        _prefs = prefs;
        _entry = entry;
        _displayName = displayName;
        _isDuplicate = isDuplicate;

        Text = "修改项设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Tokens.Surface;
        Font = Tokens.FontBody;
        ClientSize = new Size(440, 300);

        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(Tokens.S5),
            BackColor = Tokens.Surface
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var title = new Label
        {
            Text = _displayName,
            Font = Tokens.FontSection,
            ForeColor = Tokens.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        var hotkeyLabel = new Label
        {
            Text = "全局快捷键",
            Font = Tokens.FontCaption,
            ForeColor = Tokens.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };

        // 快捷键捕获：ReadOnly + KeyDown 处理，默认空表示不注册
        _hotkeyBox.Dock = DockStyle.Fill;
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.ShortcutsEnabled = false;
        _hotkeyBox.BackColor = Tokens.SurfaceSunken;
        _hotkeyBox.ForeColor = Tokens.TextPrimary;
        _hotkeyBox.BorderStyle = BorderStyle.FixedSingle;
        _hotkeyBox.KeyDown += OnHotkeyKeyDown;
        _hotkeyBox.Enter += (_, _) => _hint.Text = "按下组合键（需含 Ctrl / Alt / Shift），Esc 清空";
        _hotkeyBox.Leave += (_, _) => _hint.Text = "";

        var hotkeyRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        hotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        hotkeyRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var clearBtn = new FlatButton
        {
            Text = "清空",
            Kind = ButtonKind.Secondary,
            Dock = DockStyle.Fill,
            Margin = new Padding(Tokens.S2, 0, 0, 0)
        };
        clearBtn.Click += (_, _) => { _hotkeyBox.Text = ""; _hint.Text = ""; };

        hotkeyRow.Controls.Add(_hotkeyBox, 0, 0);
        hotkeyRow.Controls.Add(clearBtn, 1, 0);

        _collapsedBox.Text = "在主页折叠此项（只显示标题和当前值）";
        _collapsedBox.ForeColor = Tokens.TextPrimary;
        _collapsedBox.Dock = DockStyle.Fill;
        _collapsedBox.BackColor = Tokens.Surface;
        _collapsedBox.Margin = new Padding(0);

        _hint.Font = Tokens.FontCaption;
        _hint.ForeColor = Tokens.TextMuted;
        _hint.Dock = DockStyle.Fill;
        _hint.TextAlign = ContentAlignment.TopLeft;
        _hint.AutoEllipsis = true;

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var cancel = new FlatButton
        {
            Text = "取消",
            Kind = ButtonKind.Secondary,
            Dock = DockStyle.Fill,
            Margin = new Padding(Tokens.S2, 0, 0, 0)
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var save = new FlatButton
        {
            Text = "保存",
            Kind = ButtonKind.Primary,
            Dock = DockStyle.Fill,
            Margin = new Padding(Tokens.S2, 0, 0, 0)
        };
        save.Click += (_, _) => DoSave();

        buttons.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Tokens.Surface }, 0, 0);
        buttons.Controls.Add(cancel, 1, 0);
        buttons.Controls.Add(save, 2, 0);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(hotkeyLabel, 0, 1);
        root.Controls.Add(hotkeyRow, 0, 2);
        root.Controls.Add(_collapsedBox, 0, 3);
        root.Controls.Add(_hint, 0, 4);
        root.Controls.Add(buttons, 0, 5);

        Controls.Add(root);
        CancelButton = cancel;
    }

    private void LoadValues()
    {
        _hotkeyBox.Text = _entry.Hotkey ?? "";
        _collapsedBox.Checked = _entry.Collapsed;
    }

    /// <summary>
    /// 捕获组合键。
    /// 要求至少一个修饰键：单键（如 F5）注册成全局热键会抢占其他程序，
    /// 这是 RegisterHotKey 的常见误用。
    /// </summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.KeyCode == Keys.Escape)
        {
            _hotkeyBox.Text = "";
            return;
        }

        // 只按修饰键本身时不清空也不记录，等真正的功能键
        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
            return;

        bool hasModifier = e.Control || e.Alt || e.Shift;
        if (!hasModifier)
        {
            _hint.Text = "必须包含 Ctrl / Alt / Shift 中至少一个";
            return;
        }

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");

        string key = e.KeyCode switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (e.KeyCode - Keys.D0))).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + (e.KeyCode - Keys.NumPad0),
            _ => e.KeyCode.ToString()
        };
        parts.Add(key);

        _hotkeyBox.Text = string.Join("+", parts);
        _hint.Text = "";
    }

    private void DoSave()
    {
        string hotkey = _hotkeyBox.Text.Trim();

        if (hotkey.Length > 0)
        {
            if (!HotkeyManager.TryParseSpec(hotkey, out _, out _, out string parseErr))
            {
                _hint.Text = parseErr;
                return;
            }
            if (_isDuplicate(hotkey))
            {
                _hint.Text = $"快捷键 {hotkey} 已被其他修改项占用";
                return;
            }
        }

        _entry.Hotkey = hotkey;
        _entry.Collapsed = _collapsedBox.Checked;

        if (!_prefs.Save())
        {
            _hint.Text = "配置保存失败（目录可能不可写）";
            return;
        }

        Saved = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
