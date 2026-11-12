using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Rename;

public enum CaseMode
{
    Unchanged,
    Lower,
    Upper,
    /// <summary>First letter of the name upper case, the rest lower.</summary>
    FirstUpper,
    /// <summary>First letter of every word upper case.</summary>
    Title,
}

/// <summary>Multi-rename settings (Ctrl+M). Serializable: saved as presets.</summary>
public sealed class MultiRenameOptions
{
    /// <summary>Name template: <c>[N]</c> name, <c>[N2-5]</c>, <c>[N3]</c>, <c>[N-3,2]</c>, <c>[C]</c> counter, <c>[YMD]</c>, <c>[hms]</c>, <c>[P]</c>, <c>[G]</c>, <c>[S]</c>, <c>[E]</c>.</summary>
    public string NameMask { get; set; } = "[N]";

    /// <summary>Extension template (same placeholders), without the dot. Empty = no extension.</summary>
    public string ExtensionMask { get; set; } = "[E]";

    public string? Search { get; set; }

    public string? Replace { get; set; }

    public bool SearchIsRegex { get; set; }

    public bool SearchCaseSensitive { get; set; }

    /// <summary>Replace only the first occurrence.</summary>
    public bool ReplaceFirstOnly { get; set; }

    /// <summary>Search and replace in "name.ext" instead of the name only.</summary>
    public bool SearchIncludesExtension { get; set; }

    public CaseMode Case { get; set; }

    public long CounterStart { get; set; } = 1;

    public long CounterStep { get; set; } = 1;

    public int CounterDigits { get; set; } = 1;

    public MultiRenameOptions Clone() => (MultiRenameOptions)MemberwiseClone();
}

public enum RenameIssue
{
    None,
    /// <summary>Two files would get the same name.</summary>
    Duplicate,
    /// <summary>A file not being renamed already has the name.</summary>
    Exists,
    /// <summary>Empty or invalid name.</summary>
    Invalid,
}

/// <summary>One line of the preview.</summary>
public sealed record RenamePlanItem(VfsEntry Source, string NewName, RenameIssue Issue)
{
    public bool Changes => !string.Equals(Source.Name, NewName, StringComparison.Ordinal);
}

/// <summary>Multi-rename tool: templates, counter, search/replace, case.</summary>
public static partial class MultiRename
{
    private static readonly System.Buffers.SearchValues<char> s_invalid = System.Buffers.SearchValues.Create("<>:\"|?*\0");

    /// <summary>New names for <paramref name="entries"/> (in the given order: the counter follows it).</summary>
    /// <param name="existingNames">Names in the target folders that are not being renamed (conflict check).</param>
    /// <exception cref="ArgumentException">Invalid regex.</exception>
    public static IReadOnlyList<RenamePlanItem> Plan(IReadOnlyList<VfsEntry> entries, MultiRenameOptions options, ISet<string>? existingNames = null)
    {
        Regex? regex = null;
        if (!string.IsNullOrEmpty(options.Search) && options.SearchIsRegex)
        {
            regex = new Regex(options.Search, options.SearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        var names = new List<string>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            names.Add(NewName(entries[i], options, options.CounterStart + (i * options.CounterStep), regex));
        }
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var counts = names.Select((n, i) => Key(entries[i], n)).GroupBy(k => k, comparer).ToDictionary(g => g.Key, g => g.Count(), comparer);
        var renamedAway = new HashSet<string>(entries.Select(e => Key(e, e.Name)), comparer);
        var result = new List<RenamePlanItem>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var name = names[i];
            var key = Key(entries[i], name);
            var issue = !IsValid(name) ? RenameIssue.Invalid
                : counts[key] > 1 ? RenameIssue.Duplicate
                : existingNames is not null && !renamedAway.Contains(key) && existingNames.Contains(key) ? RenameIssue.Exists
                : RenameIssue.None;
            result.Add(new RenamePlanItem(entries[i], name, issue));
        }
        return result;
    }

