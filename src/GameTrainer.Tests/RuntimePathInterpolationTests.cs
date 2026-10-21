using GameTrainer.Adapter;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// RuntimePath 插值的结构性防护。
///
/// 背景：白名单一度只加在 Write 路径，ReadAll 和 BuildReadInstanceVar 漏了。
/// 投毒适配包只要被 read 一次就能在游戏进程执行任意 JS
/// （实测 `sem &lt;游戏&gt; read` 即触发，打上了 window 标记）。
///
/// 修法是把校验下沉进插值点（SafeRuntimeExpr），让新增调用点自动受保护。
/// 这里用的断言手段是**反射读源码**：确认没有任何 `{{rt}}` / `{{rtPath}}`
/// 形式的裸插值残留。这比逐点断言更耐改——将来新增构造器若忘了包
/// SafeRuntimeExpr，这个测试会红。
/// </summary>
public class RuntimePathInterpolationTests
{
    private static string ReadSource(string relative)
    {
        // 从测试运行目录回溯到仓库根
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameTrainer.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir.FullName, relative);
        Assert.True(File.Exists(path), $"找不到源文件 {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void 没有裸插值_AdapterRunner()
    {
        string src = ReadSource(@"src\GameTrainer\Adapter\AdapterRunner.cs");

        // 允许的形式只有 SafeRuntimeExpr(...) 包裹
        Assert.DoesNotContain("{{rt}}", src);
        Assert.DoesNotContain("{{rtPath}}", src);

        // 确认确实用了安全包装（不是把插值删了导致功能失效）
        Assert.Contains("SafeRuntimeExpr(rt)", src);
        Assert.Contains("SafeRuntimeExpr(rtPath)", src);
    }

    [Fact]
    public void PersistentWatcher_校验必须在插值之前()
    {
        string src = ReadSource(@"src\GameTrainer\Engine\PersistentWatcher.cs");

        // 这里的模式是「显式校验在前 + 裸插值在后」，与 AdapterRunner 的
        // SafeRuntimeExpr 包裹不同但等价。断言两者相对顺序：
        // 校验语句必须出现在插值语句之前，否则就是漏洞。
        int checkIdx = src.IndexOf("IsValidPropertyChain", StringComparison.Ordinal);
        int interpIdx = src.IndexOf("const rt = {{_adapter.EngineOptions.RuntimePath}}", StringComparison.Ordinal);

        Assert.True(checkIdx >= 0, "PersistentWatcher 缺少 RuntimePath 白名单校验");
        Assert.True(interpIdx >= 0, "没找到插值点，测试可能需要更新");
        Assert.True(checkIdx < interpIdx,
            "校验出现在插值之后，等同于没有防护——投毒的 RuntimePath 会先被执行");
    }

    [Fact]
    public void SafeRuntimeExpr对非法值返回null()
    {
        // 非法表达式必须变成无害的 null，而不是抛异常或原样插入。
        // 通过公开的白名单方法间接验证该分支的判定依据。
        Assert.False(AdapterRunner.IsValidPropertyChain("self.constructor.constructor('x')()"));
        Assert.True(AdapterRunner.IsValidPropertyChain("self.c3_runtimeInterface._localRuntime"));
    }

    [Fact]
    public void 默认运行时路径通过白名单()
    {
        // 确认内置默认值不会被自己的白名单误杀
        Assert.True(AdapterRunner.IsValidPropertyChain("self.c3_runtimeInterface._localRuntime"));
    }
}