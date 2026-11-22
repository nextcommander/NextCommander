using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// E-books (.epub): the title, author, publisher, language and cover, the chapters in reading order, and their text
/// with the book's own pictures. Scripts and anything from the internet are left out. At most
/// <see cref="MaxHtml"/> characters of the book are shown (the rest is named).
/// </summary>
public sealed partial class EbookViewer : IViewerPlugin
{
    internal const int MaxHtml = 4_000_000;

    public string Name => "E-book";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        Path.GetExtension(fileName).Equals(".epub", StringComparison.OrdinalIgnoreCase) && header.Length >= 4 && header[0] == 'P' && header[1] == 'K';

    /// <summary>A book as the viewer shows it.</summary>
    internal sealed record Book(string? Title, string? Author, string? Publisher, string? Language, string? Date, string? CoverDataUri,
        List<(string Title, string Html)> Chapters, int Left);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 300L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var book = await Task.Run(() => Read(bytes), cancellationToken).ConfigureAwait(false);
        return new ViewerContent { Html = Page(book, fileName, dark), Status = ViewerHtml.T($"Chapters: {book.Chapters.Count + book.Left}") };
    }

    internal static string Page(Book book, string fileName, bool dark)
    {
        var page = new ViewerHtml(book.Title ?? fileName, book.Author, dark);
        if (book.CoverDataUri is { } cover)
        {
            page.Raw($"<p><img src=\"{cover}\" style=\"max-height: 320px;\"></p>");
        }
        page.Facts([("Author", book.Author), ("Publisher", book.Publisher), ("Language", book.Language), ("Date", book.Date)]);
        if (book.Chapters.Count > 0)
        {
            page.Heading("Contents").Raw("<ol>");
            foreach (var (title, _) in book.Chapters)
            {
                page.Raw("<li>").Raw(ViewerHtml.E(title)).Raw("</li>");
            }
            page.Raw("</ol>");
            foreach (var (_, html) in book.Chapters)
            {
                page.Raw("<div class=\"card\">").Raw(html).Raw("</div>");
            }
        }
        if (book.Left > 0)
        {
            page.Note(ViewerHtml.T($"{book.Left} more chapters are not shown."));
        }
        return page.ToString();
    }

    internal static Book Read(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var container = Xml(zip, "META-INF/container.xml") ?? throw new InvalidDataException("Not an EPUB book (no META-INF/container.xml).");
        var opfPath = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
            ?? throw new InvalidDataException("Not an EPUB book (no package file).");
        var opf = Xml(zip, opfPath) ?? throw new InvalidDataException("Not an EPUB book (the package file is missing).");
        var folder = opfPath.Contains('/', StringComparison.Ordinal) ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : string.Empty;
        string? Meta(string name) => opf.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "metadata")?.Value.Trim();

        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .Select(e => (Id: e.Attribute("id")?.Value ?? string.Empty, Href: Uri.UnescapeDataString(e.Attribute("href")?.Value ?? string.Empty),
                Type: e.Attribute("media-type")?.Value ?? string.Empty, Properties: e.Attribute("properties")?.Value ?? string.Empty))
            .ToList();
        // the cover: EPUB 3 "cover-image", EPUB 2 <meta name="cover" content="id">
        var coverId = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "meta" && e.Attribute("name")?.Value == "cover")?.Attribute("content")?.Value;
        var coverItem = manifest.FirstOrDefault(m => m.Properties.Contains("cover-image", StringComparison.Ordinal)) is { Id.Length: > 0 } c3 ? c3
            : manifest.FirstOrDefault(m => m.Id == coverId);
        string? cover = coverItem.Href is { Length: > 0 } coverHref && Entry(zip, folder + coverHref) is { } coverBytes
            ? $"data:{coverItem.Type};base64,{Convert.ToBase64String(coverBytes)}"
            : null;

        var chapters = new List<(string, string)>();
        var size = 0;
        var left = 0;
        foreach (var idref in opf.Descendants().Where(e => e.Name.LocalName == "itemref").Select(e => e.Attribute("idref")?.Value))
        {
            var item = manifest.FirstOrDefault(m => m.Id == idref);
            if (item.Href is not { Length: > 0 } href || Entry(zip, folder + href) is not { } chapterBytes)
            {
                continue;
            }
            if (size >= MaxHtml)
            {
                left++;
                continue;
            }
            var html = Encoding.UTF8.GetString(chapterBytes);
            var title = TitleOf(html) ?? Path.GetFileNameWithoutExtension(href);
            var chapterFolder = (folder + href) is var full && full.Contains('/', StringComparison.Ordinal) ? full[..(full.LastIndexOf('/') + 1)] : string.Empty;
            var body = Pictures(MailViewer.SafeHtml(html, []), zip, chapterFolder);
            size += body.Length;
            chapters.Add((title, body));
        }
        return new Book(Meta("title"), Meta("creator"), Meta("publisher"), Meta("language"), Meta("date"), cover, chapters, left);
    }

    private static XDocument? Xml(ZipArchive zip, string path)
    {
        if (Entry(zip, path) is not { } bytes)
        {
            return null;
        }
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static byte[]? Entry(ZipArchive zip, string path)
    {
        var normalized = Normalize(path);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, normalized, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > 50L * 1024 * 1024)
        {
            return null;
        }
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>"OEBPS/Text/../Images/a.jpg" → "OEBPS/Images/a.jpg".</summary>
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }
        return string.Join('/', parts);
    }

    private static string? TitleOf(string html) =>
        Heading().Match(html) is { Success: true } m ? WebText(m.Groups[2].Value) : TitleTag().Match(html) is { Success: true } t ? WebText(t.Groups[1].Value) : null;

    private static string? WebText(string html)
    {
        var text = System.Net.WebUtility.HtmlDecode(Tags().Replace(html, string.Empty)).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>The chapter's pictures (relative to it) as data.</summary>
    private static string Pictures(string html, ZipArchive zip, string folder) =>
        ImageSource().Replace(html, m =>
        {
            var src = m.Groups[2].Value;
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || Entry(zip, folder + Uri.UnescapeDataString(src.Split('#')[0])) is not { } bytes)
            {
                return m.Value;
            }
            var type = Path.GetExtension(src).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };
            return $"{m.Groups[1].Value}=\"data:{type};base64,{Convert.ToBase64String(bytes)}\"";
        });

    [GeneratedRegex(@"<h([1-3])[^>]*>(.*?)</h\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, 1000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"(src|xlink:href)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ImageSource();
}
