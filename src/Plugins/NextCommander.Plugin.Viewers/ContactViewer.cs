using System.Globalization;
using System.Text;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// Contacts (.vcf, vCard 2.1 to 4.0) and calendars (.ics, iCalendar): each contact as a card with its photo, names,
/// phones, e-mails, addresses and notes; each event with its time, place, organizer, attendees, repetition and text,
/// ordered by time. Both formats are lines of "NAME;PARAMETERS:VALUE", folded and escaped the same way.
/// </summary>
public sealed class ContactViewer : IViewerPlugin
{
    static ContactViewer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Name => "Contacts and calendar";

    public bool CanView(string fileName, ReadOnlySpan<byte> header)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".vcf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vcard", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ics", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ical", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One line: its name, parameters (upper-case names) and value (unescaped).</summary>
    internal sealed record Property(string Name, Dictionary<string, string> Parameters, string Value)
    {
        public string? Type => Parameters.TryGetValue("TYPE", out var type) ? type.ToLowerInvariant().Replace(",", ", ", StringComparison.Ordinal) : null;
    }

    /// <summary>A BEGIN/END block (VCARD, VEVENT, VTODO ...) with its lines.</summary>
    internal sealed record Block(string Kind, List<Property> Properties)
    {
        public string? Get(string name) => Properties.FirstOrDefault(p => p.Name == name)?.Value;

        public IEnumerable<Property> All(string name) => Properties.Where(p => p.Name == name);
    }

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var bytes = await ViewerHtml.ReadAllAsync(content, 50L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var blocks = Parse(ViewerHtml.Text(bytes));
        var cards = blocks.Where(b => b.Kind == "VCARD").ToList();
        var events = blocks.Where(b => b.Kind is "VEVENT" or "VTODO").ToList();
        var page = new ViewerHtml(fileName, null, dark);
        foreach (var card in cards)
        {
            Card(page, card);
        }
        if (events.Count > 0)
        {
            var calendar = blocks.FirstOrDefault(b => b.Kind == "VCALENDAR");
            if (calendar?.Get("X-WR-CALNAME") is { } name)
            {
                page.Paragraph(name, "muted");
            }
            foreach (var item in events.OrderBy(e => Time(e.Properties.FirstOrDefault(p => p.Name is "DTSTART" or "DUE")) ?? DateTime.MaxValue))
            {
                Event(page, item);
            }
        }
        if (cards.Count == 0 && events.Count == 0)
        {
            page.Note("The file has no contacts or events.");
        }
        var status = cards.Count > 0
            ? ViewerHtml.T($"Contacts: {cards.Count}")
            : ViewerHtml.T($"Events: {events.Count}");
        return new ViewerContent { Html = page.ToString(), Status = status };
    }

    // ---------------------------------------------------------------- the lines

    /// <summary>The blocks of a file (nested ones too: a VEVENT inside a VCALENDAR is a block of its own).</summary>
    internal static List<Block> Parse(string text)
    {
        var blocks = new List<Block>();
        var open = new Stack<Block>();
        foreach (var line in Unfold(text))
        {
            if (ParseLine(line) is not { } property)
            {
                continue;
            }
            if (property.Name == "BEGIN")
            {
                var block = new Block(property.Value.Trim().ToUpperInvariant(), []);
                blocks.Add(block);
                open.Push(block);
            }
            else if (property.Name == "END")
            {
                if (open.Count > 0)
                {
                    open.Pop();
                }
            }
            else if (open.Count > 0)
            {
                open.Peek().Properties.Add(property);
            }
        }
        return blocks;
    }

