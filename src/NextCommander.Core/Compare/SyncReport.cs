using System.Globalization;
using System.Net;
using System.Text;

namespace NextCommander.Core.Compare;

/// <summary>A folder compare kept under a name: the two folders and the options (the compare and merge plan, phase 4).</summary>
public sealed class SyncPreset
{
    public string Name { get; set; } = string.Empty;

    public string Left { get; set; } = string.Empty;

    public string Right { get; set; } = string.Empty;

    public SyncOptions Options { get; set; } = new();
}

/// <summary>The result of a folder compare as a CSV file or an HTML page (the compare and merge plan, phase 4).</summary>
public static class SyncReport
{
    public static string StatusText(SyncStatus status) => status switch
    {
        SyncStatus.Equal => "Identical",
        SyncStatus.LeftNewer => "Left newer",
        SyncStatus.RightNewer => "Right newer",
        SyncStatus.Different => "Different",
        SyncStatus.LeftOnly => "Left only",
        SyncStatus.RightOnly => "Right only",
        _ => "File / folder",
    };

    private static string Size(Vfs.VfsEntry? entry) =>
        entry is null || entry.IsDirectory ? string.Empty : entry.Size.ToString(CultureInfo.InvariantCulture);

    private static string Time(Vfs.VfsEntry? entry) =>
        entry is null ? string.Empty : entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>One line per item: path, status, the size and time of each side (RFC 4180 quoting).</summary>
    public static string Csv(IEnumerable<SyncItem> items)
    {
        static string Field(string text) =>
            text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
        var csv = new StringBuilder("Path,Status,Left size,Left modified,Right size,Right modified\r\n");
        foreach (var item in items)
        {
            csv.Append(string.Join(',', new[]
            {
                item.RelativePath + (item.IsDirectory ? "/" : string.Empty), StatusText(item.Status),
                Size(item.Left), Time(item.Left), Size(item.Right), Time(item.Right),
            }.Select(Field))).Append("\r\n");
        }
        return csv.ToString();
    }

    /// <summary>A page of its own (no scripts, no outside files): the two folders, the counts and a table colored by status.</summary>
    public static string Html(IReadOnlyList<SyncItem> items, string left, string right, DateTime created)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var files = items.Where(i => !i.IsDirectory).ToList();
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Folder compare</title><style>")
            .Append("body{font:14px system-ui,sans-serif;margin:24px;color:#18202d}h1{font-size:20px}")
            .Append("table{border-collapse:collapse;width:100%}th,td{padding:4px 8px;border-bottom:1px solid #dde3ec;text-align:left}")
            .Append("td.n{text-align:right;font-variant-numeric:tabular-nums}code{font:13px Consolas,monospace}")
            .Append(".Equal{color:#5d6879}.LeftNewer,.RightNewer,.Different,.TypeMismatch{background:#fff6e5}")
            .Append(".LeftOnly{background:#fdedef}.RightOnly{background:#e9f7ee}")
            .Append("</style></head><body>");
        html.Append("<h1>Folder compare</h1><p><code>").Append(E(left)).Append("</code> ↔ <code>").Append(E(right)).Append("</code><br>")
            .Append(E(created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append("</p><p>")
            .Append(CultureInfo.InvariantCulture, $"{files.Count} files: {files.Count(f => f.Status == SyncStatus.Equal)} identical, ")
            .Append(CultureInfo.InvariantCulture, $"{files.Count(f => f.Status is SyncStatus.LeftNewer or SyncStatus.RightNewer or SyncStatus.Different or SyncStatus.TypeMismatch)} different, ")
            .Append(CultureInfo.InvariantCulture, $"{files.Count(f => f.Status == SyncStatus.LeftOnly)} left only, {files.Count(f => f.Status == SyncStatus.RightOnly)} right only.")
            .Append("</p><table><tr><th>Path</th><th>Status</th><th>Left size</th><th>Left modified</th><th>Right size</th><th>Right modified</th></tr>");
        foreach (var item in items)
        {
            html.Append("<tr class=\"").Append(item.Status).Append("\"><td><code>").Append(E(item.RelativePath + (item.IsDirectory ? "/" : string.Empty)))
                .Append("</code></td><td>").Append(E(StatusText(item.Status)))
                .Append("</td><td class=\"n\">").Append(Size(item.Left)).Append("</td><td>").Append(Time(item.Left))
                .Append("</td><td class=\"n\">").Append(Size(item.Right)).Append("</td><td>").Append(Time(item.Right)).Append("</td></tr>");
        }
        html.Append("</table></body></html>");
        return html.ToString();
    }
}
