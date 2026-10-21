using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// V4/V6 的回归锁。
///
/// 这两个问题都来自同一类疏忽：**控件看起来能用，但键盘用户或异常输入下没有反馈**。
///
/// V4：ShowFocusCues 依赖窗口真实激活状态，DrawToBitmap 离线渲染恒为 false，
///     所以必须有 ForceFocusCue 这个测试专用开关，否则焦点框永远无法离线验证。
/// V6：_busy / 解析失败 / 无值三种情况原先直接 return，点了没反应。
/// </summary>
public class InteractionFeedbackTests
{
    private static string ReadSource(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameTrainer.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string p = Path.Combine(dir.FullName, rel);
        Assert.True(File.Exists(p), $"找不到 {p}");
        return File.ReadAllText(p);
    }

    [Fact]
    public void 可聚焦控件都有ForceFocusCue开关()
    {
        // 焦点框绘制条件里必须包含 ForceFocusCue，否则离线截图永远看不到焦点。
        string src = ReadSource(@"src\GameTrainer\UI\FlatControls.cs");

        Assert.Contains("public bool ForceFocusCue", src);
        int cues = CountOccurrences(src, "ForceFocusCue || (Focused && ShowFocusCues)");
        Assert.True(cues >= 2,
            $"只有 {cues} 处绘制用了 ForceFocusCue，FlatButton 与 ToggleSwitch 都应支持");
    }

    [Fact]
    public void ToggleSwitch焦点框不越界()
    {
        // 曾经用 Rectangle.Inflate(track, 3, 3)：track 是 40x20 居中在 46x24 的控件里，
        // 外扩 3px 变成 46x26，高度超出控件被裁掉。现在按控件尺寸内缩绘制。
        string src = ReadSource(@"src\GameTrainer\UI\FlatControls.cs");

        Assert.DoesNotContain("Rectangle.Inflate(track, 3, 3)", src);
        Assert.Contains("new Rectangle(0, 0, Width - 1, Height - 1)", src);
    }

    [Fact]
    public void 静默失败已改为可见反馈()
    {
        string src = ReadSource(@"src\GameTrainer\UI\ValueCard.cs");

        // 三种提示必须存在
        Assert.Contains("ShowBusy()", src);
        Assert.Contains("ShowInputError(", src);
        Assert.Contains("ShowNoValue()", src);

        // 面向用户的三个入口（StepBy / ApplyNumberInput / ApplyInput）不得静默返回。
        // 注意不能一刀切禁止 "if (_busy) return;"——开关的 CheckedChanged 和
        // UpdateHint 里的同类判断是「程序设置值时不误触发」的内部保护，必须保留。
        Assert.Contains("if (_busy) { ShowBusy(); return; }", src);
        Assert.Contains("if (!_hasValue) { ShowNoValue(); return; }", src);

        // 三个用户入口各自都要有反馈：StepBy 2 处（busy + 无值），两个 Apply 各 2 处
        int busyGuarded = CountOccurrences(src, "if (_busy) { ShowBusy(); return; }");
        Assert.True(busyGuarded >= 3, $"只有 {busyGuarded} 个入口做了忙碌提示，应为 3（步进 + 两个应用）");
    }

    [Fact]
    public void 提示文案覆盖三种场景()
    {
        string src = ReadSource(@"src\GameTrainer\UI\ValueCard.cs");

        Assert.Contains("处理中，请稍候", src);      // _busy
        Assert.Contains("不是有效数字", src);        // 解析失败
        Assert.Contains("请先输入数值", src);        // 空输入
        Assert.Contains("先刷新再操作", src);        // 无值
    }

    [Fact]
    public void 无值提示必须幂等_不能每次追加后缀()
    {
        // 真 bug：ShowNoValue 原先直接 _hint.Text + 后缀，而 _hint.Text 是它自己上一次的产物。
        // 连点步进按钮 N 次就会叠 N 层「（先刷新再操作）」，把提示区撑爆到截断。
        string src = ReadSource(@"src\GameTrainer\UI\ValueCard.cs");

        // 必须有 EndsWith 判断来短路重复追加
        Assert.Contains("EndsWith(NoValueSuffix", src);
        // 后缀必须抽成常量，避免两处字面量不一致
        Assert.Contains("private const string NoValueSuffix", src);
    }

    [Fact]
    public void 忙碌分支标注了实际不可达()
    {
        // ShowBusy 当前不可达（_busy 只在同步 SetValue 期间为 true）。
        // 不写清楚的话，后来者会以为它是活的反馈路径，或在重构时误删。
        string src = ReadSource(@"src\GameTrainer\UI\ValueCard.cs");
        Assert.Contains("实际不可达", src);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
