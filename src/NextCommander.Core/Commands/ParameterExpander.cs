using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Commands;

/// <summary>Everything a user command (button, uc_ command) may refer to.</summary>
public sealed record ParameterContext
{
    /// <summary>Source (active) panel directory, native form.</summary>
    public required string SourceDirectory { get; init; }

    public required string TargetDirectory { get; init; }

    public string? LeftDirectory { get; init; }

    public string? RightDirectory { get; init; }

    /// <summary>Name under the cursor in the source panel (null on "..").</summary>
    public string? CursorName { get; init; }

    public string? TargetCursorName { get; init; }

    /// <summary>Marked names in the source panel (empty = nothing marked).</summary>
    public IReadOnlyList<string> Selected { get; init; } = [];

    public IReadOnlyList<string> TargetSelected { get; init; } = [];

    public string? ClipboardText { get; init; }

    public DateTime Now { get; init; } = DateTime.Now;

    public char Separator { get; init; } = Path.DirectorySeparatorChar;
}

/// <summary>
/// Expands the parameters of user commands:
/// <list type="bullet">
/// <item><c>%P %T</c> source/target path (with trailing separator), <c>%X %Y</c>-free</item>
/// <item><c>%N %M</c> name under cursor (source/target), <c>%O</c> name without extension, <c>%E</c> extension</item>
/// <item><c>%S %R</c> selected names (source/target), <c>%S1</c> first, <c>%S2-3</c> range</item>
/// <item><c>%L %F</c> list files with full paths / names, <c>%UL %UF</c> UTF-8, <c>%WL %WF</c> UTF-16</item>
/// <item><c>%B</c> source folder name, <c>%B+1</c> its parent's name</item>
/// <item><c>%C1 %C2</c> compare pair (first two selected, or cursor + target cursor)</item>
/// <item><c>%Q</c> no automatic quoting, <c>%Z</c> empty list instead of the cursor when nothing is marked</item>
/// <item><c>%$CLIPBOARD%</c>, <c>%$DATE%</c>, <c>%$DATE+1D%</c> (Y/M/D offsets), <c>%$TIME%</c>, <c>%|NAME|</c> environment variable</item>
/// <item><c>:~start,len</c> substring after a single-value parameter: <c>%N:~0,3</c></item>
/// <item>Leading <c>?</c>: ask the user (see <see cref="NeedsPrompt"/>)</item>
/// </list>
/// Values containing spaces are quoted automatically unless the token is already quoted or %Q is used.
/// </summary>
public static partial class ParameterExpander
{
    [GeneratedRegex(@"%\$(CLIPBOARD|DATE|TIME)(?:([+-]\d+)([YMDhms]))?%|%\|([^|]+)\||%(UL|UF|WL|WF|C1|C2|[PTNMOEBSRLFQZ])(\+\d+|\d+(?:-\d+)?)?(:~(-?\d+)(?:,(-?\d+))?)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>A command whose parameters start with '?' must be confirmed/edited by the user first.</summary>
    public static bool NeedsPrompt(string? parameters, out string withoutMarker)
    {
        withoutMarker = parameters ?? string.Empty;
        if (withoutMarker.StartsWith('?'))
        {
            withoutMarker = withoutMarker[1..];
            return true;
        }
        return false;
    }

    /// <param name="writeListFile">Creates a list file (lines, encoding) and returns its path.</param>
    public static string Expand(string? template, ParameterContext context, Func<IReadOnlyList<string>, Encoding, string>? writeListFile = null)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }
        var autoQuote = !template.Contains("%Q", StringComparison.Ordinal);
        var emptyWhenNothingMarked = template.Contains("%Z", StringComparison.Ordinal);
        writeListFile ??= WriteTempListFile;

        var output = new StringBuilder();
        foreach (var (token, separator, quoted) in Tokenize(template))
        {
            var expanded = PlaceholderRegex().Replace(token, m => Evaluate(m, context, emptyWhenNothingMarked, writeListFile));
            var hadPlaceholder = expanded != token;
            if (autoQuote && hadPlaceholder && !quoted && expanded.Contains(' ', StringComparison.Ordinal) && !IsMultiValueToken(token))
            {
                expanded = "\"" + expanded + "\"";
            }
            output.Append(quoted ? "\"" + expanded + "\"" : expanded).Append(separator);
        }
        return output.ToString();
    }

    private static bool IsMultiValueToken(string token) =>
        Regex.IsMatch(token, @"^%[SR](\d+-\d+)?$", RegexOptions.CultureInvariant);

