using System.IO;
using System.Text;

namespace Sonar.Services;

/// <summary>
/// 极简 protobuf 编解码（Python protobuf_mini.py 的 C# 移植）。
/// 只覆盖贴吧 App 接口需要的 varint / length-delimited 两种 wire type。
/// </summary>
public static class Pb
{
    public const int WireVarint = 0;
    public const int Wire64Bit = 1;
    public const int WireLen = 2;
    public const int Wire32Bit = 5;

    public readonly record struct Field(int WireType, object Value);

    public static byte[] EncodeVarint(ulong value)
    {
        var buffer = new List<byte>(10);
        while (true)
        {
            var chunk = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                buffer.Add((byte)(chunk | 0x80));
            }
            else
            {
                buffer.Add(chunk);
                return buffer.ToArray();
            }
        }
    }

    public static byte[] Key(int field, int wireType) =>
        EncodeVarint(((ulong)field << 3) | (uint)wireType);

    public static byte[] PutInt(int field, long value) =>
        Concat(Key(field, WireVarint), EncodeVarint(unchecked((ulong)value)));

    public static byte[] PutStr(int field, string? value)
    {
        var raw = Encoding.UTF8.GetBytes(value ?? "");
        return Concat(Key(field, WireLen), EncodeVarint((ulong)raw.Length), raw);
    }

    public static byte[] PutMsg(int field, byte[]? payload)
    {
        payload ??= Array.Empty<byte>();
        return Concat(Key(field, WireLen), EncodeVarint((ulong)payload.Length), payload);
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    public static (ulong Value, int Position) ReadVarint(byte[] data, int position)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (position >= data.Length) throw new InvalidDataException("varint 越界");
            var b = data[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return (result, position);
            shift += 7;
            if (shift > 70) throw new InvalidDataException("varint 过长");
        }
    }

    public static Dictionary<int, List<Field>> Decode(byte[] data)
    {
        var fields = new Dictionary<int, List<Field>>();
        var position = 0;
        while (position < data.Length)
        {
            ulong tag;
            try
            {
                (tag, position) = ReadVarint(data, position);
            }
            catch
            {
                break;
            }

            var fieldNo = (int)(tag >> 3);
            var wire = (int)(tag & 0x07);
            object value;
            try
            {
                switch (wire)
                {
                    case WireVarint:
                    {
                        var (v, next) = ReadVarint(data, position);
                        position = next;
                        value = v;
                        break;
                    }
                    case WireLen:
                    {
                        var (length, next) = ReadVarint(data, position);
                        position = next;
                        var len = (int)Math.Min(length, int.MaxValue);
                        if (position + len > data.Length) throw new InvalidDataException("长度越界");
                        value = data.AsSpan(position, len).ToArray();
                        position += len;
                        break;
                    }
                    case Wire64Bit:
                        if (position + 8 > data.Length) throw new InvalidDataException("64bit 越界");
                        value = data.AsSpan(position, 8).ToArray();
                        position += 8;
                        break;
                    case Wire32Bit:
                        if (position + 4 > data.Length) throw new InvalidDataException("32bit 越界");
                        value = data.AsSpan(position, 4).ToArray();
                        position += 4;
                        break;
                    default:
                        return fields;
                }
            }
            catch
            {
                break;
            }

            if (!fields.TryGetValue(fieldNo, out var list))
            {
                list = new List<Field>();
                fields[fieldNo] = list;
            }
            list.Add(new Field(wire, value));
        }
        return fields;
    }

    public static string GetStr(IReadOnlyDictionary<int, List<Field>> fields, int field, string fallback = "")
    {
        if (fields.TryGetValue(field, out var list))
        {
            foreach (var item in list)
            {
                if (item.WireType == WireLen && item.Value is byte[] raw)
                {
                    return Encoding.UTF8.GetString(raw);
                }
            }
        }
        return fallback;
    }

    public static long GetInt(IReadOnlyDictionary<int, List<Field>> fields, int field, long fallback = 0)
    {
        if (fields.TryGetValue(field, out var list))
        {
            foreach (var item in list)
            {
                if (item.WireType == WireVarint && item.Value is ulong value)
                {
                    return unchecked((long)value);
                }
            }
        }
        return fallback;
    }

    public static List<byte[]> GetMessages(IReadOnlyDictionary<int, List<Field>> fields, int field)
    {
        var result = new List<byte[]>();
        if (fields.TryGetValue(field, out var list))
        {
            foreach (var item in list)
            {
                if (item.WireType == WireLen && item.Value is byte[] raw) result.Add(raw);
            }
        }
        return result;
    }

    public static byte[]? GetMessage(IReadOnlyDictionary<int, List<Field>> fields, int field)
    {
        var messages = GetMessages(fields, field);
        return messages.Count > 0 ? messages[0] : null;
    }
}