    /// <summary>Folder-qualified key ("dir|name") so names in different folders do not collide.</summary>
    public static string Key(VfsEntry entry, string name)
    {
        var parent = entry.Path.Parent?.ToString() ?? string.Empty;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            parent += "/" + name[..slash].Replace('\\', '/');
            name = name[(slash + 1)..];
        }
        return parent + "|" + name;
    }

    private static bool IsValid(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.AsSpan().IndexOfAny(s_invalid) >= 0)
        {
            return false;
        }
        // '/' makes subfolders: every part must be a usable name.
        return name.Split('/', '\\').All(p => p.Length > 0 && p != "." && p != ".." && p.TrimEnd(' ', '.').Length > 0);
    }

    public static string NewName(VfsEntry entry, MultiRenameOptions options, long counter = 1, Regex? regex = null)
    {
        if (regex is null && options.SearchIsRegex && !string.IsNullOrEmpty(options.Search))
        {
            regex = new Regex(options.Search, options.SearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        var baseName = entry.IsDirectory ? entry.Name : entry.BaseName;
        var extension = entry.IsDirectory ? string.Empty : entry.Extension;
        var name = Expand(options.NameMask, entry, baseName, extension, counter, options);
        var ext = Expand(options.ExtensionMask, entry, baseName, extension, counter, options);
        var full = ext.Length > 0 ? name + "." + ext : name;

        if (!string.IsNullOrEmpty(options.Search))
        {
            if (options.SearchIncludesExtension || ext.Length == 0)
            {
                full = ReplaceText(full, options, regex);
            }
            else
            {
                full = ReplaceText(name, options, regex) + "." + ext;
            }
        }

        if (options.Case != CaseMode.Unchanged)
        {
            var dot = ext.Length > 0 ? full.LastIndexOf('.') : -1;
            full = dot > 0 ? ApplyCase(full[..dot], options.Case) + "." + ApplyCase(full[(dot + 1)..], options.Case is CaseMode.Upper ? CaseMode.Upper : CaseMode.Lower)
                : ApplyCase(full, options.Case);
        }
        return full;
    }

    private static string ReplaceText(string text, MultiRenameOptions options, Regex? regex)
    {
        var replacement = options.Replace ?? string.Empty;
        if (regex is not null)
        {
            return options.ReplaceFirstOnly ? regex.Replace(text, ConvertCaseEscapes(replacement), 1) : ApplyCaseEscapes(regex, text, replacement);
        }
        var comparison = options.SearchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Several search terms separated by '|': each is replaced.
        foreach (var term in options.Search!.Split('|'))
        {
            if (term.Length == 0)
            {
                continue;
            }
            if (options.ReplaceFirstOnly)
            {
                var index = text.IndexOf(term, comparison);
                if (index >= 0)
                {
                    text = text[..index] + replacement + text[(index + term.Length)..];
                }
            }
            else
            {
                text = text.Replace(term, replacement, comparison);
            }
        }
        return text;
    }

    /// <summary>Regex replacement with <c>\U...\E</c> / <c>\L...\E</c> case conversion of the inserted text.</summary>
    private static string ApplyCaseEscapes(Regex regex, string text, string replacement)
    {
        if (!replacement.Contains("\\U", StringComparison.Ordinal) && !replacement.Contains("\\L", StringComparison.Ordinal))
        {
            return regex.Replace(text, replacement);
        }
        return regex.Replace(text, m =>
        {
            var expanded = m.Result(replacement.Replace("\\U", "\u0001", StringComparison.Ordinal).Replace("\\L", "\u0002", StringComparison.Ordinal).Replace("\\E", "\u0003", StringComparison.Ordinal));
            var sb = new StringBuilder();
            var mode = 0;
            foreach (var c in expanded)
            {
                switch (c)
                {
                    case '\u0001':
                        mode = 1;
                        break;
                    case '\u0002':
                        mode = 2;
                        break;
                    case '\u0003':
                        mode = 0;
                        break;
                    default:
                        sb.Append(mode == 1 ? char.ToUpper(c, CultureInfo.CurrentCulture) : mode == 2 ? char.ToLower(c, CultureInfo.CurrentCulture) : c);
                        break;
                }
            }
            return sb.ToString();
        });
    }

    private static string ConvertCaseEscapes(string replacement) =>
        replacement.Replace("\\U", string.Empty, StringComparison.Ordinal).Replace("\\L", string.Empty, StringComparison.Ordinal).Replace("\\E", string.Empty, StringComparison.Ordinal);

    public static string ApplyCase(string text, CaseMode mode)
    {
        var culture = CultureInfo.CurrentCulture;
        return mode switch
        {
            CaseMode.Lower => text.ToLower(culture),
            CaseMode.Upper => text.ToUpper(culture),
            CaseMode.FirstUpper => text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..].ToLower(culture),
            CaseMode.Title => culture.TextInfo.ToTitleCase(text.ToLower(culture)),
            _ => text,
        };
    }

    /// <summary>Expands the placeholders of one template.</summary>
    public static string Expand(string mask, VfsEntry entry, string baseName, string extension, long counter, MultiRenameOptions options)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < mask.Length)
        {
            if (mask[i] == '[')
            {
                if (i + 1 < mask.Length && mask[i + 1] == '[')
                {
                    sb.Append('['); // "[[" = literal bracket
                    i += 2;
                    continue;
                }
                var close = mask.IndexOf(']', i + 1);
                if (close > i)
                {
                    sb.Append(Placeholder(mask[(i + 1)..close], entry, baseName, extension, counter, options));
                    i = close + 1;
                    continue;
                }
            }
            if (mask[i] == ']' && i + 1 < mask.Length && mask[i + 1] == ']')
            {
                sb.Append(']');
                i += 2;
                continue;
            }
            sb.Append(mask[i]);
            i++;
        }
        return sb.ToString();
    }

    private static string Placeholder(string token, VfsEntry entry, string baseName, string extension, long counter, MultiRenameOptions options)
    {
        if (token.Length == 0)
        {
            return string.Empty;
        }
        var head = token[0];
        var rest = token[1..];
        var time = entry.LastWriteTime;
        switch (head)
        {
            case 'N':
                return Substring(baseName, rest);
            case 'E':
                return Substring(extension, rest);
            case 'A':
                return Substring(extension.Length > 0 ? baseName + "." + extension : baseName, rest);
            case 'C':
                return CounterText(counter, rest, options);
            case 'P':
                return Substring(entry.Path.Parent?.Name ?? string.Empty, rest);
            case 'G':
                return Substring(entry.Path.Parent?.Parent?.Name ?? string.Empty, rest);
            case 'S' when rest.Length == 0:
                return entry.Size.ToString(CultureInfo.InvariantCulture);
        }
        // Date / time letters: [YMD], [Y], [M], [D], [hms], [h], [m], [s]; "Y" alone = 4 digits.
        if (token.All(c => "YMDhmsy".Contains(c, StringComparison.Ordinal)))
        {
            var sb = new StringBuilder();
            foreach (var c in token)
            {
                sb.Append(c switch
                {
                    'Y' => time.ToString("yyyy", CultureInfo.InvariantCulture),
                    'y' => time.ToString("yy", CultureInfo.InvariantCulture),
                    'M' => time.ToString("MM", CultureInfo.InvariantCulture),
                    'D' => time.ToString("dd", CultureInfo.InvariantCulture),
                    'h' => time.ToString("HH", CultureInfo.InvariantCulture),
                    'm' => time.ToString("mm", CultureInfo.InvariantCulture),
                    _ => time.ToString("ss", CultureInfo.InvariantCulture),
                });
            }
            return sb.ToString();
        }
        return "[" + token + "]"; // unknown: keep as written
    }

    /// <summary>
    /// [N] whole, [N3] 3rd char, [N2-5] chars 2..5, [N2,3] 3 chars from the 2nd, [N-3] 3rd char from the end,
    /// [N2-] from the 2nd to the end, [N-3,2] 2 chars starting 3rd from the end. Positions are 1-based.
    /// </summary>
    internal static string Substring(string text, string spec)
    {
        if (spec.Length == 0)
        {
            return text;
        }
        var m = SubstringSpec().Match(spec);
        if (!m.Success)
        {
            return text;
        }
        var start = int.Parse(m.Groups["start"].Value, CultureInfo.InvariantCulture);
        var fromEnd = m.Groups["neg"].Success;
        var startIndex = fromEnd ? text.Length - start : start - 1;
        if (startIndex < 0 || startIndex >= text.Length)
        {
            return string.Empty;
        }
        int length;
        if (m.Groups["comma"].Success)
        {
            length = int.Parse(m.Groups["comma"].Value, CultureInfo.InvariantCulture);
        }
        else if (m.Groups["dash"].Success)
        {
            var end = m.Groups["end"].Value;
            if (end.Length == 0)
            {
                length = text.Length - startIndex;
            }
            else
            {
                var endIndex = m.Groups["endneg"].Success ? text.Length - int.Parse(end, CultureInfo.InvariantCulture) : int.Parse(end, CultureInfo.InvariantCulture) - 1;
                length = endIndex - startIndex + 1;
            }
        }
        else
        {
            length = 1;
        }
        length = Math.Clamp(length, 0, text.Length - startIndex);
        return text.Substring(startIndex, length);
    }

    /// <summary>[C] counter with the options; [C:5] digits, [C10+2:3] start 10, step 2, 3 digits.</summary>
    private static string CounterText(long counter, string spec, MultiRenameOptions options)
    {
        var digits = options.CounterDigits;
        if (spec.Length > 0)
        {
            var m = CounterSpec().Match(spec);
            if (m.Success)
            {
                if (m.Groups["start"].Success)
                {
                    var start = long.Parse(m.Groups["start"].Value, CultureInfo.InvariantCulture);
                    var step = m.Groups["step"].Success ? long.Parse(m.Groups["step"].Value, CultureInfo.InvariantCulture) : options.CounterStep;
                    var index = options.CounterStep == 0 ? 0 : (counter - options.CounterStart) / options.CounterStep;
                    counter = start + (index * step);
                }
                if (m.Groups["digits"].Success)
                {
                    digits = int.Parse(m.Groups["digits"].Value, CultureInfo.InvariantCulture);
                }
            }
        }
        return counter.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, digits), '0');
    }

    [GeneratedRegex(@"^(?<neg>-)?(?<start>\d+)(?:,(?<comma>\d+)|(?<dash>-)(?<endneg>-)?(?<end>\d*))?$")]
    private static partial Regex SubstringSpec();

    [GeneratedRegex(@"^(?<start>\d+)?(?:\+(?<step>-?\d+))?(?::(?<digits>\d+))?$")]
    private static partial Regex CounterSpec();
}
