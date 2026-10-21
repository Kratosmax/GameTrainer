using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// jsVal 插值的结构性防护（源码模式锁）。
///
/// 历史模式：jsVal 的注入修复曾三次只落在一条路径上——
///   1. AdapterRunner 修复时（b1fb181），Program.cs 的老 profile 命令漏了；
///   2. RuntimePath 白名单（0109861）只加在 Write 路径，读路径漏了（67fc194 补）；
///   3. 本轮补齐 Program.cs 时，全量 grep 确认只剩四处 jsVal 插值。
///
/// 断言手段与 RuntimePathInterpolationTests 相同：反射读源码。
/// 规则：**每一个** jsVal 插值点的前文都必须经过 JsLiteral.TryPrepare 或
/// double.TryParse 兜底，禁止「ternary + 裸变量」直出。
/// 将来新增脚本构造点若忘了走共享判定，这里会红。
/// </summary>
public class JsValInterpolationTests
{
    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameTrainer.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir.FullName, relative);
        Assert.True(File.Exists(path), $"找不到源文件 {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Program_cs_没有裸jsVal直出()
    {
        string src = ReadSource(@"src\GameTrainer\Program.cs");

        // 唯一允许的模式：jsVal 由 JsLiteral.TryPrepare 产出（3 处）
        // 或 double.TryParse 兜底（jssetvar，数字否则 JSON 序列化）
        Assert.DoesNotContain("== \"string\" ? JsonSerializer.Serialize(", src);
        Assert.DoesNotContain("? JsonSerializer.Serialize(rawValue) : rawValue", src);
        Assert.DoesNotContain("? JsonSerializer.Serialize(raw) : raw", src);

        // 三处老命令的修复必须在位
        Assert.Contains("JsLiteral.TryPrepare(rawValue, patch.ValueType, out string jsVal)", src);
        Assert.Contains("JsLiteral.TryPrepare(rawValue, patch.ValueType, out string jsVal)", src);
        Assert.Contains("JsLiteral.TryPrepare(raw, type, out string jsVal)", src);
    }

    [Fact]
    public void AdapterRunner_的jsVal必须走有限数字白名单()
    {
        string src = ReadSource(@"src\GameTrainer\Adapter\AdapterRunner.cs");

        // sem 路径的修复（b1fb181）不能被回退：拒绝分支必须在插值之前
        int rejectIdx = src.IndexOf("不是合法数字", StringComparison.Ordinal);
        int jsValUseIdx = src.IndexOf("{{jsVal}}", StringComparison.Ordinal);
        Assert.True(rejectIdx >= 0, "AdapterRunner 缺少非数字拒绝分支");
        Assert.True(jsValUseIdx >= 0, "没找到 jsVal 插值点，测试可能需要更新");
        Assert.True(rejectIdx < jsValUseIdx, "拒绝分支必须在插值之前");
    }

    [Fact]
    public void js命令是唯一豁免且必须明示()
    {
        // js <port> <code> 的功能就是执行用户 JS（调试用途），是明示的设计豁免。
        // 锁住它的存在：如果未来有人把 js 命令删了或改名，提醒重新评估本测试。
        string src = ReadSource(@"src\GameTrainer\Program.cs");
        Assert.Contains("string result = cdp.Evaluate(code);", src);
    }
}
