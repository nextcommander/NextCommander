using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Data;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// CSV and TSV files as a table: the separator found from the first lines (comma, semicolon, tab or bar), quoted
/// fields (RFC 4180: quotes doubled, line breaks inside), the first row as the header. A click on a column header
/// sorts by it (numbers as numbers). At most <see cref="MaxRows"/> rows are shown.
/// </summary>
public sealed class TableViewer : IViewerPlugin
{
    internal const int MaxRows = 100_000;

    private static readonly string[] s_extensions = [".csv", ".tsv", ".tab"];

    public string Name => "Table";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        s_extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 200L * 1024 * 1024, cancellationToken);
        var text = ViewerHtml.Text(bytes);
        var separator = Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? DetectSeparator(text) : '\t';
        var (rows, more) = await Task.Run(() => Parse(text, separator, MaxRows + 1, cancellationToken), cancellationToken);
        // the control on the UI thread (this continues there)
        var header = rows.Count > 0 ? rows[0] : [];
        var body = rows.Skip(1).Take(MaxRows).ToList();
        var columns = Math.Max(header.Length, body.Count == 0 ? 0 : body.Max(r => r.Length));
        var grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ItemsSource = body.Select(r => new TableRow(r)).ToList(),
        };
        for (var i = 0; i < columns; i++)
        {
            var index = i;
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = index < header.Length && header[index].Length > 0 ? header[index] : $"#{index + 1}",
                Binding = new Binding($"[{index}]"),
                CustomSortComparer = new CellComparer(index),
                MaxWidth = 600,
            });
        }
        var status = ViewerHtml.T($"Rows: {body.Count.ToString("N0", CultureInfo.CurrentCulture)}")
            + (more ? " " + ViewerHtml.T("(the first ones)") : string.Empty)
            + " · " + ViewerHtml.T($"Columns: {columns}");
        return new ViewerContent { Control = grid, Status = status };
    }

    /// <summary>One row; its cells by index (a missing one is empty).</summary>
    internal sealed class TableRow(string[] cells)
    {
        public string this[int index] => index < cells.Length ? cells[index] : string.Empty;
    }

    /// <summary>Sorts a column by numbers when both cells are numbers, else as text.</summary>
    private sealed class CellComparer(int column) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (x as TableRow)?[column] ?? string.Empty;
            var b = (y as TableRow)?[column] ?? string.Empty;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var da) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            {
                return da.CompareTo(db);
            }
            return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    /// <summary>The separator used most often, outside quotes, on the first lines (comma when none).</summary>
    internal static char DetectSeparator(string text)
    {
        char[] candidates = [',', ';', '\t', '|'];
        var counts = new int[candidates.Length];
        var inQuotes = false;
        var lines = 0;
        foreach (var c in text.AsSpan(0, Math.Min(text.Length, 20_000)))
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c == '\n' && ++lines >= 10)
            {
                break;
            }
            else if (!inQuotes && Array.IndexOf(candidates, c) is var i and >= 0)
            {
                counts[i]++;
            }
        }
        var best = Array.IndexOf(counts, counts.Max());
        return counts[best] == 0 ? ',' : candidates[best];
    }

    /// <summary>The rows (at most <paramref name="limit"/>) and whether there were more.</summary>
    internal static (List<string[]> Rows, bool More) Parse(string text, char separator, int limit, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
                i++;
                continue;
            }
            if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                {
                    rows.Add([.. row]);
                    if (rows.Count >= limit)
                    {
                        return (rows, i + 1 < text.Length);
                    }
                    if (rows.Count % 10_000 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                row.Clear();
            }
            else
            {
                field.Append(c);
            }
            i++;
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        return (rows, false);
    }
}