    private static string Evaluate(Match m, ParameterContext c, bool emptyWhenNothingMarked, Func<IReadOnlyList<string>, Encoding, string> writeListFile)
    {
        // %$CLIPBOARD% / %$DATE+1D%
        if (m.Groups[1].Success)
        {
            var kind = m.Groups[1].Value;
            if (kind == "CLIPBOARD")
            {
                return c.ClipboardText ?? string.Empty;
            }
            var when = c.Now;
            if (m.Groups[2].Success)
            {
                var amount = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                when = m.Groups[3].Value switch
                {
                    "Y" => when.AddYears(amount),
                    "M" => when.AddMonths(amount),
                    "D" => when.AddDays(amount),
                    "h" => when.AddHours(amount),
                    "m" => when.AddMinutes(amount),
                    _ => when.AddSeconds(amount),
                };
            }
            return when.ToString(kind == "DATE" ? "yyyy-MM-dd" : "HH-mm-ss", CultureInfo.InvariantCulture);
        }
        // %|ENV|
        if (m.Groups[4].Success)
        {
            return Environment.GetEnvironmentVariable(m.Groups[4].Value) ?? string.Empty;
        }

        var code = m.Groups[5].Value;
        var arg = m.Groups[6].Success ? m.Groups[6].Value : null;
        string value;
        switch (code)
        {
            case "Q":
            case "Z":
                return string.Empty;
            case "P":
                value = WithSeparator(c.SourceDirectory, c.Separator);
                break;
            case "T":
                value = WithSeparator(c.TargetDirectory, c.Separator);
                break;
            case "N":
                value = c.CursorName ?? string.Empty;
                break;
            case "M":
                value = c.TargetCursorName ?? string.Empty;
                break;
            case "O":
                value = c.CursorName is { } n ? Path.GetFileNameWithoutExtension(n) : string.Empty;
                break;
            case "E":
                value = c.CursorName is { } e ? Path.GetExtension(e).TrimStart('.') : string.Empty;
                break;
            case "B":
                value = FolderName(c.SourceDirectory, arg is { } up && up.StartsWith('+') ? int.Parse(up[1..], CultureInfo.InvariantCulture) : 0);
                break;
            case "C1":
            case "C2":
                value = ComparePair(c)[code == "C1" ? 0 : 1];
                break;
            case "S":
            case "R":
            {
                var names = Effective(code == "S" ? c.Selected : c.TargetSelected, code == "S" ? c.CursorName : c.TargetCursorName, emptyWhenNothingMarked);
                names = Slice(names, arg);
                return string.Join(' ', names.Select(Quote));
            }
            default:
            {
                // List files: L/F (system default encoding = UTF-8 here), UL/UF UTF-8, WL/WF UTF-16.
                var names = Effective(c.Selected, c.CursorName, emptyWhenNothingMarked);
                var full = code.EndsWith('L');
                var lines = full ? names.Select(n => Join(c.SourceDirectory, n, c.Separator)).ToList() : names.ToList();
                var encoding = code.StartsWith('W') ? Encoding.Unicode : new UTF8Encoding(code.StartsWith('U'));
                return writeListFile(lines, encoding);
            }
        }

        if (m.Groups[7].Success)
        {
            value = Substring(value, int.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture),
                m.Groups[9].Success ? int.Parse(m.Groups[9].Value, CultureInfo.InvariantCulture) : null);
        }
        return value;
    }

    private static IReadOnlyList<string> Effective(IReadOnlyList<string> marked, string? cursor, bool emptyWhenNothingMarked) =>
        marked.Count > 0 ? marked : emptyWhenNothingMarked || cursor is null ? [] : [cursor];

    private static IReadOnlyList<string> Slice(IReadOnlyList<string> names, string? arg)
    {
        if (string.IsNullOrEmpty(arg) || arg.StartsWith('+'))
        {
            return names;
        }
        var dash = arg.IndexOf('-', StringComparison.Ordinal);
        var from = int.Parse(dash < 0 ? arg : arg[..dash], CultureInfo.InvariantCulture);
        var to = dash < 0 ? from : int.Parse(arg[(dash + 1)..], CultureInfo.InvariantCulture);
        return names.Skip(from - 1).Take(Math.Max(0, to - from + 1)).ToList();
    }

    private static string[] ComparePair(ParameterContext c)
    {
        if (c.Selected.Count >= 2)
        {
            return [Join(c.SourceDirectory, c.Selected[0], c.Separator), Join(c.SourceDirectory, c.Selected[1], c.Separator)];
        }
        var first = c.Selected.Count == 1 ? c.Selected[0] : c.CursorName ?? string.Empty;
        return [Join(c.SourceDirectory, first, c.Separator), Join(c.TargetDirectory, c.TargetCursorName ?? string.Empty, c.Separator)];
    }

    private static string FolderName(string directory, int levelsUp)
    {
        var d = directory.TrimEnd('/', '\\');
        for (var i = 0; i < levelsUp; i++)
        {
            d = Path.GetDirectoryName(d) ?? string.Empty;
        }
        var name = Path.GetFileName(d);
        return string.IsNullOrEmpty(name) ? d : name;
    }

    private static string Substring(string value, int start, int? length)
    {
        if (start < 0)
        {
            start = Math.Max(0, value.Length + start);
        }
        if (start >= value.Length)
        {
            return string.Empty;
        }
        var len = length is null ? value.Length - start : length.Value < 0 ? Math.Max(0, value.Length - start + length.Value) : Math.Min(length.Value, value.Length - start);
        return value.Substring(start, len);
    }

    private static string Join(string dir, string name, char separator) => WithSeparator(dir, separator) + name;

    private static string WithSeparator(string dir, char separator) =>
        dir.EndsWith('/') || dir.EndsWith('\\') ? dir : dir + separator;

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? "\"" + s + "\"" : s;

    private static string WriteTempListFile(IReadOnlyList<string> lines, Encoding encoding)
    {
        var path = Path.Combine(Path.GetTempPath(), "fm-list-" + Guid.NewGuid().ToString("N")[..8] + ".lst");
        File.WriteAllLines(path, lines, encoding);
        return path;
    }

    /// <summary>Splits on spaces; a quoted run is one token (quotes removed, re-added on output).</summary>
    private static IEnumerable<(string Token, string Separator, bool Quoted)> Tokenize(string template)
    {
        var i = 0;
        while (i < template.Length)
        {
            var start = i;
            var quoted = template[i] == '"';
            string token;
            if (quoted)
            {
                var end = template.IndexOf('"', i + 1);
                if (end < 0)
                {
                    end = template.Length;
                }
                token = template[(i + 1)..end];
                i = Math.Min(template.Length, end + 1);
            }
            else
            {
                while (i < template.Length && template[i] != ' ')
                {
                    i++;
                }
                token = template[start..i];
            }
            var sepStart = i;
            while (i < template.Length && template[i] == ' ')
            {
                i++;
            }
            yield return (token, template[sepStart..i], quoted);
        }
    }
}
