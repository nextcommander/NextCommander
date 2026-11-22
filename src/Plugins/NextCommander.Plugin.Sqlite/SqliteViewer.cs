using System.Globalization;
using System.Net;
using System.Text;
using NextCommander.Core.Localization;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Sqlite;

/// <summary>
/// The Lister (F3) on a SQLite database: its tables and views with their row counts, columns (type, key, NOT NULL,
/// default) and first rows, and the indexes. The file is read in place when it is a local one, otherwise through a
/// temporary copy.
/// </summary>
public sealed class SqliteViewer : IViewerPlugin
{
    /// <summary>The rows shown of each table.</summary>
    internal static int PreviewRows { get; set; } = 10;

    /// <summary>A long text or BLOB is cut to this many characters in the preview.</summary>
    private const int CellLimit = 80;

    public string Name => "SQLite";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        SqliteFileSystemProvider.IsDatabase(header) && SqliteDatabase.IsAvailable;

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        string? temp = null;
        try
        {
            var path = content is FileStream { Name: { } name } && File.Exists(name) ? name : null;
            if (path is null)
            {
                temp = Path.Combine(Path.GetTempPath(), "nc-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
                await using (var copy = File.Create(temp))
                {
                    await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                }
                path = temp;
            }
            return await Task.Run(() => Overview(path, fileName, dark, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    internal static ViewerContent Overview(string path, string fileName, bool dark, CancellationToken cancellationToken)
    {
        using var db = SqliteDatabase.Open(path);
        var objects = db.Query("SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY type, name", out _)
            .Select(r => ((string)r[0]!, (string)r[1]!)).ToList();
        var indexes = db.Query("SELECT name, tbl_name, sql FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY tbl_name, name", out _)
            .Select(r => ((string)r[0]!, (string)r[1]!, (string)r[2]!)).ToList();
        var tableCount = objects.Count(o => o.Item2 == "table");
        var viewCount = objects.Count - tableCount;

        var html = new StringBuilder();
        var (text, muted, line, head, accent) = dark
            ? ("#e6e6e6", "#9a9a9a", "#3c3c3c", "#2a2a2a", "#6cb6ff")
            : ("#1f1f1f", "#6b6b6b", "#dcdcdc", "#f3f3f3", "#0b6bcb");
        html.Append(CultureInfo.InvariantCulture, $"""
            <html><head><style>
            body {"{"} font-family: Segoe UI, Ubuntu, Helvetica, Arial, sans-serif; font-size: 10pt; color: {text}; margin: 12px; {"}"}
            h1 {"{"} font-size: 15pt; margin: 0 0 4px 0; {"}"}
            h2 {"{"} font-size: 12pt; margin: 18px 0 4px 0; color: {accent}; {"}"}
            .muted {"{"} color: {muted}; {"}"}
            table {"{"} border-collapse: collapse; margin: 4px 0 8px 0; {"}"}
            th, td {"{"} border: 1px solid {line}; padding: 2px 6px; text-align: left; vertical-align: top; {"}"}
            th {"{"} background-color: {head}; {"}"}
            td.num {"{"} text-align: right; {"}"}
            td.null {"{"} color: {muted}; font-style: italic; {"}"}
            code {"{"} font-family: Cascadia Mono, Consolas, DejaVu Sans Mono, monospace; {"}"}
            </style></head><body>
            """);
        html.Append(CultureInfo.InvariantCulture, $"<h1>{E(fileName)}</h1><div class=\"muted\">");
        html.Append(E(T($"{tableCount} tables"))).Append(" · ").Append(E(T($"{viewCount} views"))).Append(" · ").Append(E(T($"{indexes.Count} indexes")));
        html.Append(" · SQLite ").Append(E(SqliteDatabase.Version)).Append("</div>");

        if (objects.Count == 0)
        {
            html.Append("<p>").Append(E(T("The database has no tables."))).Append("</p>");
        }
        foreach (var (name, kind) in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quoted = SqliteDatabase.Quote(name);
            string count;
            try
            {
                count = T($"{Convert.ToInt64(db.Scalar("SELECT count(*) FROM " + quoted), CultureInfo.InvariantCulture):N0} rows");
            }
            catch (IOException ex)
            {
                count = ex.Message;
            }
            html.Append("<h2>").Append(E(name)).Append("</h2><div class=\"muted\">")
                .Append(E(kind == "view" ? T("View") : T("Table"))).Append(" · ").Append(E(count)).Append("</div>");

            // the columns: name, type, primary key, NOT NULL, default
            var columns = db.Query("SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(" + Literal(name) + ")", out _).ToList();
            if (columns.Count > 0)
            {
                html.Append("<table><tr><th>").Append(E(T("Column"))).Append("</th><th>").Append(E(T("Type"))).Append("</th><th>")
                    .Append(E(T("Key"))).Append("</th><th>").Append(E(T("Not null"))).Append("</th><th>").Append(E(T("Default"))).Append("</th></tr>");
                foreach (var c in columns)
                {
                    html.Append("<tr><td>").Append(E((string)c[0]!)).Append("</td><td>").Append(E(c[1] as string ?? string.Empty))
                        .Append("</td><td>").Append(c[4] is long pk && pk > 0 ? E(T("Primary key")) : string.Empty)
                        .Append("</td><td>").Append(c[2] is long nn && nn != 0 ? "✓" : string.Empty)
                        .Append("</td><td><code>").Append(E(c[3] as string ?? string.Empty)).Append("</code></td></tr>");
                }
                html.Append("</table>");
            }

            // the first rows
            try
            {
                var rows = db.Query($"SELECT * FROM {quoted} LIMIT {PreviewRows}", out var names).ToList();
                if (rows.Count > 0)
                {
                    html.Append("<div class=\"muted\">").Append(E(T("First rows"))).Append("</div><table><tr>");
                    foreach (var n in names)
                    {
                        html.Append("<th>").Append(E(n)).Append("</th>");
                    }
                    html.Append("</tr>");
                    foreach (var row in rows)
                    {
                        html.Append("<tr>");
                        foreach (var value in row)
                        {
                            html.Append(value switch
                            {
                                null => "<td class=\"null\">NULL</td>",
                                long or double => "<td class=\"num\">" + E(SqliteFileSystemProvider.Text(value)) + "</td>",
                                byte[] bytes => "<td class=\"null\">" + E(T($"BLOB, {bytes.Length} bytes")) + "</td>",
                                _ => "<td>" + E(Cut(SqliteFileSystemProvider.Text(value))) + "</td>",
                            });
                        }
                        html.Append("</tr>");
                    }
                    html.Append("</table>");
                }
            }
            catch (IOException ex)
            {
                html.Append("<div class=\"muted\">").Append(E(ex.Message)).Append("</div>");
            }
        }

        if (indexes.Count > 0)
        {
            html.Append("<h2>").Append(E(T("Indexes"))).Append("</h2><table>");
            foreach (var (name, table, sql) in indexes)
            {
                html.Append("<tr><td>").Append(E(name)).Append("</td><td>").Append(E(table)).Append("</td><td><code>").Append(E(sql)).Append("</code></td></tr>");
            }
            html.Append("</table>");
        }
        html.Append("</body></html>");
        return new ViewerContent { Html = html.ToString(), Status = T($"{tableCount} tables") };
    }

    private static string T(string text) => Localizer.T(text);

    private static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>A name as a SQL string literal ('it''s').</summary>
    private static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Cut(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= CellLimit ? single : string.Concat(single.AsSpan(0, CellLimit), "…");
    }
}
