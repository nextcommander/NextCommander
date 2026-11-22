using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NextCommander.Core.Documents;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// E-mails: .eml (MIME: headers with encoded words, nested multipart, base64 and quoted-printable, charsets) and
/// Outlook's .msg (a compound file of MAPI properties). Shows the sender, recipients, date and subject, the body (its
/// HTML part with pictures of the mail itself; pictures from the internet are not loaded, they could tell the sender
/// the mail was opened) and the attachments.
/// </summary>
public sealed partial class MailViewer : IViewerPlugin
{
    static MailViewer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Name => "E-mail";

    public bool CanView(string fileName, ReadOnlySpan<byte> header)
    {
        var extension = Path.GetExtension(fileName);
        // a .msg is Outlook's only when it is a compound file (other programs use .msg for other things)
        return extension.Equals(".eml", StringComparison.OrdinalIgnoreCase)
            || (extension.Equals(".msg", StringComparison.OrdinalIgnoreCase) && header.Length >= 8 && header[..8].SequenceEqual(s_compoundFile));
    }

    private static readonly byte[] s_compoundFile = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>A mail as the viewer shows it.</summary>
    internal sealed record Mail(string? From, string? To, string? Cc, DateTimeOffset? Date, string? Subject, string? Text, string? Html,
        List<Attachment> Attachments);

