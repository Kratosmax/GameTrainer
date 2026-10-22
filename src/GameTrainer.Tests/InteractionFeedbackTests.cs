using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 交互反馈的回归锁。
///
/// 历史：V4/V6 时期这类问题来自同一个疏忽——控件看起来能用，
/// 但键盘用户或异常输入下没有任何反馈。
///
/// 重构后（OriginOS 单控件自绘），反馈方式变了：
///   旧：按钮始终可点，点下去用提示文字告诉你"输入为空 / 不是数字 / 请先刷新"
///   新：输入为空时按钮直接禁用（视觉上灰掉），不给你点了没反应的机会
///
/// 契约本身没变——**用户操作必须有可见结果，不能静默无反应**。
/// 这里断言的是新实现下该契约的落实方式。
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
    public void 输入为空时应用按钮禁用而不是静默失败()
    {
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        // 按钮可用性由输入内容决定
        Assert.Contains("bool canApply = row.Input.Length > 0", src);

        // 不可用时用禁用色，而不是保持主色让人以为能点
        Assert.Contains("canApply ? Tokens.AccentSoft : Tokens.SurfaceAlt", src);
        Assert.Contains("canApply ? Tokens.Accent : Tokens.TextDisabled", src);
    }

    [Fact]
    public void 提交空输入直接返回不产生无反馈的写入()
    {
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        // CommitEdit 里空值必须短路，避免"点了应用但什么也没发生"
        Assert.Contains("if (value.Length == 0) return;", src);
    }

    [Fact]
    public void 读取失败必须在卡片内可见()
    {
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        // 失败块要渲染错误文案，不能只留一个空卡片
        Assert.Contains("if (!b.Ok)", src);
        Assert.Contains("ShortenError(b.Error)", src);
    }

    [Fact]
    public void 错误文案覆盖三种常见场景()
    {
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        Assert.Contains("游戏未启动", src);      // 找不到进程
        Assert.Contains("不在当前场景", src);    // 无实例
        Assert.Contains("读取失败", src);        // 兜底
    }

    [Fact]
    public void 空状态必须无条件可见_不能挂在动画进度上()
    {
        // 真 bug：空状态用 _pageT 算 alpha，动画不推进时 _pageT 恒为 0，
        // 整块文字完全透明，用户看到一片空白。
        // 空状态是必须让人看见的信息，不能依赖可能不跑的动画。
        string src = ReadSource(@"src\GameTrainer\Pages\GameView.cs");

        int idx = src.IndexOf("private void PaintEmpty", StringComparison.Ordinal);
        Assert.True(idx > 0, "找不到 PaintEmpty");

        string body = src.Substring(idx, Math.Min(600, src.Length - idx));
        Assert.Contains("const float t = 1f", body);
    }

    [Fact]
    public void 首页空状态同样不依赖动画进度()
    {
        string src = ReadSource(@"src\GameTrainer\Pages\HomeView.cs");

        int idx = src.IndexOf("private void PaintEmpty", StringComparison.Ordinal);
        Assert.True(idx > 0, "找不到 PaintEmpty");

        string body = src.Substring(idx, Math.Min(600, src.Length - idx));
        Assert.Contains("const float t = 1f", body);
    }
}
