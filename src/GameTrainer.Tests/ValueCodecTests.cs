using GameTrainer.Memory;
using Xunit;

namespace GameTrainer.Tests;

/// <summary>
/// 数值编解码。
///
/// 写错一个字节就等于把垃圾值写进游戏，而且类型猜错时
/// 编解码不对称会静默产生错误结果。这里逐一锁定字节序与往返一致性。
/// </summary>
public class ValueCodecTests
{
    // NumType 是 internal，xUnit 的 Theory 参数必须是 public 可见类型，
    // 所以用 int 传枚举值再转换。
    [Theory]
    [InlineData((int)NumType.Byte, 1)]
    [InlineData((int)NumType.Int16, 2)]
    [InlineData((int)NumType.UInt16, 2)]
    [InlineData((int)NumType.Int32, 4)]
    [InlineData((int)NumType.UInt32, 4)]
    [InlineData((int)NumType.Float, 4)]
    [InlineData((int)NumType.Int64, 8)]
    [InlineData((int)NumType.UInt64, 8)]
    [InlineData((int)NumType.Double, 8)]
    public void 各类型字节宽度(int tRaw, int expected)
    {
        Assert.Equal(expected, ValueCodec.SizeOf((NumType)tRaw));
    }

    [Fact]
    public void 小端字节序_Int32()
    {
        // 1 在 little-endian 下是 01 00 00 00
        var buf = ValueCodec.Encode(1, NumType.Int32);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, buf);
    }

    [Fact]
    public void 小端字节序_Int16()
    {
        var buf = ValueCodec.Encode(0x1234, NumType.Int16);
        Assert.Equal(new byte[] { 0x34, 0x12 }, buf);
    }

    [Fact]
    public void Double一百_符合IEEE754()
    {
        // 100.0 = 0x4059000000000000，小端为 00 00 00 00 00 00 59 40
        var buf = ValueCodec.Encode(100.0, NumType.Double);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x59, 0x40 }, buf);
    }

    [Fact]
    public void Double七千七百七十七_符合IEEE754()
    {
        // 7777.0 实测字节，来自真实内存读取
        var buf = ValueCodec.Encode(7777.0, NumType.Double);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x61, 0xBE, 0x40 }, buf);
        Assert.Equal(7777.0, (double)ValueCodec.Decode(buf, NumType.Double));
    }

    [Theory]
    [InlineData((int)NumType.Byte, "255")]
    [InlineData((int)NumType.Int16, "-1234")]
    [InlineData((int)NumType.UInt16, "65535")]
    [InlineData((int)NumType.Int32, "-123456")]
    [InlineData((int)NumType.Int64, "-9000000000")]
    [InlineData((int)NumType.Float, "3.5")]
    [InlineData((int)NumType.Double, "-0.125")]
    public void 编解码往返一致(int tRaw, string text)
    {
        var t = (NumType)tRaw;
        Assert.True(ValueCodec.TryParse(text, t, out object v));
        byte[] buf = ValueCodec.Encode(v, t);
        Assert.Equal(ValueCodec.SizeOf(t), buf.Length);
        object back = ValueCodec.Decode(buf, t);
        Assert.Equal(v, back);
    }

    [Fact]
    public void 超出类型范围_解析失败()
    {
        Assert.False(ValueCodec.TryParse("256", NumType.Byte, out _));
        Assert.False(ValueCodec.TryParse("-1", NumType.Byte, out _));
        Assert.False(ValueCodec.TryParse("32768", NumType.Int16, out _));
        Assert.False(ValueCodec.TryParse("2147483648", NumType.Int32, out _));
    }

    [Fact]
    public void 非法文本_解析失败()
    {
        Assert.False(ValueCodec.TryParse("abc", NumType.Int32, out _));
        Assert.False(ValueCodec.TryParse("", NumType.Double, out _));
        Assert.False(ValueCodec.TryParse("1.5", NumType.Int32, out _));  // int 不接受小数
    }

    [Fact]
    public void 浮点使用不变文化_不受系统区域影响()
    {
        // 某些区域用逗号作小数点，必须固定用 InvariantCulture
        Assert.True(ValueCodec.TryParse("3.5", NumType.Double, out object v));
        Assert.Equal(3.5, (double)v);
    }

    [Fact]
    public void ToDouble覆盖全部数值类型()
    {
        Assert.Equal(1.0, ValueCodec.ToDouble((byte)1));
        Assert.Equal(-2.0, ValueCodec.ToDouble((short)-2));
        Assert.Equal(3.0, ValueCodec.ToDouble((ushort)3));
        Assert.Equal(-4.0, ValueCodec.ToDouble(-4));
        Assert.Equal(5.0, ValueCodec.ToDouble(5u));
        Assert.Equal(-6.0, ValueCodec.ToDouble(-6L));
        Assert.Equal(7.0, ValueCodec.ToDouble(7ul));
        Assert.Equal(8.5, ValueCodec.ToDouble(8.5f));
        Assert.Equal(9.5, ValueCodec.ToDouble(9.5));
    }

    [Fact]
    public void ToDouble对非数值返回NaN()
    {
        Assert.True(double.IsNaN(ValueCodec.ToDouble("not a number")));
        Assert.True(double.IsNaN(ValueCodec.ToDouble(null)));
    }

    [Fact]
    public void Format浮点用往返格式_不丢精度()
    {
        // "R" 格式保证 round-trip，避免 0.1 显示成 0.10000000000000001 这类问题
        Assert.Equal("0.1", ValueCodec.Format(0.1));
        Assert.Equal("3.5", ValueCodec.Format(3.5f));
        Assert.Equal("100", ValueCodec.Format(100));
    }

    [Fact]
    public void Int64超过2的53次方_不丢精度()
    {
        // switch 表达式会把所有分支统一推断为 double，
        // 超过 2^53 的整数转 double 会丢精度。这条用例专门锁住它。
        long big = 9007199254740993L;   // 2^53 + 1，double 无法精确表示
        byte[] buf = ValueCodec.Encode(big, NumType.Int64);
        object back = ValueCodec.Decode(buf, NumType.Int64);

        Assert.IsType<long>(back);
        Assert.Equal(big, (long)back);
        Assert.NotEqual((long)(double)big, (long)back);  // 证明真的没走 double
    }

    [Fact]
    public void UInt64最大值_不丢精度()
    {
        ulong max = ulong.MaxValue;
        byte[] buf = ValueCodec.Encode(max, NumType.UInt64);
        object back = ValueCodec.Decode(buf, NumType.UInt64);
        Assert.IsType<ulong>(back);
        Assert.Equal(max, (ulong)back);
    }

    [Fact]
    public void 解码返回类型与声明类型一致()
    {
        Assert.IsType<byte>(ValueCodec.Decode(new byte[1], NumType.Byte));
        Assert.IsType<short>(ValueCodec.Decode(new byte[2], NumType.Int16));
        Assert.IsType<ushort>(ValueCodec.Decode(new byte[2], NumType.UInt16));
        Assert.IsType<int>(ValueCodec.Decode(new byte[4], NumType.Int32));
        Assert.IsType<uint>(ValueCodec.Decode(new byte[4], NumType.UInt32));
        Assert.IsType<long>(ValueCodec.Decode(new byte[8], NumType.Int64));
        Assert.IsType<ulong>(ValueCodec.Decode(new byte[8], NumType.UInt64));
        Assert.IsType<float>(ValueCodec.Decode(new byte[4], NumType.Float));
        Assert.IsType<double>(ValueCodec.Decode(new byte[8], NumType.Double));
    }

    [Fact]
    public void 负数编码正确()
    {
        // -1 在 int32 下是全 FF
        var buf = ValueCodec.Encode(-1, NumType.Int32);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, buf);
        Assert.Equal(-1, (int)ValueCodec.Decode(buf, NumType.Int32));
    }
}
