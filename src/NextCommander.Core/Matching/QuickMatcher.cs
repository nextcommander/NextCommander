namespace NextCommander.Core.Matching;

public enum QuickMatchMode
{
    /// <summary>Name starts with the text (the default).</summary>
    Prefix,
    /// <summary>Text occurs anywhere in the name.</summary>
    Anywhere,
    /// <summary>Characters of the text occur in order ("fmgr" matches "NextCommander").</summary>
    Fuzzy,
}

/// <summary>Matcher for quick search (Ctrl+Alt+letter) and quick filter (Ctrl+S). Case-insensitive.
/// Text with * or ? is a wildcard mask with an implied trailing '*'.</summary>
public sealed class QuickMatcher
{
    private readonly string _text;
    private readonly QuickMatchMode _mode;
    private readonly System.Text.RegularExpressions.Regex? _regex;

    public QuickMatcher(string text, QuickMatchMode mode)
    {
        _text = text;
        _mode = mode;
        if (text.Contains('*', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal))
        {
            var mask = (mode == QuickMatchMode.Prefix ? string.Empty : "*") + text + (text.EndsWith('*') ? string.Empty : "*");
            _regex = WildcardMatcher.ToRegex(mask);
        }
    }

    public bool IsEmpty => _text.Length == 0;

    public bool IsMatch(string name)
    {
        if (_text.Length == 0)
        {
            return true;
        }
        if (_regex is not null)
        {
            return _regex.IsMatch(name);
        }
        return _mode switch
        {
            QuickMatchMode.Prefix => name.StartsWith(_text, StringComparison.OrdinalIgnoreCase),
            QuickMatchMode.Anywhere => name.Contains(_text, StringComparison.OrdinalIgnoreCase),
            _ => IsSubsequence(name),
        };
    }

    private bool IsSubsequence(string name)
    {
        var j = 0;
        for (var i = 0; i < name.Length && j < _text.Length; i++)
        {
            if (char.ToUpperInvariant(name[i]) == char.ToUpperInvariant(_text[j]))
            {
                j++;
            }
        }
        return j == _text.Length;
    }

    /// <summary>Index of the next matching name starting at <paramref name="start"/> (wrapping), or -1.</summary>
    public int FindNext(IReadOnlyList<string> names, int start, int direction = 1)
    {
        if (names.Count == 0)
        {
            return -1;
        }
        start = ((start % names.Count) + names.Count) % names.Count;
        for (var k = 0; k < names.Count; k++)
        {
            var i = ((start + (k * direction)) % names.Count + names.Count) % names.Count;
            if (IsMatch(names[i]))
            {
                return i;
            }
        }
        return -1;
    }
}
