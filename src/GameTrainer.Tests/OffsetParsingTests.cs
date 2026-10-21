using GameTrainer.Engine;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 偏移字符串解析。
///
/// 这是 memory_aob 最容易出错的一环：符号写反不会报错，
/// 只会静默读到错误的地址，然后读出一个看起来"像数字"的垃圾值。
/// 本轮实现时就把 +0x10 写成了 -0x10，读出 1.06E-313 才发现。
/// </summary>
public class OffsetParsingTests
{
    [Theory]
    [InlineData("0x10", 0x10)]
    [InlineData("+0x10", 0x10)]
    [InlineData("-0x10", -0x10)]
    [InlineData("0X10", 0x10)]
    [InlineData("0x0", 0)]
    [InlineData("0x28", 0x28)]
    [InlineData("0xFF", 0xFF)]
    public void 十六进制_带符号与大小写(string input, long expected)
    {
        Assert.Equal(expected, PatchEngine.ParseOffset(input));
    }

    [Theory]
    [InlineData("16", 16)]
    [InlineData("+16", 16)]
    [InlineData("-16", -16)]
    [InlineData("0", 0)]
    [InlineData("-24", -24)]
    public void 十进制(string input, long expected)
    {
        Assert.Equal(expected, PatchEngine.ParseOffset(input));
    }

    [Theory]
    [InlineData("  0x10  ", 0x10)]
    [InlineData(" -0x1C ", -0x1C)]
    [InlineData("\t+8\n", 8)]
    public void 忽略首尾空白(string input, long expected)
    {
        Assert.Equal(expected, PatchEngine.ParseOffset(input));
    }

    [Fact]
    public void 负十六进制不会因符号抛异常()
    {
        // Convert.ToInt64("-18", 16) 会抛格式错误，
        // 所以实现必须先把负号取出来再转换。
        Assert.Equal(-0x18, PatchEngine.ParseOffset("-0x18"));
        Assert.Equal(-0x1C, PatchEngine.ParseOffset("-0x1C"));
    }

    [Fact]
    public void 大偏移不溢出()
    {
        Assert.Equal(0x7FFFFFFF, PatchEngine.ParseOffset("0x7FFFFFFF"));
        Assert.Equal(-0x100000, PatchEngine.ParseOffset("-0x100000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0xZZ")]
    [InlineData("abc")]
    [InlineData("0x")]
    [InlineData("+")]
    [InlineData("-")]
    public void 非法输入抛FormatException(string input)
    {
        Assert.Throws<FormatException>(() => PatchEngine.ParseOffset(input));
    }

    [Fact]
    public void 符号与数值组合正确()
    {
        // 关键回归：+0x10 必须是正数，-0x10 必须是负数。
        // 两者绝对值相同但方向相反，写反会导致完全不同的地址。
        long plus = PatchEngine.ParseOffset("+0x10");
        long minus = PatchEngine.ParseOffset("-0x10");
        Assert.Equal(16, plus);
        Assert.Equal(-16, minus);
        Assert.NotEqual(plus, minus);
    }
}
