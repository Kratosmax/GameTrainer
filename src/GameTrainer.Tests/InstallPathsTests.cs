using GameTrainer.Adapter;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 用户指定游戏路径的持久化契约。
///
/// 背景：早期实现会在 C:/D:/E:/F: 的 Games 目录下自动找游戏，
/// 那是未经同意的磁盘探测，已删除。现在只能靠用户显式指定。
/// 这条链路一旦断了，用户设完路径重启后又得重设，所以必须锁住。
/// </summary>
public class InstallPathsTests
{
    [Fact]
    public void 设置路径后能读回()
    {
        string id = "test-adapter-" + Guid.NewGuid().ToString("N");
        string path = @"F:\Some\Game\game.exe";

        AdapterInstallPaths.Set(id, path);

        Assert.Equal(path, AdapterInstallPaths.Resolve(id));

        // 清理
        AdapterInstallPaths.Set(id, null);
        Assert.Null(AdapterInstallPaths.Resolve(id));
    }

    [Fact]
    public void 未设置的适配包返回null而不是空串()
    {
        string id = "never-set-" + Guid.NewGuid().ToString("N");

        // 返回 null 而不是 ""：调用方用 null 判断"没有路径"，
        // 空串会让 File.Exists("") 也返回 false，但语义上混淆。
        Assert.Null(AdapterInstallPaths.Resolve(id));
    }

    [Fact]
    public void 传空值会删除记录()
    {
        string id = "clear-me-" + Guid.NewGuid().ToString("N");
        AdapterInstallPaths.Set(id, @"C:\a\b.exe");

        Assert.NotNull(AdapterInstallPaths.Resolve(id));

        AdapterInstallPaths.Set(id, "");

        Assert.Null(AdapterInstallPaths.Resolve(id));
    }

    [Fact]
    public void 适配包id不区分大小写()
    {
        string id = "CaseTest" + Guid.NewGuid().ToString("N");
        AdapterInstallPaths.Set(id, @"D:\x\y.exe");

        // 适配包 id 在别处都是 OrdinalIgnoreCase 比较，
        // 路径表也必须一致，否则大小写不同就读不到。
        Assert.Equal(@"D:\x\y.exe", AdapterInstallPaths.Resolve(id.ToLowerInvariant()));

        AdapterInstallPaths.Set(id, null);
    }

    [Fact]
    public void 路径带空格能正确存取()
    {
        // 实测踩过：Hospital Rhapsody 的路径带空格，
        // 剪贴板粘贴时可能带引号，Set 里要 Trim 掉。
        string id = "space-" + Guid.NewGuid().ToString("N");
        string path = @"F:\Game\Hospital Rhapsody\HospitalRhapsody.exe";

        AdapterInstallPaths.Set(id, path);

        Assert.Equal(path, AdapterInstallPaths.Resolve(id));

        AdapterInstallPaths.Set(id, null);
    }
}
