using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 数据目录解析的边界行为。
///
/// 这块踩过两个真实问题：
/// 1. **劫持**：程序向上 6 层找含 profiles 的目录，发行包解压在工程内部时
///    会读到仓库的自测靶配置（实测 profiles 命令列出了 gttest）。
/// 2. **过度收紧**：修 #1 时先改成"目录存在即命中"，但构建输出目录
///    bin/Release/net8.0-windows 下会残留空 adapters，导致开发期停在
///    那层找不到工程根。
///
/// 正确规则：同目录**非空**的 adapters/profiles 优先；
/// 否则向上找同时具备 .sln 或 src 的"工程根"。
/// </summary>
public class DataRootResolutionTests
{
    /// <summary>复刻 ResolveSkillRoot 的判定规则（可注入目录结构）。</summary>
    private static string Resolve(string baseDir, Func<string, bool> hasData, Func<string, bool> looksLikeRepo)
    {
        if (hasData(Path.Combine(baseDir, "adapters")) || hasData(Path.Combine(baseDir, "profiles")))
            return baseDir;

        var d = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
        {
            bool hd = hasData(Path.Combine(d.FullName, "adapters")) || hasData(Path.Combine(d.FullName, "profiles"));
            if (hd && looksLikeRepo(d.FullName)) return d.FullName;
        }
        return baseDir;
    }

    [Fact]
    public void 同目录有非空adapters_用同目录()
    {
        string baseDir = @$"C:\pkg";
        var root = Resolve(baseDir,
            hasData: p => p == Path.Combine(baseDir, "adapters"),
            looksLikeRepo: _ => false);

        Assert.Equal(baseDir, root);
    }

    [Fact]
    public void 同目录adapters是空_继续向上找()
    {
        // 构建输出目录会残留空 adapters：必须视为"无数据"，否则开发期停在那层
        string baseDir = @"C:\repo\temp\build\GameTrainer\Release\net8.0-windows";
        string repoRoot = @"C:\repo";

        var root = Resolve(baseDir,
            hasData: p => p == Path.Combine(repoRoot, "adapters"),      // 只有工程根非空
            looksLikeRepo: p => p == repoRoot);

        Assert.Equal(repoRoot, root);
    }

    [Fact]
    public void 上层有数据但不是工程根_不算()
    {
        // 发行包解压在别人目录里时，上层可能有任意同名文件夹。
        // 只有同时像"工程根"（有 .sln 或 src）才认，防止劫持。
        string baseDir = @"D:\some\random\place\pkg";
        string fakeParent = @"D:\some\random";

        var root = Resolve(baseDir,
            hasData: p => p == Path.Combine(fakeParent, "profiles"),    // 上层有 profiles
            looksLikeRepo: _ => false);                                 // 但不像仓库

        Assert.Equal(baseDir, root);   // 不采用，退回自身
    }

    [Fact]
    public void 向上最多六层()
    {
        // 7 层以上不再找，避免深目录结构里误命中
        string deep = @"C:\a\b\c\d\e\f\g\pkg";
        string farRoot = @"C:\a";

        var root = Resolve(deep,
            hasData: p => p == Path.Combine(farRoot, "adapters"),
            looksLikeRepo: p => p == farRoot);

        Assert.Equal(deep, root);
    }

    [Fact]
    public void 都找不到_退回baseDir()
    {
        string baseDir = @"C:\nothing";
        var root = Resolve(baseDir, _ => false, _ => false);
        Assert.Equal(baseDir, root);
    }

    [Fact]
    public void 工程根同时有数据且像仓库_采用()
    {
        string baseDir = @"F:\git\temp\game-trainer\temp\build\GameTrainer\Release\net8.0-windows";
        string repoRoot = @"F:\git\temp\game-trainer";

        var root = Resolve(baseDir,
            hasData: p => p == Path.Combine(repoRoot, "adapters"),
            looksLikeRepo: p => File.Exists(Path.Combine(p, "GameTrainer.sln")) || Directory.Exists(Path.Combine(p, "src")));

        Assert.Equal(repoRoot, root);
    }
}
