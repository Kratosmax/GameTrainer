using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 长文本可读性的回归锁。
///
/// 历史：V3 时期 ValueCard 里的 warning 文本超过卡片宽度会被截断且无提示，
/// 当时靠给 Label 挂 ToolTip 解决——这是 WinForms 子控件方案下的补丁。
///
/// 重构后（单控件自绘）不再有 Label，也就不需要 ToolTip：
///   - 文本统一走 Draw.Text，超长自动省略号，不会画出边界
///   - 关键信息（错误原因、游戏名）改用独占一行或加宽区域，不靠悬停才能看全
///
/// 契约没变：**用户能看到的文本必须是完整的，或被明确告知还有更多**。
/// 这里断言新实现下该契约的落实方式。
/// </summary>
public class LongTextReadabilityTests
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
    public void 所有文字绘制都走统一的省略号策略()
    {
        string src = ReadSource(@"src\GameTrainer\UI\Draw.cs");

        // DrawString 必须带 EllipsisCharacter，否则超长文本会画出控件边界
        Assert.Contains("Trimming = StringTrimming.EllipsisCharacter", src);

        // 并且要禁止自动换行（换行会撑破固定高度的行）
        Assert.Contains("StringFormatFlags.NoWrap", src);
    }

    [Fact]
    public void 错误文案有独立的缩短函数而不是直接截断()
    {
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        // 缩短时要给出语义化的短语，而不是把原句砍一半
        Assert.Contains("ShortenError", src);
        Assert.Contains("找不到进程", src);
    }

    [Fact]
    public void 游戏标题宽度足够容纳常见游戏名()
    {
        // 真 bug：Label.AutoSize=false 时不给 Width 会用默认 100px，
        // "Hospital Rhapsody" 被截成 "Hos"。
        string src = ReadSource(@"src\GameTrainer\GuiForm.cs");

        Assert.Contains("_gameTitle.Width = 640", src);
    }

    [Fact]
    public void 数值区宽度按实际文字测量而不是固定值()
    {
        // 真 bug：固定 130px 装不下 19pt 的 2,000,070，被截成 2000...
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        Assert.Contains("Draw.Measure(g, row.Value", src);
    }

    [Fact]
    public void 卡片行宽分配留足输入框与按钮空间()
    {
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        // 输入框要放得下 5 位数字，按钮要放得下"应用"两字
        Assert.Contains("int inputW = 110", src);
        Assert.Contains("int btnW = 72", src);
    }
}
