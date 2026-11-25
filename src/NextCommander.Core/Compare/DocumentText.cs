using System.Net;
using System.Text.RegularExpressions;
using NextCommander.Core.Documents;

namespace NextCommander.Core.Compare;

/// <summary>
/// The text of documents for Compare by content (the compare and merge plan, phase 7; 25 November 2026): Word, Excel, PowerPoint
/// and OpenDocument files through <see cref="OfficeHtml"/> (paragraphs, table rows with their cells separated by tabs,
/// slides), PDF files page by page through PDFium. Compared as text, read-only.
/// </summary>
public static partial class DocumentText
{
    /// <summary>Whether a file of this extension is compared by its text.</summary>
    public static bool Supports(string extension) =>
        OfficeHtml.Supports(extension) || (extension.Equals("pdf", StringComparison.OrdinalIgnoreCase) && PdfDocument.IsAvailable);

    /// <summary>The lines of a document's text; an <see cref="IOException"/> / <see cref="InvalidDataException"/> when it cannot be read.</summary>
    public static List<string> Lines(string extension, byte[] data)
    {
        if (extension.Equals("pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(data);
            var lines = new List<string>();
            for (var page = 0; page < pdf.PageCount; page++)
            {
                lines.Add($"── {page + 1} ──"); // the pages keep their places
                lines.AddRange(Split(pdf.PageText(page)));
            }
            return lines;
        }
        using var stream = new MemoryStream(data, writable: false);
        return FromHtml(OfficeHtml.ToHtml(extension, stream));
    }

    [GeneratedRegex(@"<(script|style)\b.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"</(p|h[1-6]|li|tr|div|table|section|article|blockquote|pre|caption)>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineEnds();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellEnds();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    /// <summary>The text of HTML: a line per paragraph, heading, list item and table row (cells separated by tabs).</summary>
    public static List<string> FromHtml(string html)
    {
        var text = Hidden().Replace(html, string.Empty);
        text = LineEnds().Replace(text, "\n");
        text = CellEnds().Replace(text, "\t");
        text = WebUtility.HtmlDecode(Tags().Replace(text, string.Empty));
        return Split(text);
    }

    /// <summary>The lines of a text, their spaces at the ends left out, without empty lines in a row.</summary>
    private static List<string> Split(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Replace(' ', ' ').TrimEnd('\t', ' ').Trim(' ');
            if (line.Length > 0 || (lines.Count > 0 && lines[^1].Length > 0))
            {
                lines.Add(line);
            }
        }
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }
}
