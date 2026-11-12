using System.Globalization;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Matching;

/// <summary>
/// Expands the name syntax of "new folder" / "new file" (F7 / Shift+F4):
/// <c>a|b|"c d"</c> → three names, <c>dir&lt;1-3&gt;</c> → dir1, dir2, dir3, <c>&lt;01-10&gt;</c> keeps zero padding.
/// Several ranges in one name produce every combination.
/// </summary>
public static partial class NameExpander
{
    public const int MaxNames = 10_000;

    [GeneratedRegex(@"<(\d+)-(\d+)>", RegexOptions.CultureInvariant)]
    private static partial Regex RangeRegex();

    public static IReadOnlyList<string> Expand(string? input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return result;
        }
        foreach (var part in SplitAlternatives(input))
        {
            ExpandRanges(part, result);
            if (result.Count > MaxNames)
            {
                throw new ArgumentException($"Too many names (more than {MaxNames}).", nameof(input));
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void ExpandRanges(string name, List<string> output)
    {
        var match = RangeRegex().Match(name);
        if (!match.Success)
        {
            output.Add(name);
            return;
        }
        var fromText = match.Groups[1].Value;
        var toText = match.Groups[2].Value;
        var from = long.Parse(fromText, CultureInfo.InvariantCulture);
        var to = long.Parse(toText, CultureInfo.InvariantCulture);
        var width = fromText.StartsWith('0') && fromText.Length > 1 ? fromText.Length : 0;
        var step = from <= to ? 1 : -1;
        for (var i = from; ; i += step)
        {
            var number = width > 0 ? i.ToString(new string('0', width), CultureInfo.InvariantCulture) : i.ToString(CultureInfo.InvariantCulture);
            ExpandRanges(name[..match.Index] + number + name[(match.Index + match.Length)..], output);
            if (i == to || output.Count > MaxNames)
            {
                break;
            }
        }
    }

    private static IEnumerable<string> SplitAlternatives(string input)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in input)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (c == '|' && !quoted)
            {
                if (current.ToString().Trim() is { Length: > 0 } s)
                {
                    yield return s;
                }
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }
}
