using System.Text.RegularExpressions;

namespace NextCommander.Core.Search;

/// <summary>Plain or regex text search with case and whole-word options (find files content search, Lister F7).</summary>
public sealed class TextMatcher
{
    /// <summary>How far a regex match may reach across chunk boundaries.</summary>
    public const int RegexReach = 4096;

    private readonly Regex? _regex;
    private readonly StringComparison _comparison;

    public TextMatcher(string pattern, bool caseSensitive = false, bool wholeWords = false, bool regex = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        Pattern = pattern;
        CaseSensitive = caseSensitive;
        WholeWords = wholeWords;
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (regex)
        {
            var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
            if (!caseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }
            _regex = new Regex(pattern, options, TimeSpan.FromSeconds(5));
        }
        IsAscii = pattern.All(char.IsAscii);
    }

    public string Pattern { get; }

    public bool CaseSensitive { get; }

    public bool WholeWords { get; }

    public bool IsRegex => _regex is not null;

    /// <summary>True when the pattern only has ASCII characters (then single-byte code pages need no separate pass).</summary>
    public bool IsAscii { get; }

    /// <summary>Longest possible match (chunk overlap).</summary>
    public int MaxMatchLength => _regex is null ? Pattern.Length : RegexReach;

    /// <summary>First match at or after <paramref name="start"/>; (-1, 0) when none.</summary>
    public (int Index, int Length) Find(ReadOnlySpan<char> text, int start = 0)
    {
        if (_regex is not null)
        {
            foreach (var m in _regex.EnumerateMatches(text, start))
            {
                if (m.Length > 0 && (!WholeWords || IsWordAt(text, m.Index, m.Length)))
                {
                    return (m.Index, m.Length);
                }
            }
            return (-1, 0);
        }
        var pos = start;
        while (pos <= text.Length - Pattern.Length)
        {
            var found = text[pos..].IndexOf(Pattern.AsSpan(), _comparison);
            if (found < 0)
            {
                return (-1, 0);
            }
            var index = pos + found;
            if (!WholeWords || IsWordAt(text, index, Pattern.Length))
            {
                return (index, Pattern.Length);
            }
            pos = index + 1;
        }
        return (-1, 0);
    }

    /// <summary>Last match that starts before <paramref name="end"/>; (-1, 0) when none.</summary>
    public (int Index, int Length) FindLast(ReadOnlySpan<char> text, int end)
    {
        var last = (-1, 0);
        var pos = 0;
        while (true)
        {
            var (index, length) = Find(text, pos);
            if (index < 0 || index >= end)
            {
                return last;
            }
            last = (index, length);
            pos = index + 1;
        }
    }

    public bool IsMatch(string text) => Find(text).Index >= 0;

    private static bool IsWordAt(ReadOnlySpan<char> text, int index, int length)
    {
        var beforeOk = index == 0 || !IsWordChar(text[index - 1]);
        var end = index + length;
        var afterOk = end >= text.Length || !IsWordChar(text[end]);
        return beforeOk && afterOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
