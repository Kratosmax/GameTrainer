using System.Buffers.Binary;
using System.Globalization;

namespace GameTrainer.Memory;

internal enum NumType
{
    Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double
}

internal static class ValueCodec
{
    public static int SizeOf(NumType t) => t switch
    {
        NumType.Byte => 1,
        NumType.Int16 or NumType.UInt16 => 2,
        NumType.Int32 or NumType.UInt32 or NumType.Float => 4,
        NumType.Int64 or NumType.UInt64 or NumType.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    public static bool TryParse(string s, NumType t, out object value)
    {
        value = null;
        switch (t)
        {
            case NumType.Byte:
                if (byte.TryParse(s, out var b)) { value = b; return true; } return false;
            case NumType.Int16:
                if (short.TryParse(s, out var s16)) { value = s16; return true; } return false;
            case NumType.UInt16:
                if (ushort.TryParse(s, out var u16)) { value = u16; return true; } return false;
            case NumType.Int32:
                if (int.TryParse(s, out var i32)) { value = i32; return true; } return false;
            case NumType.UInt32:
                if (uint.TryParse(s, out var u32)) { value = u32; return true; } return false;
            case NumType.Int64:
                if (long.TryParse(s, out var i64)) { value = i64; return true; } return false;
            case NumType.UInt64:
                if (ulong.TryParse(s, out var u64)) { value = u64; return true; } return false;
            case NumType.Float:
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) { value = f; return true; } return false;
            case NumType.Double:
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { value = d; return true; } return false;
        }
        return false;
    }

    public static byte[] Encode(object value, NumType t)
    {
        byte[] buf = new byte[SizeOf(t)];
        switch (t)
        {
            case NumType.Byte: buf[0] = Convert.ToByte(value, CultureInfo.InvariantCulture); break;
            case NumType.Int16: BinaryPrimitives.WriteInt16LittleEndian(buf, Convert.ToInt16(value, CultureInfo.InvariantCulture)); break;
            case NumType.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(buf, Convert.ToUInt16(value, CultureInfo.InvariantCulture)); break;
            case NumType.Int32: BinaryPrimitives.WriteInt32LittleEndian(buf, Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
            case NumType.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(buf, Convert.ToUInt32(value, CultureInfo.InvariantCulture)); break;
            case NumType.Int64: BinaryPrimitives.WriteInt64LittleEndian(buf, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case NumType.UInt64: BinaryPrimitives.WriteUInt64LittleEndian(buf, Convert.ToUInt64(value, CultureInfo.InvariantCulture)); break;
            case NumType.Float: BinaryPrimitives.WriteSingleLittleEndian(buf, Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
            case NumType.Double: BinaryPrimitives.WriteDoubleLittleEndian(buf, Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
        }
        return buf;
    }

    public static object Decode(ReadOnlySpan<byte> buf, NumType t) => t switch
    {
        NumType.Byte => buf[0],
        NumType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(buf),
        NumType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(buf),
        NumType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(buf),
        NumType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(buf),
        NumType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(buf),
        NumType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(buf),
        NumType.Float => BinaryPrimitives.ReadSingleLittleEndian(buf),
        NumType.Double => BinaryPrimitives.ReadDoubleLittleEndian(buf),
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    /// <summary>把值转换成双精度用于区间比较，只对数值类型有意义。</summary>
    public static double ToDouble(object v) => v switch
    {
        byte b => b,
        short s => s,
        ushort us => us,
        int i => i,
        uint ui => ui,
        long l => l,
        ulong ul => ul,
        float f => f,
        double d => d,
        _ => double.NaN
    };

    public static string Format(object v) => v switch
    {
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v?.ToString() ?? "null"
    };
}