    /// <summary>An attachment (or a picture of the HTML part): its name, type, bytes and Content-ID.</summary>
    internal sealed record Attachment(string Name, string? Type, byte[] Data, string? ContentId, bool Inline);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 100L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var mail = CompoundFile.IsCompoundFile(bytes) ? ReadMsg(bytes) : ReadEml(bytes);
        return new ViewerContent { Html = Page(mail, fileName, dark), Status = mail.Attachments.Count(a => !a.Inline) is var n and > 0
            ? ViewerHtml.T($"Attachments: {n}") : ViewerHtml.T("E-mail") };
    }

    internal static string Page(Mail mail, string fileName, bool dark)
    {
        var page = new ViewerHtml(string.IsNullOrWhiteSpace(mail.Subject) ? fileName : mail.Subject!, null, dark);
        page.Facts(
        [
            ("From", mail.From),
            ("To", mail.To),
            ("Cc", mail.Cc),
            ("Date", mail.Date?.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)),
        ]);
        var attachments = mail.Attachments.Where(a => !a.Inline || mail.Html is null).ToList();
        if (attachments.Count > 0)
        {
            page.Heading("Attachments").Table(["Name", "Type", "Size"], attachments.Select(a => (IReadOnlyList<string?>)[a.Name, a.Type, ViewerHtml.Size(a.Data.Length)]));
        }
        if (mail.Html is { } html)
        {
            page.Raw("<div class=\"card\">").Raw(SafeHtml(html, mail.Attachments)).Raw("</div>");
            if (RemoteContent().IsMatch(html))
            {
                page.Note("Pictures from the internet are not loaded.");
            }
        }
        else
        {
            page.Pre(mail.Text ?? string.Empty);
        }
        return page.ToString();
    }

    /// <summary>
    /// The mail's HTML within the page: its body only, no scripts, no pictures or styles from the internet (they would
    /// load when shown); the pictures of the mail itself ("cid:") as data.
    /// </summary>
    internal static string SafeHtml(string html, IReadOnlyList<Attachment> parts)
    {
        var body = BodyContent().Match(html) is { Success: true } m ? m.Groups[1].Value : html;
        body = Scripts().Replace(body, string.Empty);
        body = EventAttributes().Replace(body, string.Empty);
        body = CidSource().Replace(body, match =>
        {
            var id = match.Groups[2].Value;
            var part = parts.FirstOrDefault(p => p.ContentId is { } cid && cid.Trim('<', '>').Equals(id, StringComparison.OrdinalIgnoreCase));
            return part is null ? "src=\"\"" : $"src=\"data:{part.Type ?? "image/png"};base64,{Convert.ToBase64String(part.Data)}\"";
        });
        body = RemoteSource().Replace(body, "data-blocked=\"$2\"");
        body = RemoteUrl().Replace(body, "url()");
        return body;
    }

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BodyContent();

    [GeneratedRegex(@"<(script|iframe|object|embed)\b.*?(</\1>|/>)", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Scripts();

    [GeneratedRegex(@"\s on\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace, 1000)]
    private static partial Regex EventAttributes();

    [GeneratedRegex(@"src\s*=\s*([""']?)cid:([^""'\s>]+)\1", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex CidSource();

    [GeneratedRegex(@"(src|background)\s*=\s*[""']?((https?:)?//[^""'\s>]*)[""']?", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RemoteSource();

    [GeneratedRegex(@"url\(\s*[""']?(https?:)?//[^)]*\)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RemoteUrl();

    [GeneratedRegex(@"(src|background)\s*=\s*[""']?(https?:)?//|url\(\s*[""']?(https?:)?//", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RemoteContent();

    // ---------------------------------------------------------------- .eml (MIME)

    internal static Mail ReadEml(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes); // bytes as characters; each part is decoded with its own charset
        var (headers, body) = SplitPart(text);
        var mail = new Mail(Decode(Header(headers, "From")), Decode(Header(headers, "To")), Decode(Header(headers, "Cc")), ParseDate(Header(headers, "Date")),
            Decode(Header(headers, "Subject")), null, null, []);
        var texts = new List<string>();
        var htmls = new List<string>();
        Walk(headers, body, mail.Attachments, texts, htmls, 0);
        return mail with { Text = texts.Count > 0 ? string.Join("\n\n", texts) : null, Html = htmls.FirstOrDefault() };
    }

    /// <summary>The headers (unfolded, by name) and the body of a part.</summary>
    internal static (List<(string Name, string Value)> Headers, string Body) SplitPart(string part)
    {
        var end = part.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var crlf and >= 0 ? (crlf, 4)
            : part.IndexOf("\n\n", StringComparison.Ordinal) is var lf and >= 0 ? (lf, 2) : (part.Length, 0);
        var headerText = part[..end.Item1];
        var body = end.Item1 + end.Item2 <= part.Length ? part[(end.Item1 + end.Item2)..] : string.Empty;
        var headers = new List<(string, string)>();
        foreach (var line in headerText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && headers.Count > 0)
            {
                headers[^1] = (headers[^1].Item1, headers[^1].Item2 + " " + line.Trim());
            }
            else if (line.IndexOf(':') is var colon and > 0)
            {
                headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }
        return (headers, body);
    }

    private static string? Header(List<(string Name, string Value)> headers, string name) =>
        headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>A header's main value and its parameters ("text/plain; charset=utf-8; name=\"a.txt\"").</summary>
    internal static (string Value, Dictionary<string, string> Parameters) Parameters(string? header)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(header))
        {
            return (string.Empty, parameters);
        }
        var parts = SplitParameters(header);
        foreach (var part in parts.Skip(1))
        {
            if (part.IndexOf('=') is var eq and > 0)
            {
                var key = part[..eq].Trim();
                var value = part[(eq + 1)..].Trim().Trim('"');
                // RFC 2231: name*=utf-8''a%20b.txt
                if (key.EndsWith('*'))
                {
                    key = key.TrimEnd('*');
                    var pieces = value.Split('\'', 3);
                    if (pieces.Length == 3)
                    {
                        value = DecodePercent(pieces[2], pieces[0]);
                    }
                }
                parameters[key] = value;
            }
        }
        return (parts[0].Trim().ToLowerInvariant(), parameters);
    }

    private static List<string> SplitParameters(string header)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in header)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            if (c == ';' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        parts.Add(current.ToString());
        return parts;
    }

    private static string DecodePercent(string text, string charset)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.Add((byte)text[i]);
            }
        }
        return EncodingOf(charset).GetString([.. bytes]);
    }

    private static void Walk(List<(string Name, string Value)> headers, string body, List<Attachment> attachments, List<string> texts, List<string> htmls, int depth)
    {
        var (type, parameters) = Parameters(Header(headers, "Content-Type"));
        if (type.Length == 0)
        {
            type = "text/plain";
        }
        if (type.StartsWith("multipart/", StringComparison.Ordinal) && parameters.TryGetValue("boundary", out var boundary) && depth < 20)
        {
            foreach (var part in SplitMultipart(body, boundary))
            {
                var (partHeaders, partBody) = SplitPart(part);
                Walk(partHeaders, partBody, attachments, texts, htmls, depth + 1);
            }
            return;
        }
        var (disposition, dispositionParameters) = Parameters(Header(headers, "Content-Disposition"));
        var name = Decode(dispositionParameters.GetValueOrDefault("filename") ?? parameters.GetValueOrDefault("name"));
        var data = DecodeBody(body, Header(headers, "Content-Transfer-Encoding"));
        var contentId = Header(headers, "Content-ID");
        if (disposition != "attachment" && name is null && type is ("text/plain" or "text/html"))
        {
            var text = EncodingOf(parameters.GetValueOrDefault("charset")).GetString(data);
            (type == "text/html" ? htmls : texts).Add(text);
            return;
        }
        attachments.Add(new Attachment(name ?? (type == "message/rfc822" ? "message.eml" : T("(no name)")), type, data, contentId,
            Inline: disposition != "attachment" && contentId is not null));
    }

    private static string T(string text) => ViewerHtml.T(text);

    private static IEnumerable<string> SplitMultipart(string body, string boundary)
    {
        var marker = "--" + boundary;
        var parts = body.Split(marker);
        // before the first marker: the preamble; "--" after the last: the end
        foreach (var part in parts.Skip(1))
        {
            if (part.StartsWith("--", StringComparison.Ordinal))
            {
                yield break;
            }
            yield return part.TrimStart('\r', '\n').TrimStart();
        }
    }

    internal static byte[] DecodeBody(string body, string? transferEncoding)
    {
        switch (transferEncoding?.Trim().ToLowerInvariant())
        {
            case "base64":
                var clean = new string(body.Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=').ToArray());
                try
                {
                    return Convert.FromBase64String(clean.TrimEnd('=') + new string('=', (4 - clean.TrimEnd('=').Length % 4) % 4));
                }
                catch (FormatException)
                {
                    return Encoding.Latin1.GetBytes(body);
                }
            case "quoted-printable":
                return QuotedPrintable(body, underscoreIsSpace: false);
            default:
                return Encoding.Latin1.GetBytes(body.TrimEnd('\r', '\n'));
        }
    }

    private static byte[] QuotedPrintable(string text, bool underscoreIsSpace)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=' && i + 1 < text.Length && text[i + 1] is '\r' or '\n')
            {
                // a soft line break
                i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 2 : 1;
            }
            else if (c == '=' && i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.Add(underscoreIsSpace && c == '_' ? (byte)' ' : (byte)c);
            }
        }
        return [.. bytes];
    }

    /// <summary>A header with RFC 2047 encoded words ("=?utf-8?B?...?=", "=?iso-8859-9?Q?...?=") and raw UTF-8 decoded.</summary>
    internal static string? Decode(string? header)
    {
        if (header is null)
        {
            return null;
        }
        // a header in raw UTF-8 (the mail was read as Latin-1, byte by byte)
        var raw = Encoding.Latin1.GetBytes(header);
        if (raw.Any(b => b >= 0x80) && IsUtf8(raw))
        {
            header = Encoding.UTF8.GetString(raw);
        }
        // spaces between two encoded words are not part of the text
        header = BetweenWords().Replace(header, "?==?");
        var decoded = EncodedWord().Replace(header, m =>
        {
            var encoding = EncodingOf(m.Groups[1].Value);
            var bytes = m.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                ? DecodeBody(m.Groups[3].Value, "base64")
                : QuotedPrintable(m.Groups[3].Value, underscoreIsSpace: true);
            return encoding.GetString(bytes);
        });
        return decoded.Trim();
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", RegexOptions.None, 1000)]
    private static partial Regex EncodedWord();

    /// <summary>White space between the end of one encoded word and the start of the next.</summary>
    [GeneratedRegex(@"\?=\s+=\?", RegexOptions.None, 1000)]
    private static partial Regex BetweenWords();

    private static Encoding EncodingOf(string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (value is null)
        {
            return null;
        }
        // "Tue, 18 Nov 2026 10:15:30 +0300 (TRT)": the comment off
        var clean = value.Split('(')[0].Trim();
        string[] formats = ["ddd, d MMM yyyy H:mm:ss zzz", "d MMM yyyy H:mm:ss zzz", "ddd, d MMM yyyy H:mm zzz", "ddd, d MMM yyyy H:mm:ss 'GMT'", "ddd, d MMM yyyy H:mm:ss 'UT'"];
        var withColon = OffsetWithoutColon().Replace(clean, "$1:$2");
        return DateTimeOffset.TryParseExact(withColon, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : DateTimeOffset.TryParse(withColon, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ? date : null;
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$", RegexOptions.None, 1000)]
    private static partial Regex OffsetWithoutColon();

    // ---------------------------------------------------------------- .msg (Outlook: MAPI properties in a compound file)

    internal static Mail ReadMsg(byte[] bytes)
    {
        var file = CompoundFile.Open(bytes);
        string? Text(string storage, int property)
        {
            var prefix = storage.Length == 0 ? string.Empty : storage + "/";
            if (file.ReadPath($"{prefix}__substg1.0_{property:X4}001F") is { } unicode)
            {
                return Encoding.Unicode.GetString(unicode).TrimEnd('\0');
            }
            return file.ReadPath($"{prefix}__substg1.0_{property:X4}001E") is { } ansi ? Encoding.Latin1.GetString(ansi).TrimEnd('\0') : null;
        }
        byte[]? Binary(string storage, int property) => file.ReadPath($"{(storage.Length == 0 ? string.Empty : storage + "/")}__substg1.0_{property:X4}0102");

        var senderName = Text(string.Empty, 0x0C1A);
        var senderAddress = Text(string.Empty, 0x5D01) ?? Text(string.Empty, 0x0C1F);
        var from = senderName is null ? senderAddress : senderAddress is null || senderAddress == senderName ? senderName : $"{senderName} <{senderAddress}>";
        var html = Binary(string.Empty, 0x1013) is { } htmlBytes ? Encoding.UTF8.GetString(htmlBytes) : Text(string.Empty, 0x1013);
        var attachments = new List<Attachment>();
        foreach (var storage in file.Children(string.Empty).Where(c => c.StartsWith("__attach_version1.0_", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var data = Binary(storage, 0x3701) ?? [];
            var name = Text(storage, 0x3707) ?? Text(storage, 0x3704) ?? Text(storage, 0x3001) ?? T("(no name)");
            var contentId = Text(storage, 0x3712);
            attachments.Add(new Attachment(name, Text(storage, 0x370E), data, contentId, Inline: contentId is not null && html is not null));
        }
        return new Mail(from, Text(string.Empty, 0x0E04), Text(string.Empty, 0x0E03), MsgDate(file), Text(string.Empty, 0x0037),
            Text(string.Empty, 0x1000), html, attachments);
    }

    /// <summary>The sent (else delivered) time from the fixed-size properties of the message (32 bytes of header, 16 per property).</summary>
    private static DateTimeOffset? MsgDate(CompoundFile file)
    {
        if (file.ReadPath("__properties_version1.0") is not { } properties)
        {
            return null;
        }
        DateTimeOffset? sent = null;
        DateTimeOffset? delivered = null;
        for (var offset = 32; offset + 16 <= properties.Length; offset += 16)
        {
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(offset));
            if ((tag & 0xFFFF) != 0x0040)
            {
                continue; // not a time
            }
            var time = BinaryPrimitives.ReadInt64LittleEndian(properties.AsSpan(offset + 8));
            if (time <= 0 || time > DateTime.MaxValue.ToFileTimeUtc())
            {
                continue;
            }
            var value = new DateTimeOffset(DateTime.FromFileTimeUtc(time));
            switch (tag >> 16)
            {
                case 0x0039:
                    sent = value;
                    break;
                case 0x0E06:
                    delivered = value;
                    break;
            }
        }
        return sent ?? delivered;
    }
}
