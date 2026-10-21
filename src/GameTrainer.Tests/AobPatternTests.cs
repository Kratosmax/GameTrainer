using GameTrainer.Memory;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// AOB 签名解析与匹配。
///
/// 这是安全机制的地基：签名不唯一就拒绝写入。
/// 一旦解析或匹配有偏差，可能定位到错误地址并写入。
/// </summary>
public class AobPatternTests
{
    [Fact]
    public void 完整字节_精确匹配()
    {
        var p = new AobPattern("DE AD BE EF");
        Assert.Equal(4, p.Length);
        Assert.True(p.IsMatchAt(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 0));
        Assert.False(p.IsMatchAt(new byte[] { 0xDE, 0xAD, 0xBE, 0x00 }, 0));
    }

    [Fact]
    public void 全通配符_匹配任意字节()
    {
        var p = new AobPattern("?? ??");
        Assert.Equal(2, p.Length);
        Assert.True(p.IsMatchAt(new byte[] { 0x00, 0xFF }, 0));
        Assert.True(p.IsMatchAt(new byte[] { 0x12, 0x34 }, 0));
    }

    [Fact]
    public void 单问号等价于双问号()
    {
        var a = new AobPattern("? ?");
        var b = new AobPattern("?? ??");
        Assert.Equal(b.Length, a.Length);
        var data = new byte[] { 0xAB, 0xCD };
        Assert.True(a.IsMatchAt(data, 0));
    }

    [Fact]
    public void 半字节通配_高四位()
    {
        // "4?" = 高四位必须是 4
        var p = new AobPattern("4?");
        Assert.True(p.IsMatchAt(new byte[] { 0x4A }, 0));
        Assert.True(p.IsMatchAt(new byte[] { 0x4F }, 0));
        Assert.False(p.IsMatchAt(new byte[] { 0x5A }, 0));
        Assert.False(p.IsMatchAt(new byte[] { 0x3A }, 0));
    }

    [Fact]
    public void 半字节通配_低四位()
    {
        // "?F" = 低四位必须是 F
        var p = new AobPattern("?F");
        Assert.True(p.IsMatchAt(new byte[] { 0x0F }, 0));
        Assert.True(p.IsMatchAt(new byte[] { 0xAF }, 0));
        Assert.False(p.IsMatchAt(new byte[] { 0xA0 }, 0));
    }

    [Fact]
    public void 混合通配符()
    {
        var p = new AobPattern("48 8B ?? ?? 89 4?");
        Assert.Equal(6, p.Length);
        Assert.True(p.IsMatchAt(new byte[] { 0x48, 0x8B, 0x00, 0xFF, 0x89, 0x4A }, 0));
        Assert.False(p.IsMatchAt(new byte[] { 0x48, 0x8B, 0x00, 0xFF, 0x89, 0x5A }, 0));
    }

    [Fact]
    public void 大小写不敏感()
    {
        var upper = new AobPattern("DE AD");
        var lower = new AobPattern("de ad");
        var data = new byte[] { 0xDE, 0xAD };
        Assert.True(upper.IsMatchAt(data, 0));
        Assert.True(lower.IsMatchAt(data, 0));
    }

    [Fact]
    public void 多余空白被忽略()
    {
        var p = new AobPattern("  DE   AD\tBE\nEF  ");
        Assert.Equal(4, p.Length);
    }

    [Fact]
    public void 空签名抛异常()
    {
        Assert.Throws<FormatException>(() => new AobPattern(""));
        Assert.Throws<FormatException>(() => new AobPattern("   "));
    }

    [Fact]
    public void 非法片段抛异常()
    {
        Assert.Throws<FormatException>(() => new AobPattern("ZZ"));
        Assert.Throws<FormatException>(() => new AobPattern("ABC"));  // 3 位
        Assert.Throws<FormatException>(() => new AobPattern("A"));    // 1 位
    }

    [Fact]
    public void 越界访问不抛异常_返回false()
    {
        var p = new AobPattern("DE AD BE EF");
        var data = new byte[] { 0xDE, 0xAD };
        Assert.False(p.IsMatchAt(data, 0));      // 数据不够长
        Assert.False(p.IsMatchAt(data, -1));     // 负偏移
        Assert.False(p.IsMatchAt(data, 100));    // 远超范围
    }

    [Fact]
    public void 首次命中_返回相对偏移()
    {
        var p = new AobPattern("BE EF");
        var data = new byte[] { 0x00, 0x11, 0xBE, 0xEF, 0x22 };
        Assert.Equal(2, p.FirstMatch(data));
    }

    [Fact]
    public void 未命中_返回负一()
    {
        var p = new AobPattern("FF FF");
        Assert.Equal(-1, p.FirstMatch(new byte[] { 0x00, 0x11 }));
    }

    [Fact]
    public void 统计命中_达到上限即返回()
    {
        var p = new AobPattern("AA");
        var data = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA };
        // 默认上限 2，命中 2 个就返回
        Assert.Equal(2, p.CountMatches(data));
        // 上限提高到 10，全部统计
        Assert.Equal(4, p.CountMatches(data, maxCount: 10));
    }

    [Fact]
    public void 统计命中_重叠位置也计入()
    {
        // "AA AA" 在 "AA AA AA" 中从 0 和 1 都能命中
        var p = new AobPattern("AA AA");
        Assert.Equal(2, p.CountMatches(new byte[] { 0xAA, 0xAA, 0xAA }, maxCount: 10));
    }

    [Fact]
    public void 空数据不命中()
    {
        var p = new AobPattern("AA");
        Assert.Equal(-1, p.FirstMatch(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, p.CountMatches(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Source保留原始输入()
    {
        var p = new AobPattern("DE  AD");
        Assert.Equal("DE  AD", p.Source);
        Assert.Equal("DE  AD", p.ToString());
    }
}
