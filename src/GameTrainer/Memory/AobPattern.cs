using System.Globalization;

namespace GameTrainer.Memory;

/// <summary>
/// AOB（Array of Bytes）签名。
/// 支持通配符 ?? 与半字节通配 h? / ?h，例如 "48 8B ?? ?? 89 4?" 或 "48 8B ?? ?? 89 4? ?? ?? EB"。
///
/// 设计原则：签名必须唯一命中，否则拒绝写入。宁可写不进去，绝不瞎写。
/// </summary>
internal sealed class AobPattern
{
    private readonly byte[] _values;
    private readonly byte[] _masks; // 0xFF = 必须相等；0xF0/0x0F = 半字节；0x00 = 通配

    public string Source { get; }
    public int Length => _values.Length;

    public AobPattern(string pattern)
    {
        Source = pattern;
        // 按任意空白分割（空格/制表/换行），并过滤空片段
        string[] parts = pattern
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        _values = new byte[parts.Length];
        _masks = new byte[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p is "?" or "??")
            {
                _values[i] = 0x00; _masks[i] = 0x00;
            }
            else if (p.Length == 2)
            {
                if (p[0] == '?')
                {
                    _values[i] = ParseNibble(p[1], low: true); _masks[i] = 0x0F;
                }
                else if (p[1] == '?')
                {
                    _values[i] = ParseNibble(p[0], low: false); _masks[i] = 0xF0;
                }
                else
                {
                    _values[i] = byte.Parse(p, NumberStyles.HexNumber);
                    _masks[i] = 0xFF;
                }
            }
            else
            {
                throw new FormatException($"无法解析 AOB 片段 \"{p}\"（应为 2 位十六进制或通配符）。");
            }
        }

        if (_values.Length == 0) throw new FormatException("AOB 签名为空。");
    }

    private static byte ParseNibble(char c, bool low)
    {
        byte v = byte.Parse(c.ToString(), NumberStyles.HexNumber);
        return low ? v : (byte)(v << 4);
    }

    public bool IsMatchAt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + Length > data.Length) return false;
        for (int i = 0; i < Length; i++)
        {
            byte mask = _masks[i];
            if (mask == 0xFF && data[offset + i] != _values[i]) return false;
            if (mask == 0xF0 && (data[offset + i] & 0xF0) != _values[i]) return false;
            if (mask == 0x0F && (data[offset + i] & 0x0F) != _values[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// 在一段缓冲区内找第一个命中位置（相对偏移），未命中返回 -1。
    /// </summary>
    public int FirstMatch(ReadOnlySpan<byte> data)
    {
        int limit = data.Length - Length;
        for (int i = 0; i <= limit; i++)
            if (IsMatchAt(data, i)) return i;
        return -1;
    }

    /// <summary>
    /// 统计一段缓冲区内命中个数（用于唯一性判断）。超过 maxCount 提前返回。
    /// </summary>
    public int CountMatches(ReadOnlySpan<byte> data, int maxCount = 2)
    {
        int n = 0, limit = data.Length - Length;
        for (int i = 0; i <= limit; i++)
        {
            if (IsMatchAt(data, i))
            {
                n++;
                if (n >= maxCount) return n;
            }
        }
        return n;
    }

    public override string ToString() => Source;
}
