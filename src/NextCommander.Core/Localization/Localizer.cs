using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NextCommander.Core.Localization;

/// <summary>
/// UI translation keyed by the English source text (gettext style), so code keeps plain English literals. The language
/// files themselves use readable keys ("Menu.nc_Copy", "Settings.Display.ShowHiddenFiles"): en.json gives the English
/// text of every key, another language file its translation; <see cref="Join"/> links them.
/// Entries are exact texts ("Copy" → "Kopyala") or patterns with placeholders ("Delete {0} files?" →
/// "{0} dosya silinsin mi?") that also translate interpolated strings; placeholder values are translated too when they
/// are known texts. Unknown texts stay as they are.
/// </summary>
public sealed partial class Localizer
{
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly List<(Regex Pattern, string Template, int Literal)> _patterns = [];
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>The language of the texts in the code (en.json): needs no translation.</summary>
    public const string BaseLanguage = "en";

    /// <summary>The active translation (null: English, nothing translated).</summary>
    public static Localizer? Current { get; set; }

    /// <summary>Language code of <see cref="Current"/> (<see cref="BaseLanguage"/> when none).</summary>
    public static string CurrentLanguage => Current?.Language ?? BaseLanguage;

    public string Language { get; }

    public int Count => _exact.Count + _patterns.Count;

    /// <summary>Whether a text is one of the translations (already in the target language).</summary>
    public bool IsTranslation(string text) => (_values ??= [.. _exact.Values, .. _patterns.Select(p => p.Template)]).Contains(text);

    private HashSet<string>? _values;

    public Localizer(string language, IReadOnlyDictionary<string, string> entries)
    {
        Language = language;
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value) || key == NameKey)
            {
                continue; // "_name": the language's own name, not a text
            }
            if (Placeholder().IsMatch(key))
            {
                // Kept even when the template is the same ("{0} ▲"): the placeholder values still get translated.
                var literal = Placeholder().Replace(key, string.Empty);
                if (literal.Trim().Length == 0)
                {
                    continue; // "{0}", "{0}{1}": would match anything
                }
                _patterns.Add((ToRegex(key), value, literal.Length));
            }
            else if (key != value)
            {
                _exact[key] = value;
            }
        }
        // Most specific (longest fixed text) first: "{0} files" must not win over "Delete {0} files?".
        _patterns.Sort((a, b) => b.Literal.CompareTo(a.Literal));
    }

    /// <summary>
    /// Links a language file to the texts in the code: <paramref name="source"/> (en.json) maps keys to English texts,
    /// <paramref name="target"/> the same keys to translations. Gives "English text": "translation" pairs. An entry of the
    /// target whose key is no key of the source is taken as "English text": "translation".
    /// </summary>
    public static Dictionary<string, string> Join(IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> target)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in target)
        {
            if (key == NameKey)
            {
                pairs[key] = value;
            }
            else if (source.TryGetValue(key, out var text))
            {
                pairs[text] = value;
            }
            else if (!KeyPattern().IsMatch(key))
            {
                pairs[key] = value;
            }
        }
        return pairs;
    }

    /// <summary>A translation from a language file and en.json (see <see cref="Join"/>).</summary>
    public static Localizer FromFiles(string language, string sourceJson, string targetJson) =>
        new(language, Join(ParseFile(sourceJson), ParseFile(targetJson)));

    /// <summary>"Menu.nc_Copy", "Settings.Display.ShowHiddenFiles": a key of a language file (not a text).</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex KeyPattern();

    /// <summary>Loads "English text": "translation" pairs from JSON.</summary>
    public static Localizer FromJson(string language, string json) =>
        new(language, ParseFile(json));

    /// <summary>Entries of a language file (comments and trailing commas allowed).</summary>
    public static Dictionary<string, string> ParseFile(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, s_fileOptions) ?? [];

    private static readonly JsonSerializerOptions s_fileOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Key of a language file that holds the language's own name ("English", "Deutsch").</summary>
    public const string NameKey = "_name";

    /// <summary>Translates with <see cref="Current"/>; the text itself when there is no translation.</summary>
    public static string T(string text) => Current?.Translate(text) ?? text;

    /// <summary>Translation of <paramref name="text"/> (unchanged when unknown).</summary>
    public string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || !NeedsTranslation(text))
        {
            return text ?? string.Empty;
        }
        return _cache.GetOrAdd(text, TranslateCore);
    }

    private string TranslateCore(string text)
    {
        if (_exact.TryGetValue(text, out var exact))
        {
            return exact;
        }
        // Surrounding white space and a trailing colon or ellipsis keep their place.
        var trimmed = text.Trim();
        if (trimmed.Length != text.Length && _exact.TryGetValue(trimmed, out var inner))
        {
            return text.Replace(trimmed, inner, StringComparison.Ordinal);
        }
        foreach (var (pattern, template, _) in _patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }
            return Placeholder().Replace(template, m =>
            {
                var index = int.Parse(m.Groups["index"].Value, CultureInfo.InvariantCulture);
                var group = match.Groups["p" + index.ToString(CultureInfo.InvariantCulture)];
                return group.Success ? Translate(group.Value) : m.Value;
            });
        }
        // Multi-line messages: translate line by line when every line is known.
        if (text.Contains('\n', StringComparison.Ordinal))
        {
            var lines = text.Split('\n');
            var translated = lines.Select(l => l.Length == 0 ? l : Translate(l.TrimEnd('\r')) + (l.EndsWith('\r') ? "\r" : string.Empty)).ToArray();
            if (!translated.SequenceEqual(lines))
            {
                return string.Join('\n', translated);
            }
        }
        return text;
    }

    /// <summary>Numbers, paths and other texts without letters are never translated.</summary>
    private static bool NeedsTranslation(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>"{0} files ({1:N0} bytes)" → ^(?&lt;p0&gt;.+?) files \((?&lt;p1&gt;.+?) bytes\)$</summary>
    private static Regex ToRegex(string key)
    {
        var sb = new StringBuilder("^");
        var last = 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Placeholder().Matches(key))
        {
            sb.Append(Regex.Escape(key[last..m.Index]));
            var name = "p" + m.Groups["index"].Value;
            // A repeated placeholder must match the same text.
            sb.Append(used.Add(name) ? $"(?<{name}>.*?)" : $"\\k<{name}>");
            last = m.Index + m.Length;
        }
        sb.Append(Regex.Escape(key[last..])).Append('$');
        return new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }

    [GeneratedRegex(@"\{(?<index>\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    /// <summary>"system" → "tr" on a Turkish system, otherwise "en"; explicit codes are kept.</summary>
    public static string Resolve(string? setting, CultureInfo uiCulture) =>
        setting is null or "" or "system" ? (uiCulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en") : setting;
}
