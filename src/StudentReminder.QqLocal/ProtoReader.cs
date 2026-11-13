using System.Text;

namespace StudentReminder.QqLocal;

/// <summary>
/// Minimal protobuf wire-format reader for extracting QQ NT message text. QQ encodes a
/// message body in the numeric column "40800" as a nested protobuf; the plain-text of each
/// segment lives in the length-delimited field 45101. We walk nested messages recursively,
/// concatenating every 45101 field, and tolerate unknown fields instead of failing.
/// </summary>
public static class ProtoReader
{
    private const int TextField = 45101;
    private const int MaxDepth = 24;

    /// <summary>Concatenate every 45101 text field found at any nesting depth.</summary>
    public static string ExtractText(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        Walk(data, 0, sb);
        return sb.ToString();
    }

    private static void Walk(ReadOnlySpan<byte> data, int depth, StringBuilder sb)
    {
        if (depth > MaxDepth || data.Length == 0) return;
        var pos = 0;
        while (pos < data.Length)
        {
            var (tag, tagBytes) = ReadVarint(data[pos..]);
            if (tagBytes == 0) return;
            pos += tagBytes;
            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            switch (wireType)
            {
                case 0:
                    var (_, varintBytes) = ReadVarint(data[pos..]);
                    if (varintBytes == 0) return;
                    pos += varintBytes;
                    break;
                case 1:
                    if (pos + 8 > data.Length) return;
                    pos += 8;
                    break;
                case 2:
                    var (len, lenBytes) = ReadVarint(data[pos..]);
                    if (lenBytes == 0 || (long)len > data.Length - pos - lenBytes) return;
                    pos += lenBytes;
                    var payload = data.Slice(pos, (int)len);
                    pos += (int)len;
                    if (fieldNumber == TextField)
                    {
                        sb.Append(Encoding.UTF8.GetString(payload));
                    }
                    else if (LooksLikeMessage(payload))
                    {
                        Walk(payload, depth + 1, sb);
                    }
                    break;
                case 5:
                    if (pos + 4 > data.Length) return;
                    pos += 4;
                    break;
                default:
                    return;
            }
        }
    }

    private static bool LooksLikeMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;
        var (tag, consumed) = ReadVarint(data);
        return consumed != 0 && (tag & 0x7) <= 5 && (tag >> 3) != 0;
    }

    private static (ulong Value, int Consumed) ReadVarint(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (var i = 0; i < 10 && i < data.Length; i++)
        {
            var b = data[i];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return (value, i + 1);
        }
        return (0, 0);
    }
}
