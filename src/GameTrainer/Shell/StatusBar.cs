using GameTrainer.Adapter;
using GameTrainer.UI;

namespace GameTrainer.Shell;

/// <summary>
/// 底部状态栏 —— 只显示「对当前任务有意义」的信息。
///
/// 抽出来的原因：原来这段在 GuiForm 里由 BuildStatusBar + 4 个 Label 字段
/// + ConfigureStatusLabel 组成，任何一处文案变化都要在 1700 行里找。
/// 现在它自成一个控件，对外只有四个 setter。
///
/// 设计取舍：状态栏不重复顶栏已经说过的信息。
/// 顶栏有状态胶囊（运行中/未运行），这里就不再放「已连接/未连接」——
/// 旧版两处并存且各自更新，会同时亮起互相矛盾的状态。
/// </summary>
internal sealed class StatusBar : Panel
{
    private readonly Label _game = new();
    private readonly Label _engine = new();
    private readonly Label _startup = new();
    private readonly Label _hotkey = new();

    public StatusBar()
    {
        Dock = DockStyle.Fill;
        BackColor = Tokens.Surface;
        Padding = new Padding(Tokens.S5, 0, Tokens.S5, 0);
        Margin = new Padding(0);

        // 顶边分隔线：状态栏与内容区之间需要一条弱边界，
        // 否则白底和白卡在视觉上连成一片。
        Paint += (_, e) =>
        {
            using var pen = new Pen(Tokens.Border, 1f);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Tokens.Surface,
            Margin = new Padding(0)
        };

        // 前两列承载较长的动态文本，给弹性宽度；后两列是短提示
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        foreach (var l in new[] { _game, _engine, _startup, _hotkey })
        {
            l.Dock = DockStyle.Fill;
            l.Font = Tokens.FontCaption;
            l.ForeColor = Tokens.TextSecondary;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;
            l.Margin = new Padding(0);
        }

        layout.Controls.Add(_game, 0, 0);
        layout.Controls.Add(_engine, 1, 0);
        layout.Controls.Add(_startup, 2, 0);
        layout.Controls.Add(_hotkey, 3, 0);

        Controls.Add(layout);

        SetGame(null);
        SetStartup(false);
        SetHotkeyCount(0);
    }

    /// <summary>
    /// 显示当前游戏与引擎。
    /// 传 null 表示未选择游戏（首页）。
    /// </summary>
    public void SetGame(GameAdapter a)
    {
        if (a == null)
        {
            _game.Text = "未选择游戏";
            _game.ForeColor = Tokens.TextMuted;
            _engine.Text = "引擎：—";
            return;
        }

        _game.Text = $"{a.Name} · {a.Engine}";
        _game.ForeColor = Tokens.TextPrimary;

        // 只对走 CDP 的引擎关心端口；原生内存类游戏没有端口概念，
        // 写「未连接」会让人以为有问题。
        bool usesCdp = string.Equals(a.Engine, "construct3", StringComparison.OrdinalIgnoreCase);
        _engine.Text = usesCdp
            ? $"调试端口：{a.Connection.DebugPort}"
            : $"原生内存 · {a.Bindings.Count} 个数值";
    }

    /// <summary>开机自启状态。</summary>
    public void SetStartup(bool enabled)
    {
        _startup.Text = enabled ? "开机自启：已启用" : "开机自启：未启用";
        _startup.ForeColor = Tokens.TextSecondary;
    }

    /// <summary>已绑定的快捷键数量。</summary>
    public void SetHotkeyCount(int count)
    {
        _hotkey.Text = count > 0 ? $"快捷键：{count} 项已绑定" : "快捷键：未配置";
        _hotkey.ForeColor = Tokens.TextSecondary;
    }

    /// <summary>首页时清空游戏相关信息，避免残留上一个游戏。</summary>
    public void ClearGame()
    {
        _game.Text = "从下方选择一个游戏开始";
        _game.ForeColor = Tokens.TextMuted;
        _engine.Text = "";
    }
}