    /// <summary>Lines with their folded continuations joined (a line starting with a space or tab; vCard 2.1's "=" soft breaks too).</summary>
    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t' && current.Length > 0)
            {
                current.Append(raw.AsSpan(1));
                continue;
            }
            if (current.Length > 0 && current[^1] == '=' && current.ToString().Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
            {
                current.Length--;
                current.Append(raw);
                continue;
            }
            if (current.Length > 0)
            {
                yield return current.ToString();
            }
            current.Clear().Append(raw);
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    internal static Property? ParseLine(string line)
    {
        // the value starts at the first colon outside quotes
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0)
        {
            return null;
        }
        var head = line[..colon].Split(';');
        // "item1.TEL": the group is left out
        var name = head[0].Contains('.', StringComparison.Ordinal) ? head[0][(head[0].LastIndexOf('.') + 1)..] : head[0];
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in head.Skip(1))
        {
            if (parameter.IndexOf('=') is var eq and > 0)
            {
                var key = parameter[..eq].ToUpperInvariant();
                var value = parameter[(eq + 1)..].Trim('"');
                parameters[key] = parameters.TryGetValue(key, out var earlier) ? earlier + "," + value : value;
            }
            else
            {
                // vCard 2.1: ";HOME;VOICE" without "TYPE="
                parameters["TYPE"] = parameters.TryGetValue("TYPE", out var earlier) ? earlier + "," + parameter : parameter;
            }
        }
        var raw = line[(colon + 1)..];
        if (parameters.TryGetValue("ENCODING", out var encoding) && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
        {
            var charset = parameters.TryGetValue("CHARSET", out var c) ? c : "utf-8";
            raw = QuotedPrintable(raw, charset);
        }
        return new Property(name.ToUpperInvariant(), parameters, raw);
    }

