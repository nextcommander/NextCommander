using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NextCommander.Core.Text;
using Markdig;

namespace NextCommander.App.Views.Lister;

/// <summary>Turns HTML, Markdown and RTF files into a self-contained HTML page for the Lister preview (key 5).</summary>
public static partial class DocumentPreview
{
    private static readonly MarkdownPipeline s_markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "html", "htm", "xhtml", "md", "markdown", "mdown", "mkd", "rtf",
    };

    public static bool Supports(string extension) => Extensions.Contains(extension);

    /// <summary>The page for a file: body converted by type, wrapped with a stylesheet for the light or dark theme.</summary>
    public static string Build(string extension, string text, bool dark)
    {
        var ext = extension.ToLowerInvariant();
        string body;
        if (ext == "rtf" || text.StartsWith("{\\rtf", StringComparison.Ordinal))
        {
            body = RtfConverter.ToHtml(text);
        }
        else if (ext is "md" or "markdown" or "mdown" or "mkd")
        {
            body = Markdown.ToHtml(text, s_markdown);
        }
        else
        {
            // HTML: no scripts; keep the document's own styles (the renderer supports CSS 2).
            body = Scripts().Replace(text, string.Empty);
            return Inject(body, Style(dark));
        }
        return $"<html><head>{Style(dark)}</head><body>{body}</body></html>";
    }

    /// <summary>Adds the theme stylesheet before the document's own (so the document still wins).</summary>
    private static string Inject(string html, string style)
    {
        var head = HeadOpen().Match(html);
        if (head.Success)
        {
            return html.Insert(head.Index + head.Length, style);
        }
        var htmlTag = HtmlOpen().Match(html);
        return htmlTag.Success ? html.Insert(htmlTag.Index + htmlTag.Length, "<head>" + style + "</head>") : style + html;
    }

    /// <summary>VS Code Dark+ / light page colors.</summary>
    private static string Style(bool dark)
    {
        var (fg, bg, link, code, border, muted) = dark
            ? ("#CCCCCC", "#1E1E1E", "#3794FF", "#2D2D2D", "#454545", "#9D9D9D")
            : ("#1F2328", "#FFFFFF", "#0969DA", "#F3F5F8", "#D0D7DE", "#59636E");
        var sb = new StringBuilder("<style>");
        sb.Append(CultureFree($"body {{ font-family: Segoe UI, Helvetica, Arial, sans-serif; font-size: 10.5pt; color: {fg}; background-color: {bg}; margin: 12px 16px; }}"));
        sb.Append(CultureFree($"a {{ color: {link}; }} h1, h2 {{ border-bottom: 1px solid {border}; padding-bottom: 4px; }}"));
        sb.Append(CultureFree($"code, pre {{ font-family: Cascadia Mono, Consolas, Menlo, monospace; background-color: {code}; }} pre {{ padding: 8px; }}"));
        sb.Append(CultureFree($"blockquote {{ color: {muted}; border-left: 3px solid {border}; margin-left: 0; padding-left: 10px; }}"));
        sb.Append(CultureFree($"table {{ border-collapse: collapse; margin-bottom: 10px; }} td, th {{ border: 1px solid {border}; padding: 3px 8px; }} th {{ background-color: {code}; color: {muted}; font-weight: normal; }} hr {{ border: 0; border-top: 1px solid {border}; }}"));
        sb.Append(" p { margin: 0 0 8px 0; }</style>");
        return sb.ToString();
    }

    private static string CultureFree(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>An HTML body (an Office / OpenDocument preview) as a page with the theme's stylesheet.</summary>
    public static string Wrap(string body, bool dark) => $"<html><head>{Style(dark)}</head><body>{body}</body></html>";

    /// <summary>Plain text as a page (fallback).</summary>
    public static string PlainText(string text, bool dark) =>
        $"<html><head>{Style(dark)}</head><body><pre>{WebUtility.HtmlEncode(text)}</pre></body></html>";

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Scripts();

    [GeneratedRegex(@"<head\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();

    [GeneratedRegex(@"<html\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpen();
}
