using System.Text;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Matching;

/// <summary>
/// File mask:
/// <c>*.txt *.doc;readme* | *.bak obj\</c>
/// <list type="bullet">
/// <item>Patterns are separated by spaces or ';' (use quotes for names containing spaces).</item>
/// <item>Patterns after '|' exclude.</item>
/// <item>A pattern ending in '\' or '/' applies to directories only.</item>
/// <item><c>*.*</c> also matches names without an extension.</item>
/// </list>
/// Matching is case-insensitive.
/// </summary>
public sealed class WildcardMatcher
{
    private readonly PatternSet _fileIncludes = new();
    private readonly PatternSet _dirIncludes = new();
    private readonly PatternSet _fileExcludes = new();
    private readonly PatternSet _dirExcludes = new();

    private WildcardMatcher(string mask)
    {
        Mask = mask;
    }

    public static WildcardMatcher All { get; } = Parse("*");

    public string Mask { get; }

    /// <summary>True when the mask contains directory patterns (e.g. <c>src\</c>), i.e. selects directories.</summary>
    public bool HasDirectoryIncludes => _dirIncludes.Count > 0;

    /// <summary>True when the mask contains file patterns (a mask like <c>obj\</c> has none).</summary>
    public bool HasFileIncludes => _fileIncludes.Count > 0;

    public static WildcardMatcher Parse(string? mask)
    {
        mask = string.IsNullOrWhiteSpace(mask) ? "*" : mask.Trim();
        var matcher = new WildcardMatcher(mask);
        var pipe = IndexOfUnquoted(mask, '|');
        var includePart = pipe < 0 ? mask : mask[..pipe];
        var excludePart = pipe < 0 ? string.Empty : mask[(pipe + 1)..];

        foreach (var token in Tokenize(includePart))
        {
            Add(token, matcher._fileIncludes, matcher._dirIncludes);
        }
        foreach (var token in Tokenize(excludePart))
        {
            Add(token, matcher._fileExcludes, matcher._dirExcludes);
        }
        return matcher;
    }

    public bool IsMatch(string name, bool isDirectory) =>
        isDirectory ? MatchesDirectory(name) : MatchesFile(name);

    /// <summary>File passes include patterns (or there are none for files) and is not excluded.</summary>
    public bool MatchesFile(string name)
    {
        var included = _fileIncludes.Count == 0 || _fileIncludes.IsMatch(name);
        return included && !_fileExcludes.IsMatch(name);
    }

    /// <summary>Directory is not excluded and passes directory include patterns (if any).</summary>
    public bool MatchesDirectory(string name)
    {
        var included = _dirIncludes.Count == 0 || _dirIncludes.IsMatch(name);
        return included && !_dirExcludes.IsMatch(name);
    }

    /// <summary>True when a "| name\" pattern excludes this directory (find files: skip the subtree).</summary>
    public bool IsDirectoryExcluded(string name) => _dirExcludes.IsMatch(name);

    /// <summary>Selection semantics (Num+): directories are only selected by explicit directory patterns.</summary>
    public bool SelectsDirectory(string name) => HasDirectoryIncludes && MatchesDirectory(name);

    public static Regex ToRegex(string glob)
    {
        if (glob == "*.*")
        {
            glob = "*";
        }
        var sb = new StringBuilder("^");
        foreach (var c in glob)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public override string ToString() => Mask;

    private static void Add(string token, PatternSet files, PatternSet dirs)
    {
        if (token.EndsWith('\\') || token.EndsWith('/'))
        {
            var dir = token.TrimEnd('\\', '/');
            dirs.Add(dir.Length == 0 ? "*" : dir);
        }
        else
        {
            files.Add(token);
        }
    }

    /// <summary>
    /// Patterns of one kind. Common shapes avoid regex entirely: "*" (everything), "*.ext" (extension lookup)
    /// and names without wildcards (exact lookup). Matching is case-insensitive.
    /// </summary>
    private sealed class PatternSet
    {
        private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> _regexes = [];
        private bool _all;

        public int Count { get; private set; }

        public void Add(string glob)
        {
            Count++;
            if (glob is "*" or "*.*")
            {
                _all = true;
            }
            else if (glob.Length > 2 && glob.StartsWith("*.", StringComparison.Ordinal) && glob.AsSpan(2).IndexOfAny("*?.") < 0)
            {
                _extensions.Add(glob[2..]);
            }
            else if (glob.AsSpan().IndexOfAny("*?") < 0)
            {
                _exact.Add(glob);
            }
            else
            {
                _regexes.Add(ToRegex(glob));
            }
        }

        public bool IsMatch(string name)
        {
            if (_all || _exact.Contains(name))
            {
                return true;
            }
            if (_extensions.Count > 0)
            {
                var dot = name.LastIndexOf('.');
                if (dot >= 0 && _extensions.Contains(name[(dot + 1)..]))
                {
                    return true;
                }
            }
            foreach (var regex in _regexes)
            {
                if (regex.IsMatch(name))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static IEnumerable<string> Tokenize(string part)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in part)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && (c == ' ' || c == ';' || c == '\t'))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static int IndexOfUnquoted(string s, char ch)
    {
        var quoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && s[i] == ch)
            {
                return i;
            }
        }
        return -1;
    }
}
