using System.Globalization;
using System.Net;
using System.Text;

namespace NextCommander.Core.Text;

/// <summary>
/// Minimal RTF reader for the Lister preview: text with paragraphs, bold / italic / underline, tabs, \'hh characters in
/// the document code page and \uN Unicode. Tables become lines; fonts, colors, pictures and other destinations are skipped.
/// </summary>
public static class RtfConverter
{
    private sealed class State
    {
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Skip;
        public int UnicodeSkip = 1;

        public State Clone() => (State)MemberwiseClone();
    }

    private static readonly HashSet<string> s_skipDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr", "headerf", "footerl", "footerr",
        "footerf", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "themedata", "colorschememapping",
        "latentstyles", "datastore", "object", "fldinst", "filetbl", "revtbl", "pgdsctbl", "bkmkstart", "bkmkend", "footnote",
    };

    /// <summary>Converts RTF to an HTML fragment (paragraphs and inline formatting).</summary>
    public static string ToHtml(string rtf)
    {
        EncodingDetector.EnsureCodePages();
        var html = new StringBuilder();
        var stack = new Stack<State>();
        var state = new State();
        var encoding = Encoding.GetEncoding(1252);
        var bytes = new List<byte>(); // pending \'hh bytes (multi-byte code pages)
        var openTags = string.Empty;
        var paragraph = new StringBuilder();
        var pendingSkip = 0; // characters to skip after \uN

        void FlushBytes()
        {
            if (bytes.Count > 0)
            {
                AppendText(encoding.GetString([.. bytes]));
                bytes.Clear();
            }
        }

        void AppendText(string text)
        {
            if (state.Skip || text.Length == 0)
            {
                return;
            }
            var tags = (state.Bold ? "b" : string.Empty) + (state.Italic ? "i" : string.Empty) + (state.Underline ? "u" : string.Empty);
            if (tags != openTags)
            {
                Close(paragraph, openTags);
                foreach (var t in tags)
                {
                    paragraph.Append('<').Append(t).Append('>');
                }
                openTags = tags;
            }
            paragraph.Append(WebUtility.HtmlEncode(text));
        }

        void EndParagraph()
        {
            FlushBytes();
            Close(paragraph, openTags);
            openTags = string.Empty;
            html.Append("<p>").Append(paragraph.Length == 0 ? "&nbsp;" : paragraph.ToString()).Append("</p>\n");
            paragraph.Clear();
        }

        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (c == '{')
            {
                FlushBytes();
                stack.Push(state.Clone());
                i++;
                // {\*\destination ...} is ignorable when unknown
                if (i + 1 < rtf.Length && rtf[i] == '\\' && rtf[i + 1] == '*')
                {
                    state.Skip = true;
                }
                continue;
            }
            if (c == '}')
            {
                FlushBytes();
                if (stack.Count > 0)
                {
                    state = stack.Pop();
                }
                i++;
                continue;
            }
            if (c is '\r' or '\n')
            {
                i++;
                continue;
            }
            if (c != '\\')
            {
                if (pendingSkip > 0)
                {
                    pendingSkip--;
                    i++;
                    continue;
                }
                FlushBytes();
                var start = i;
                while (i < rtf.Length && rtf[i] is not ('\\' or '{' or '}' or '\r' or '\n'))
                {
                    i++;
                }
                AppendText(rtf[start..i]);
                continue;
            }

            // Control symbol or word
            i++;
            if (i >= rtf.Length)
            {
                break;
            }
            var symbol = rtf[i];
            if (symbol == '\'')
            {
                if (i + 2 < rtf.Length && byte.TryParse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    if (pendingSkip > 0)
                    {
                        pendingSkip--;
                    }
                    else if (!state.Skip)
                    {
                        bytes.Add(b);
                    }
                }
                i += 3;
                continue;
            }
            if (!char.IsAsciiLetter(symbol))
            {
                FlushBytes();
                i++;
                switch (symbol)
                {
                    case '\\' or '{' or '}':
                        AppendText(symbol.ToString());
                        break;
                    case '~':
                        AppendText(" ");
                        break;
                    case '-':
                        break; // optional hyphen
                    case '_':
                        AppendText("‑");
                        break;
                    case '\r' or '\n':
                        if (!state.Skip)
                        {
                            EndParagraph();
                        }
                        break;
                }
                continue;
            }
            var wordStart = i;
            while (i < rtf.Length && char.IsAsciiLetter(rtf[i]))
            {
                i++;
            }
            var word = rtf[wordStart..i];
            int? param = null;
            var paramStart = i;
            if (i < rtf.Length && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i])))
            {
                i++;
                while (i < rtf.Length && char.IsAsciiDigit(rtf[i]))
                {
                    i++;
                }
                if (int.TryParse(rtf.AsSpan(paramStart, i - paramStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p))
                {
                    param = p;
                }
            }
            if (i < rtf.Length && rtf[i] == ' ')
            {
                i++; // delimiter
            }
            if (word != "u")
            {
                FlushBytes();
            }
            switch (word)
            {
                case var d when s_skipDestinations.Contains(d):
                    state.Skip = true;
                    break;
                case "ansicpg" when param is { } cp:
                    try
                    {
                        encoding = Encoding.GetEncoding(cp);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                        // keep 1252
                    }
                    break;
                case "par" or "sect" or "page":
                    if (!state.Skip)
                    {
                        EndParagraph();
                    }
                    break;
                case "row":
                    if (!state.Skip)
                    {
                        EndParagraph();
                    }
                    break;
                case "line":
                    if (!state.Skip)
                    {
                        Close(paragraph, openTags);
                        openTags = string.Empty;
                        paragraph.Append("<br>");
                    }
                    break;
                case "tab" or "cell":
                    AppendText("\t");
                    break;
                case "b":
                    state.Bold = param != 0;
                    break;
                case "i":
                    state.Italic = param != 0;
                    break;
                case "ul":
                    state.Underline = param != 0;
                    break;
                case "ulnone":
                    state.Underline = false;
                    break;
                case "plain":
                    state.Bold = state.Italic = state.Underline = false;
                    break;
                case "uc" when param is { } uc:
                    state.UnicodeSkip = uc;
                    break;
                case "u" when param is { } u:
                    FlushBytes();
                    AppendText(((char)(u < 0 ? u + 65536 : u)).ToString());
                    pendingSkip = state.UnicodeSkip;
                    break;
                case "emdash":
                    AppendText("—");
                    break;
                case "endash":
                    AppendText("–");
                    break;
                case "bullet":
                    AppendText("•");
                    break;
                case "lquote":
                    AppendText("‘");
                    break;
                case "rquote":
                    AppendText("’");
                    break;
                case "ldblquote":
                    AppendText("“");
                    break;
                case "rdblquote":
                    AppendText("”");
                    break;
            }
        }
        FlushBytes();
        if (paragraph.Length > 0)
        {
            EndParagraph();
        }
        return html.ToString().Replace("\t", "&emsp;", StringComparison.Ordinal);
    }

    private static void Close(StringBuilder sb, string tags)
    {
        for (var k = tags.Length - 1; k >= 0; k--)
        {
            sb.Append("</").Append(tags[k]).Append('>');
        }
    }

    /// <summary>True when the bytes start like an RTF document.</summary>
    public static bool LooksLikeRtf(ReadOnlySpan<byte> start) => start.StartsWith("{\\rtf"u8);
}
