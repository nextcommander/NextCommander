using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// Jupyter notebooks (.ipynb): the kernel and language, then every cell in order: Markdown as text, code with its
/// number and its outputs (text, tables, pictures, errors without their terminal colors). Scripts and anything from
/// the internet are left out.
/// </summary>
public sealed partial class NotebookViewer : IViewerPlugin
{
    private static readonly MarkdownPipeline s_markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public string Name => "Notebook";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) => Path.GetExtension(fileName).Equals(".ipynb", StringComparison.OrdinalIgnoreCase);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 200L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var (html, cells) = await Task.Run(() => Render(bytes, fileName, dark), cancellationToken).ConfigureAwait(false);
        return new ViewerContent { Html = html, Status = ViewerHtml.T($"Cells: {cells}") };
    }

    internal static (string Html, int Cells) Render(byte[] bytes, string fileName, bool dark)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        string? Path(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                {
                    return null;
                }
            }
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }
        var kernel = root.TryGetProperty("metadata", out var metadata) ? Path(metadata, "kernelspec", "display_name") : null;
        var language = root.TryGetProperty("metadata", out metadata) ? Path(metadata, "language_info", "name") ?? Path(metadata, "kernelspec", "language") : null;
        var page = new ViewerHtml(System.IO.Path.GetFileNameWithoutExtension(fileName), kernel, dark);
        page.Facts([("Kernel", kernel), ("Language", language)]);
        var count = 0;
        if (root.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
        {
            foreach (var cell in cells.EnumerateArray())
            {
                count++;
                var type = cell.TryGetProperty("cell_type", out var t) ? t.GetString() : null;
                var source = Source(cell);
                if (type == "markdown")
                {
                    page.Raw("<div>").Raw(MailViewer.SafeHtml(Markdown.ToHtml(source, s_markdown), [])).Raw("</div>");
                    continue;
                }
                if (type != "code")
                {
                    page.Pre(source);
                    continue;
                }
                var number = cell.TryGetProperty("execution_count", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32().ToString(CultureInfo.InvariantCulture) : " ";
                page.Raw($"<div class=\"muted\"><code>In [{number}]:</code></div>").Pre(source);
                if (cell.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var output in outputs.EnumerateArray())
                    {
                        Output(page, output);
                    }
                }
            }
        }
        return (page.ToString(), count);
    }

    /// <summary>A cell's source: a string or an array of lines.</summary>
    private static string Source(JsonElement cell) => cell.TryGetProperty("source", out var source) ? Joined(source) : string.Empty;

    private static string Joined(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(l => l.GetString())),
        _ => string.Empty,
    };

    private static void Output(ViewerHtml page, JsonElement output)
    {
        var kind = output.TryGetProperty("output_type", out var k) ? k.GetString() : null;
        switch (kind)
        {
            case "stream":
                page.Pre(output.TryGetProperty("text", out var text) ? Joined(text) : string.Empty);
                break;
            case "error":
                var name = output.TryGetProperty("ename", out var e) ? e.GetString() : null;
                var value = output.TryGetProperty("evalue", out var v) ? v.GetString() : null;
                var trace = output.TryGetProperty("traceback", out var tb) && tb.ValueKind == JsonValueKind.Array
                    ? string.Join('\n', tb.EnumerateArray().Select(l => l.GetString())) : string.Empty;
                page.Raw("<div class=\"bad\">").Raw(ViewerHtml.E($"{name}: {value}")).Raw("</div>").Pre(AnsiColors().Replace(trace, string.Empty));
                break;
            default:
                if (!output.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                {
                    break;
                }
                if (data.TryGetProperty("image/png", out var png))
                {
                    page.Raw($"<p><img src=\"data:image/png;base64,{Joined(png).Replace("\n", string.Empty, StringComparison.Ordinal)}\"></p>");
                }
                else if (data.TryGetProperty("image/jpeg", out var jpeg))
                {
                    page.Raw($"<p><img src=\"data:image/jpeg;base64,{Joined(jpeg).Replace("\n", string.Empty, StringComparison.Ordinal)}\"></p>");
                }
                else if (data.TryGetProperty("text/html", out var html))
                {
                    page.Raw("<div>").Raw(MailViewer.SafeHtml(Joined(html), [])).Raw("</div>");
                }
                else if (data.TryGetProperty("text/markdown", out var markdown))
                {
                    page.Raw("<div>").Raw(MailViewer.SafeHtml(Markdown.ToHtml(Joined(markdown), s_markdown), [])).Raw("</div>");
                }
                else if (data.TryGetProperty("text/plain", out var plain))
                {
                    page.Pre(Joined(plain));
                }
                break;
        }
    }

    /// <summary>Terminal color codes in a traceback ("\u001b[0;31m").</summary>
    [GeneratedRegex("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.None, 1000)]
    private static partial Regex AnsiColors();
}
