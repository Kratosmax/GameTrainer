using GameTrainer.Adapter;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 适配包校验：aob 与 chain 的互斥规则。
///
/// 两者都给或都不给都意味着配置错误，但错误配置如果静默通过，
/// 运行时才会以"定位失败"的形式暴露，排查成本高。
/// </summary>
public class AdapterValidationTests
{
    private static GameAdapter Base(string kind = "memory_aob") => new()
    {
        Id = "test",
        Name = "Test",
        Engine = "native_memory",
        Exe = "test.exe",
        Mode = "allow",
        Bindings = new List<ValueBinding>()
    };

    private static ValueBinding Binding(string semantic = Semantics.Gold) => new()
    {
        Semantic = semantic,
        Kind = "memory_aob",
        Type = "double",
        Range = new[] { 0.0, 9999.0 }
    };

    [Fact]
    public void 只有aob_合法()
    {
        var a = Base();
        var b = Binding();
        b.Aob = "DE AD BE EF";
        a.Bindings.Add(b);
        Assert.Empty(a.Validate());
    }

    [Fact]
    public void 只有chain_合法()
    {
        var a = Base();
        var b = Binding();
        b.Chain = new List<string> { "0x30000000", "0x00" };
        a.Bindings.Add(b);
        Assert.Empty(a.Validate());
    }

    [Fact]
    public void 两者都给_报错()
    {
        var a = Base();
        var b = Binding();
        b.Aob = "DE AD BE EF";
        b.Chain = new List<string> { "0x30000000", "0x00" };
        a.Bindings.Add(b);

        var errs = a.Validate();
        Assert.Contains(errs, e => e.Contains("只能给一个"));
    }

    [Fact]
    public void 两者都不给_报错()
    {
        var a = Base();
        a.Bindings.Add(Binding());

        var errs = a.Validate();
        Assert.Contains(errs, e => e.Contains("需要 aob 或 chain"));
    }

    [Fact]
    public void chain为空列表_视同未提供()
    {
        var a = Base();
        var b = Binding();
        b.Chain = new List<string>();
        a.Bindings.Add(b);

        var errs = a.Validate();
        Assert.Contains(errs, e => e.Contains("需要 aob 或 chain"));
    }

    [Fact]
    public void chain可以不给module_绝对地址模式()
    {
        var a = Base();
        var b = Binding();
        b.Chain = new List<string> { "0x30000000" };   // 单级 = 直接地址
        b.Module = null;
        a.Bindings.Add(b);

        Assert.Empty(a.Validate());
    }

    [Fact]
    public void 未知语义_报错()
    {
        var a = Base();
        var b = Binding("nonexistent_semantic");
        b.Aob = "DE AD";
        a.Bindings.Add(b);

        var errs = a.Validate();
        Assert.Contains(errs, e => e.Contains("未知语义"));
    }

    [Fact]
    public void 语义重复_报错()
    {
        var a = Base();
        var b1 = Binding();
        b1.Aob = "DE AD";
        var b2 = Binding();
        b2.Aob = "BE EF";
        a.Bindings.Add(b1);
        a.Bindings.Add(b2);

        var errs = a.Validate();
        Assert.Contains(errs, e => e.Contains("语义重复"));
    }

    [Fact]
    public void 缺少必填字段_报错()
    {
        var a = new GameAdapter { Bindings = new List<ValueBinding>() };
        var errs = a.Validate();
        Assert.Contains(errs, e => e.Contains("id"));
        Assert.Contains(errs, e => e.Contains("exe"));
        Assert.Contains(errs, e => e.Contains("engine"));
    }

    [Fact]
    public void Describe区分三种绑定()
    {
        var b = Binding();
        b.Aob = "DE AD";
        Assert.Contains("AOB", b.Describe());

        b.Chain = new List<string> { "0x1000" };
        b.Aob = null;
        Assert.Contains("AOB", b.Describe());   // chain 目前复用 AOB 描述

        var iv = Binding();
        iv.Kind = "instance_var";
        iv.ObjectName = "heros";
        iv.VarName = "vie";
        Assert.Equal("heros.vie", iv.Describe());

        var idb = Binding();
        idb.Kind = "indexeddb_key";
        idb.StorageKey = "or";
        Assert.Contains("IndexedDB", idb.Describe());
    }
}