    private static string QuotedPrintable(string text, string charset)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '=' && i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
            }
        }
        try
        {
            return Encoding.GetEncoding(charset).GetString([.. bytes]);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString([.. bytes]);
        }
    }

    /// <summary>"a\, b\nc" → "a, b" + line break + "c".</summary>
    internal static string Unescape(string value) =>
        value.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\;", ";", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    // ---------------------------------------------------------------- contacts

    private static void Card(ViewerHtml page, Block card)
    {
        var name = card.Get("FN") is { Length: > 0 } fn ? Unescape(fn)
            : card.Get("N") is { } n ? string.Join(' ', n.Split(';').Skip(1).Take(2).Concat(n.Split(';').Take(1)).Where(p => p.Length > 0)).Trim()
            : ViewerHtml.T("(no name)");
        page.Raw("<div class=\"card\">");
        if (Photo(card) is { } photo)
        {
            page.Raw($"<img src=\"{photo}\" style=\"float: right; max-height: 96px;\">");
        }
        page.Raw("<h2>").Raw(ViewerHtml.E(name)).Raw("</h2>");
        var rows = new List<(string, string?)>
        {
            ("Organization", card.Get("ORG") is { } org ? Unescape(org.Replace(';', ' ')).Trim() : null),
            ("Title", card.Get("TITLE") is { } title ? Unescape(title) : null),
            ("Nickname", card.Get("NICKNAME") is { } nick ? Unescape(nick) : null),
            ("Birthday", card.Get("BDAY") is { } bday ? Date(bday) : null),
        };
        rows.AddRange(card.All("TEL").Select(t => ("Phone", (string?)Typed(t))));
        rows.AddRange(card.All("EMAIL").Select(e => ("E-mail", (string?)Typed(e))));
        rows.AddRange(card.All("ADR").Select(a => ("Address", (string?)Typed(a with { Value = string.Join(", ", Unescape(a.Value.Replace(';', '\u0001')).Split('\u0001').Where(p => p.Trim().Length > 0)) }))));
        rows.AddRange(card.All("URL").Select(u => ("Web", (string?)Unescape(u.Value))));
        rows.Add(("Note", card.Get("NOTE") is { } note ? Unescape(note) : null));
        page.Facts(rows).Raw("</div>");
    }

    private static string Typed(Property property) =>
        property.Type is { Length: > 0 } type ? $"{Unescape(property.Value)} ({type})" : Unescape(property.Value);

    /// <summary>The contact's photo as data (vCard 3: base64 with ENCODING=b; vCard 4: a data: URI); a link is not loaded.</summary>
    private static string? Photo(Block card)
    {
        if (card.All("PHOTO").FirstOrDefault() is not { } photo)
        {
            return null;
        }
        if (photo.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return photo.Value;
        }
        if (photo.Parameters.TryGetValue("ENCODING", out var encoding) && encoding.ToUpperInvariant() is "B" or "BASE64")
        {
            var type = photo.Type?.Split(',')[0].Trim() switch
            {
                "png" => "image/png",
                "gif" => "image/gif",
                _ => "image/jpeg",
            };
            return $"data:{type};base64,{photo.Value.Replace(" ", string.Empty, StringComparison.Ordinal)}";
        }
        return null;
    }

    // ---------------------------------------------------------------- events

    private static void Event(ViewerHtml page, Block item)
    {
        var start = item.Properties.FirstOrDefault(p => p.Name is "DTSTART");
        var end = item.Properties.FirstOrDefault(p => p.Name is "DTEND");
        var due = item.Properties.FirstOrDefault(p => p.Name is "DUE");
        page.Raw("<div class=\"card\">").Raw("<h2>").Raw(ViewerHtml.E(item.Get("SUMMARY") is { } summary ? Unescape(summary) : ViewerHtml.T("(no title)"))).Raw("</h2>");
        page.Facts(
        [
            ("Starts", Shown(start)),
            ("Ends", Shown(end)),
            ("Due", Shown(due)),
            ("Place", item.Get("LOCATION") is { } location ? Unescape(location) : null),
            ("Organizer", item.All("ORGANIZER").FirstOrDefault() is { } organizer ? Person(organizer) : null),
            ("Attendees", string.Join(", ", item.All("ATTENDEE").Select(Person))),
            ("Repeats", item.Get("RRULE")),
            ("Status", item.Get("STATUS")?.ToLowerInvariant()),
        ]);
        if (item.Get("DESCRIPTION") is { Length: > 0 } description)
        {
            page.Pre(Unescape(description));
        }
        page.Raw("</div>");
    }

    /// <summary>"Name (mail)" of an ORGANIZER / ATTENDEE ("CN=Ayşe:mailto:ayse@x").</summary>
    private static string Person(Property property)
    {
        var mail = property.Value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? property.Value[7..] : property.Value;
        return property.Parameters.TryGetValue("CN", out var name) ? $"{name} ({mail})" : mail;
    }

    private static string? Shown(Property? property)
    {
        if (property is null)
        {
            return null;
        }
        var allDay = property.Value.Length == 8 || (property.Parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase));
        return Time(property) is { } time
            ? time.ToString(allDay ? "D" : "f", CultureInfo.CurrentCulture)
            : property.Value;
    }

    /// <summary>"20261122", "20261122T101500Z" (UTC: shown in local time), "20261122T101500" with TZID (that zone's time).</summary>
    internal static DateTime? Time(Property? property)
    {
        if (property is null)
        {
            return null;
        }
        var value = property.Value.Trim();
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }
        if (value.EndsWith('Z') && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            return utc.ToLocalTime();
        }
        if (!DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }
        if (property.Parameters.TryGetValue("TZID", out var zone))
        {
            try
            {
                return TimeZoneInfo.ConvertTime(local, TimeZoneInfo.FindSystemTimeZoneById(zone), TimeZoneInfo.Local);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                // an unknown zone: the time as written
            }
        }
        return local;
    }

    private static string Date(string value) =>
        DateTime.TryParseExact(value.Replace("-", string.Empty, StringComparison.Ordinal)[..Math.Min(8, value.Replace("-", string.Empty, StringComparison.Ordinal).Length)],
            "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("D", CultureInfo.CurrentCulture)
            : value;
}
