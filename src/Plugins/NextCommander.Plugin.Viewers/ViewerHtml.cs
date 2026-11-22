using System.Text;
using NextCommander.Core.Localization;

namespace NextCommander.Plugin.Viewers;

/// <summary>The pages the HTML viewers build: one look (light or dark), texts translated, values escaped.</summary>
internal sealed class ViewerHtml
{
    private readonly StringBuilder _html = new();

    public ViewerHtml(string title, string? subtitle, bool dark)
    {
        var (text, muted, line, head, accent, card) = dark
            ? ("#e6e6e6", "#9a9a9a", "#3c3c3c", "#2a2a2a", "#6cb6ff", "#262626")
            : ("#1f1f1f", "#6b6b6b", "#dcdcdc", "#f3f3f3", "#0b6bcb", "#fafafa");
        _html.Append("<html><head><style>")
            .Append("body { font-family: Segoe UI, Ubuntu, Helvetica, Arial, sans-serif; font-size: 10pt; color: ").Append(text).Append("; margin: 12px; }")
            .Append("h1 { font-size: 15pt; margin: 0 0 4px 0; }")
            .Append("h2 { font-size: 12pt; margin: 18px 0 6px 0; color: ").Append(accent).Append("; }")
            .Append(".muted { color: ").Append(muted).Append("; }")
            .Append("table { border-collapse: collapse; margin: 4px 0 8px 0; }")
            .Append("th, td { border: 1px solid ").Append(line).Append("; padding: 3px 7px; text-align: left; vertical-align: top; }")
            .Append("th { background-color: ").Append(head).Append("; font-weight: normal; color: ").Append(muted).Append("; }")
            .Append("table.kv th { width: 170px; }")
            .Append(".card { border: 1px solid ").Append(line).Append("; background-color: ").Append(card).Append("; padding: 8px 12px; margin: 0 0 10px 0; }")
            .Append("pre, code { font-family: Cascadia Mono, Consolas, DejaVu Sans Mono, monospace; font-size: 9pt; }")
            .Append("pre { border: 1px solid ").Append(line).Append("; background-color: ").Append(head).Append("; padding: 6px; white-space: pre-wrap; }")
            .Append(".ok { color: #2e9d4f; } .bad { color: #d9483b; }")
            .Append("</style></head><body>");
        _html.Append("<h1>").Append(E(title)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            _html.Append("<div class=\"muted\">").Append(E(subtitle)).Append("</div>");
        }
    }

    public static string T(string text) => Localizer.T(text);

    /// <summary>Text for HTML: the markup characters escaped, letters of every language kept as they are.</summary>
    public static string E(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return text ?? string.Empty;
        }
        var html = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            html.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
        return html.ToString();
    }

    /// <summary>A section title (translated).</summary>
    public ViewerHtml Heading(string text)
    {
        _html.Append("<h2>").Append(E(T(text))).Append("</h2>");
        return this;
    }

    /// <summary>A table of names (translated) and values; empty values are left out.</summary>
    public ViewerHtml Facts(IEnumerable<(string Name, string? Value)> rows)
    {
        var shown = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (shown.Count == 0)
        {
            return this;
        }
        _html.Append("<table class=\"kv\">");
        foreach (var (name, value) in shown)
        {
            _html.Append("<tr><th>").Append(E(T(name))).Append("</th><td>").Append(E(value)).Append("</td></tr>");
        }
        _html.Append("</table>");
        return this;
    }

    /// <summary>A table with a header row (translated) and rows of values.</summary>
    public ViewerHtml Table(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        _html.Append("<table><tr>");
        foreach (var name in header)
        {
            _html.Append("<th>").Append(E(T(name))).Append("</th>");
        }
        _html.Append("</tr>");
        foreach (var row in rows)
        {
            _html.Append("<tr>");
            foreach (var value in row)
            {
                _html.Append("<td>").Append(E(value)).Append("</td>");
            }
            _html.Append("</tr>");
        }
        _html.Append("</table>");
        return this;
    }

    /// <summary>A line of text (not translated: it is the file's).</summary>
    public ViewerHtml Paragraph(string? text, string? cssClass = null)
    {
        _html.Append(cssClass is null ? "<p>" : $"<p class=\"{cssClass}\">").Append(E(text)).Append("</p>");
        return this;
    }

    /// <summary>A message of the viewer (translated).</summary>
    public ViewerHtml Note(string text) => Paragraph(T(text), "muted");

    public ViewerHtml Pre(string? text)
    {
        _html.Append("<pre>").Append(E(text)).Append("</pre>");
        return this;
    }

    /// <summary>HTML made by the viewer itself (already safe).</summary>
    public ViewerHtml Raw(string html)
    {
        _html.Append(html);
        return this;
    }

    public override string ToString() => _html + "</body></html>";

    /// <summary>A size as "1.5 MB".</summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} bytes" : value.ToString(value >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>The whole content of the viewed stream (at most <paramref name="limit"/> bytes: a bigger file is refused).</summary>
    public static async Task<byte[]> ReadAllAsync(Stream content, long limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                throw new IOException(T($"The file is too big for this view (more than {Size(limit)})."));
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Text with its encoding found from a byte order mark (UTF-8 without one).</summary>
    public static string Text(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
