using System.Text;

namespace NextCommander.Core.Text;

/// <summary>
/// Line navigation by byte position, without an index (works for files of any size):
/// the scroll position is a byte offset, lines are found by scanning for '\n' around it. Lines longer than
/// <see cref="MaxLineBytes"/> are split into pieces of that size.
/// </summary>
public sealed class LineNavigator
{
    private const int ScanBack = 256 * 1024;

    private readonly IRandomAccessSource _source;

    public LineNavigator(IRandomAccessSource source, Encoding encoding, int dataStart = 0, int maxLineBytes = 4096)
    {
        _source = source;
        Encoding = encoding;
        CharSize = encoding.CodePage is 1200 or 1201 ? 2 : 1;
        BigEndian = encoding.CodePage == 1201;
        DataStart = dataStart;
        MaxLineBytes = Math.Max(16, maxLineBytes / CharSize * CharSize);
    }

    public Encoding Encoding { get; }

    /// <summary>First byte after the byte order mark.</summary>
    public int DataStart { get; }

    public int MaxLineBytes { get; }

    public int CharSize { get; }

    private bool BigEndian { get; }

    public long Length => _source.Length;

    /// <summary>Start of the line after the one starting at <paramref name="lineStart"/>; <see cref="Length"/> at the end.</summary>
    public long NextLineStart(long lineStart)
    {
        var buffer = new byte[MaxLineBytes];
        var n = _source.ReadFully(lineStart, buffer);
        var newline = FindNewline(buffer.AsSpan(0, n), forward: true);
        if (newline >= 0)
        {
            return lineStart + newline + CharSize;
        }
        return lineStart + n;
    }

    /// <summary>Start of the line that contains byte <paramref name="position"/>.</summary>
    public long LineStartOf(long position)
    {
        position = Math.Clamp(position, DataStart, Math.Max(DataStart, Length));
        position = DataStart + (position - DataStart) / CharSize * CharSize;
        var from = Math.Max(DataStart, position - ScanBack);
        from = DataStart + (from - DataStart) / CharSize * CharSize;
        var buffer = new byte[(int)(position - from)];
        var n = _source.ReadFully(from, buffer);
        var newline = FindNewline(buffer.AsSpan(0, n), forward: false);
        long lineStart;
        if (newline >= 0)
        {
            lineStart = from + newline + CharSize;
        }
        else if (from == DataStart)
        {
            lineStart = DataStart;
        }
        else
        {
            // Extremely long line: just cut at the piece size.
            return DataStart + (position - DataStart) / MaxLineBytes * MaxLineBytes;
        }
        return lineStart + (position - lineStart) / MaxLineBytes * MaxLineBytes;
    }

    /// <summary>Start of the line before the one starting at <paramref name="lineStart"/>.</summary>
    public long PreviousLineStart(long lineStart) =>
        lineStart <= DataStart ? DataStart : LineStartOf(lineStart - CharSize);

    /// <summary>Reads <paramref name="count"/> lines starting at <paramref name="lineStart"/>.</summary>
    public IReadOnlyList<TextLine> ReadLines(long lineStart, int count)
    {
        var result = new List<TextLine>(count);
        var pos = Math.Max(lineStart, DataStart);
        while (result.Count < count && pos < Length)
        {
            var next = NextLineStart(pos);
            if (next <= pos)
            {
                break;
            }
            var bytes = _source.ReadBytes(pos, (int)(next - pos));
            result.Add(new TextLine(pos, next, Decode(bytes)));
            pos = next;
        }
        return result;
    }

    /// <summary>Line start <paramref name="lines"/> lines before or after <paramref name="lineStart"/>.</summary>
    public long Move(long lineStart, int lines)
    {
        var pos = lineStart;
        for (var i = 0; i < Math.Abs(lines); i++)
        {
            var next = lines > 0 ? NextLineStart(pos) : PreviousLineStart(pos);
            if (next == pos || (lines > 0 && next >= Length))
            {
                break;
            }
            pos = next;
        }
        return pos;
    }

    /// <summary>Decodes a line without its line break; control characters become visible placeholders.</summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.GetString(bytes);
        var end = text.Length;
        while (end > 0 && text[end - 1] is '\n' or '\r')
        {
            end--;
        }
        var sb = new StringBuilder(end);
        for (var i = 0; i < end; i++)
        {
            var c = text[i];
            sb.Append(c switch
            {
                '\t' => "    ",
                < ' ' => "·",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    private int FindNewline(ReadOnlySpan<byte> data, bool forward)
    {
        if (CharSize == 1)
        {
            return forward ? data.IndexOf((byte)'\n') : data.LastIndexOf((byte)'\n');
        }
        ReadOnlySpan<byte> pattern = BigEndian ? [0, (byte)'\n'] : [(byte)'\n', 0];
        if (forward)
        {
            for (var i = 0; i + 1 < data.Length; i += 2)
            {
                if (data[i] == pattern[0] && data[i + 1] == pattern[1])
                {
                    return i;
                }
            }
        }
        else
        {
            for (var i = (data.Length & ~1) - 2; i >= 0; i -= 2)
            {
                if (data[i] == pattern[0] && data[i + 1] == pattern[1])
                {
                    return i;
                }
            }
        }
        return -1;
    }
}

/// <summary>One displayed line: byte range [Start, End) and its text.</summary>
public sealed record TextLine(long Start, long End, string Text);
