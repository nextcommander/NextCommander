using System.Globalization;
using System.Net;
using System.Text;

namespace NextCommander.Core.Compare;

/// <summary>
/// The differences of two files as an HTML page of its own (side by side, line numbers, the changed characters marked;
/// no scripts, no outside files): the compare window's report (the compare and merge plan, phase 5).
/// </summary>
public static class DiffReport
{
    /// <summary>
    /// The page for the rows of a comparison; <paramref name="context"/> equal lines are kept around each difference
    /// (null: every line).
    /// </summary>
    public static string Html(IReadOnlyList<DiffLine> rows, string leftName, string rightName, DateTime created, int? context = null, InlineLevel level = InlineLevel.Character)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>")
            .Append(E(leftName)).Append(" ↔ ").Append(E(rightName)).Append("</title><style>")
            .Append("body{font:14px system-ui,sans-serif;margin:24px;color:#18202d}h1{font-size:18px}")
            .Append("table{border-collapse:collapse;width:100%;table-layout:fixed;font:13px Consolas,Menlo,monospace}")
            .Append("td{padding:1px 6px;vertical-align:top;white-space:pre-wrap;word-break:break-all}")
            .Append("td.n{width:48px;text-align:right;color:#8c96a6;user-select:none}")
            .Append(".del{background:#fdedef}.add{background:#e9f7ee}.fill{background:#e6eaf0}.ign{color:#8c96a6}")
            .Append("del,ins{text-decoration:none;border-radius:2px}del{background:#f6c0c7}ins{background:#b4e6c3}")
            .Append("tr.gap td{background:#f4f6f9;color:#8c96a6;text-align:center}")
            .Append("</style></head><body>");
        var blocks = TextDiff.Blocks(rows).Count(b => !b.Ignored);
        html.Append("<h1>").Append(E(leftName)).Append(" ↔ ").Append(E(rightName)).Append("</h1><p>")
            .Append(E(created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append(" · ")
            .Append(blocks.ToString(CultureInfo.InvariantCulture)).Append(blocks == 1 ? " difference" : " differences").Append("</p><table>");

        var shown = Shown(rows, context);
        var skipped = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!shown[i])
            {
                skipped = true;
                continue;
            }
            if (skipped)
            {
                html.Append("<tr class=\"gap\"><td colspan=\"4\">⋯</td></tr>");
                skipped = false;
            }
            var row = rows[i];
            string? leftMarked = null, rightMarked = null;
            if (row.Kind == DiffKind.Changed && !row.Ignored && row.Left is not null && row.Right is not null)
            {
                var (l, r) = TextDiff.InlineDiff(row.Left, row.Right, level);
                leftMarked = Marked(row.Left, l, "del");
                rightMarked = Marked(row.Right, r, "ins");
            }
            var leftClass = row.Kind == DiffKind.Equal ? string.Empty : row.Left is null ? "fill" : row.Ignored ? "ign" : "del";
            var rightClass = row.Kind == DiffKind.Equal ? string.Empty : row.Right is null ? "fill" : row.Ignored ? "ign" : "add";
            html.Append("<tr><td class=\"n\">").Append(row.LeftNumber?.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td class=\"").Append(leftClass).Append("\">").Append(leftMarked ?? E(row.Left ?? string.Empty))
                .Append("</td><td class=\"n\">").Append(row.RightNumber?.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td class=\"").Append(rightClass).Append("\">").Append(rightMarked ?? E(row.Right ?? string.Empty))
                .Append("</td></tr>");
        }
        if (skipped)
        {
            html.Append("<tr class=\"gap\"><td colspan=\"4\">⋯</td></tr>");
        }
        html.Append("</table></body></html>");
        return html.ToString();
    }

    /// <summary>The rows kept: all, or the differences with <paramref name="context"/> equal rows around them.</summary>
    private static bool[] Shown(IReadOnlyList<DiffLine> rows, int? context)
    {
        var shown = new bool[rows.Count];
        if (context is not { } around)
        {
            Array.Fill(shown, true);
            return shown;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == DiffKind.Equal)
            {
                continue;
            }
            for (var j = Math.Max(0, i - around); j <= Math.Min(rows.Count - 1, i + around); j++)
            {
                shown[j] = true;
            }
        }
        return shown;
    }

    private static string Marked(string text, IReadOnlyList<InlineSpan> spans, string tag)
    {
        var html = new StringBuilder();
        foreach (var span in spans)
        {
            var part = WebUtility.HtmlEncode(text.Substring(span.Start, span.Length));
            html.Append(span.Different ? $"<{tag}>{part}</{tag}>" : part);
        }
        return html.ToString();
    }
}
