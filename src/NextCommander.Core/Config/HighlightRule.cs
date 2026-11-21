using System.Text.RegularExpressions;

namespace NextCommander.Core.Config;

/// <summary>
/// Words colored in the terminal's output (user's request, 21 November 2026): whole
/// words, case ignored, in text the program writes without a color of its own.
/// </summary>
public sealed class HighlightRule
{
    /// <summary>The words, separated by commas or spaces ("error, failed, denied").</summary>
    public string Words { get; set; } = string.Empty;

    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = "#E5484D";

    /// <summary>The words of the rule.</summary>
    public IEnumerable<string> WordList => Words.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The rule as a pattern of whole words (null: no words).</summary>
    public Regex? Pattern() => WordList.ToList() is { Count: > 0 } words
        ? new Regex(@"(?<![\w-])(" + string.Join('|', words.Select(Regex.Escape)) + @")(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
        : null;

    /// <summary>The rules a new installation starts with: errors red, warnings amber, success green.</summary>
    public static List<HighlightRule> Defaults() =>
    [
        new() { Words = "error, errors, failed, failure, fatal, denied, refused, critical, exception", Color = "#E5484D" },
        new() { Words = "warning, warnings, warn, deprecated", Color = "#E2A400" },
        new() { Words = "ok, success, succeeded, done, passed, active", Color = "#2E9E44" },
    ];
}
