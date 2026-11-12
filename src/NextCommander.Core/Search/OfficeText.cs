using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Search;

/// <summary>Plain text of zip-based documents (Office XML, OpenDocument, EPUB) for content search.</summary>
public static partial class OfficeText
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "docx", "docm", "dotx", "xlsx", "xlsm", "pptx", "pptm", "odt", "ods", "odp", "odg", "ott", "epub",
    };

    public static bool IsSupported(string extension) => Extensions.Contains(extension);

    /// <summary>Extracted text, or null when the file is not a readable zip.</summary>
    public static async Task<string?> ExtractAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var sb = new StringBuilder();
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsTextPart(entry.FullName) || entry.Length > 64 * 1024 * 1024)
                {
                    continue;
                }
                await using var part = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(part, Encoding.UTF8);
                var xml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                sb.Append(XmlToText(xml)).Append('\n');
            }
            return sb.ToString();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool IsTextPart(string name)
    {
        if (name.Contains("_rels/", StringComparison.Ordinal) || name.StartsWith('['))
        {
            return false;
        }
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".xhtml", StringComparison.Ordinal) || lower.EndsWith(".html", StringComparison.Ordinal) || lower.EndsWith(".htm", StringComparison.Ordinal))
        {
            return true;
        }
        if (!lower.EndsWith(".xml", StringComparison.Ordinal))
        {
            return false;
        }
        // Skip styles, settings, themes, metadata: only parts with document text.
        return lower is "content.xml" or "word/document.xml" or "xl/sharedstrings.xml"
            || lower.StartsWith("word/header", StringComparison.Ordinal) || lower.StartsWith("word/footer", StringComparison.Ordinal)
            || lower.StartsWith("word/footnotes", StringComparison.Ordinal) || lower.StartsWith("word/endnotes", StringComparison.Ordinal)
            || lower.StartsWith("word/comments", StringComparison.Ordinal)
            || lower.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
            || lower.StartsWith("ppt/slides/slide", StringComparison.Ordinal) || lower.StartsWith("ppt/notesslides/", StringComparison.Ordinal);
    }

    /// <summary>Strips tags; paragraph-like closing tags become line breaks so words of different paragraphs don't join.</summary>
    public static string XmlToText(string xml)
    {
        var withBreaks = BreakTags().Replace(xml, "\n");
        var noTags = AnyTag().Replace(withBreaks, string.Empty);
        return WebUtility.HtmlDecode(noTags);
    }

    [GeneratedRegex(@"</(?:w:p|a:p|text:p|text:h|p|div|li|h[1-6]|si|c|row|tr|td)>|<(?:w:br|w:tab|text:tab|text:s|text:line-break|br)\b[^>]*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex AnyTag();
}
