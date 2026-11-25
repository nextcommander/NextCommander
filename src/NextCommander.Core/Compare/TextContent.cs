using System.Text;
using NextCommander.Core.Text;

namespace NextCommander.Core.Compare;

/// <summary>
/// A text file as lines, with what is needed to write it back the same way: its encoding, byte order mark, line ends and
/// whether its last line ends (patches applied to files, reports).
/// </summary>
public sealed class TextContent
{
    public List<string> Lines { get; set; } = [];

    public Encoding Encoding { get; set; } = EncodingDetector.Utf8NoBom;

    public bool Bom { get; set; }

    public string NewLine { get; set; } = "\n";

    public bool EndsWithNewline { get; set; } = true;

    /// <summary>A new, empty file (lines end as on this system).</summary>
    public static TextContent Empty() => new() { NewLine = Environment.NewLine };

    /// <summary>The text of a file's bytes (encoding guessed, line ends of any kind).</summary>
    public static TextContent Read(byte[] data)
    {
        EncodingDetector.EnsureCodePages();
        var encoding = EncodingDetector.Detect(data, out var bom, complete: true);
        var text = encoding.GetString(data, bom, data.Length - bom);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1); // the final line break does not start a new line
        }
        return new TextContent
        {
            Lines = lines,
            Encoding = encoding,
            Bom = bom > 0,
            NewLine = text.Length == 0 ? Environment.NewLine : EncodingDetector.DetectNewLine(text),
            EndsWithNewline = text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r'),
        };
    }

    /// <summary>The bytes to write (characters the encoding has not are an error, not a question mark).</summary>
    public byte[] Bytes()
    {
        var text = Lines.Count == 0 ? string.Empty : string.Join(NewLine, Lines) + (EndsWithNewline ? NewLine : string.Empty);
        var strict = Encoding.GetEncoding(Encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        byte[] preamble = !Bom ? [] : Encoding.CodePage switch
        {
            65001 => [0xEF, 0xBB, 0xBF],
            1200 => [0xFF, 0xFE],
            1201 => [0xFE, 0xFF],
            _ => [],
        };
        return [.. preamble, .. strict.GetBytes(text)];
    }
}
