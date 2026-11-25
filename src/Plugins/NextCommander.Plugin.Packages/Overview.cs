using System.Globalization;
using System.Net;
using System.Text;
using NextCommander.Core.Localization;

namespace NextCommander.Plugin.Packages;

/// <summary>The Lister's HTML page of a package: a title, sections, tables of details and lists (labels translated, values as they are).</summary>
internal sealed class Overview
{
    private readonly StringBuilder _html = new();

    public Overview(bool dark)
    {
        var (text, muted, line, head, accent) = dark
            ? ("#e6e6e6", "#9a9a9a", "#3c3c3c", "#2a2a2a", "#6cb6ff")
            : ("#1f1f1f", "#6b6b6b", "#dcdcdc", "#f3f3f3", "#0b6bcb");
        _html.Append(CultureInfo.InvariantCulture, $"""
            <html><head><style>
            body {"{"} font-family: Segoe UI, Ubuntu, Helvetica, Arial, sans-serif; font-size: 10pt; color: {text}; margin: 12px; {"}"}
            h1 {"{"} font-size: 15pt; margin: 0 0 4px 0; {"}"}
            h2 {"{"} font-size: 12pt; margin: 18px 0 4px 0; color: {accent}; {"}"}
            .muted {"{"} color: {muted}; {"}"}
            table {"{"} border-collapse: collapse; margin: 4px 0 8px 0; {"}"}
            th, td {"{"} border: 1px solid {line}; padding: 2px 6px; text-align: left; vertical-align: top; {"}"}
            th {"{"} background-color: {head}; {"}"}
            td.num {"{"} text-align: right; {"}"}
            pre {"{"} white-space: pre-wrap; margin: 4px 0; {"}"}
            </style></head><body>
            """);
    }

    public string? Status { get; set; }

    public static string T(string text) => Localizer.T(text);

    public static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>"12.3 MB".</summary>
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
        return unit == 0 ? $"{bytes} {T(units[0])}" : string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {units[unit]}");
    }

    public void Title(string title, params string?[] details)
    {
        _html.Append("<h1>").Append(E(title)).Append("</h1>");
        var shown = details.Where(d => !string.IsNullOrEmpty(d)).ToList();
        if (shown.Count > 0)
        {
            _html.Append("<div class=\"muted\">").Append(string.Join(" · ", shown.Select(E))).Append("</div>");
        }
    }

    public void Section(string title) => _html.Append("<h2>").Append(E(T(title))).Append("</h2>");

    public void Paragraph(string text) => _html.Append("<p>").Append(E(text)).Append("</p>");

    public void Preformatted(string text) => _html.Append("<pre>").Append(E(text)).Append("</pre>");

    /// <summary>A two-column table of labels (translated) and values; empty values are left out.</summary>
    public void Fields(IEnumerable<(string Label, string? Value)> fields)
    {
        var rows = fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (rows.Count == 0)
        {
            return;
        }
        _html.Append("<table>");
        foreach (var (label, value) in rows)
        {
            _html.Append("<tr><th>").Append(E(T(label))).Append("</th><td>").Append(E(value)).Append("</td></tr>");
        }
        _html.Append("</table>");
    }

    /// <summary>A table with headers (translated); columns in <paramref name="numeric"/> are right-aligned.</summary>
    public void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows, params int[] numeric)
    {
        _html.Append("<table><tr>");
        foreach (var header in headers)
        {
            _html.Append("<th>").Append(E(T(header))).Append("</th>");
        }
        _html.Append("</tr>");
        foreach (var row in rows)
        {
            _html.Append("<tr>");
            for (var i = 0; i < row.Count; i++)
            {
                _html.Append(numeric.Contains(i) ? "<td class=\"num\">" : "<td>").Append(E(row[i])).Append("</td>");
            }
            _html.Append("</tr>");
        }
        _html.Append("</table>");
    }

    public void List(IEnumerable<string> items)
    {
        _html.Append("<ul>");
        foreach (var item in items)
        {
            _html.Append("<li>").Append(E(item)).Append("</li>");
        }
        _html.Append("</ul>");
    }

    public string Html => _html + "</body></html>";
}
