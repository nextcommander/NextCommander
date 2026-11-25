using System.Text.RegularExpressions;
using NextCommander.Core.Compare;

namespace NextCommander.Core.Config;

/// <summary>How spaces count when lines are compared.</summary>
public enum CompareWhitespace
{
    /// <summary>Every space counts.</summary>
    Compare,

    /// <summary>More or fewer spaces count as one, spaces at the ends do not count.</summary>
    IgnoreChanges,

    /// <summary>No space counts ("a b" = "ab").</summary>
    IgnoreAll,
}

/// <summary>A line filter: a difference whose lines all match it is ignored.</summary>
public sealed class CompareLineFilter
{
    public string Pattern { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>A substitution: what matches is replaced before the lines are compared (the text shown stays).</summary>
public sealed class CompareSubstitution
{
    public string Pattern { get; set; } = string.Empty;

    public string Replacement { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Compare by content (the plan of 25 November 2026, phase 3; Configuration > Operation/Compare): what the comparison
/// ignores, how differences are shown, the font, a backup when saving, an external compare program.
/// </summary>
public sealed class CompareSettings
{
    public bool IgnoreCase { get; set; }

    public CompareWhitespace Whitespace { get; set; } = CompareWhitespace.Compare;

    public bool IgnoreBlankLines { get; set; }

    /// <summary>CRLF and LF files compare equal (off: a different line end makes every line different).</summary>
    public bool IgnoreLineEndings { get; set; } = true;

    /// <summary>Removed lines added elsewhere are shown as moved.</summary>
    public bool DetectMoved { get; set; } = true;

    /// <summary>The alike lines of a difference sit side by side.</summary>
    public bool MatchSimilarLines { get; set; } = true;

    /// <summary>The differences inside a changed line are marked by words (off: by characters).</summary>
    public bool WordLevel { get; set; }

    public List<CompareLineFilter> LineFilters { get; set; } = [];

    public List<CompareSubstitution> Substitutions { get; set; } = [];

    /// <summary>The font of the compare window; empty = the Lister's.</summary>
    public string Font { get; set; } = string.Empty;

    /// <summary>The font size; 0 = the Lister's.</summary>
    public double FontSize { get; set; }

    /// <summary>Saving keeps the file as it was in "name.bak" first.</summary>
    public bool Backup { get; set; }

    /// <summary>Files > Compare by content opens the program below instead of the window.</summary>
    public bool UseExternal { get; set; }

    /// <summary>An external compare program.</summary>
    public string ExternalProgram { get; set; } = string.Empty;

    /// <summary>Its parameters: %1 the left file, %2 the right one.</summary>
    public string ExternalArguments { get; set; } = "\"%1\" \"%2\"";

    /// <summary>A pattern that is not a valid regular expression (the settings window refuses it): its error, null when fine.</summary>
    public static string? PatternProblem(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "The pattern is empty";
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return null;
        }
        catch (ArgumentException ex)
        {
            return $"Not a regular expression: {ex.Message}";
        }
    }

    /// <summary>The options of the diff engine (filters that are off or not valid left out).</summary>
    public DiffOptions ToOptions()
    {
        static Regex? Compile(string pattern) =>
            PatternProblem(pattern) is null ? new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) : null;
        return new DiffOptions
        {
            IgnoreCase = IgnoreCase,
            IgnoreWhitespace = Whitespace == CompareWhitespace.IgnoreChanges,
            IgnoreAllWhitespace = Whitespace == CompareWhitespace.IgnoreAll,
            IgnoreBlankLines = IgnoreBlankLines,
            IgnoreLineEndings = IgnoreLineEndings,
            DetectMoved = DetectMoved,
            MatchSimilarLines = MatchSimilarLines,
            IgnoreLines = [.. LineFilters.Where(f => f.Enabled).Select(f => Compile(f.Pattern)).OfType<Regex>()],
            Substitutions = [.. Substitutions.Where(s => s.Enabled).Select(s => Compile(s.Pattern) is { } regex ? new DiffSubstitution(regex, s.Replacement) : null).OfType<DiffSubstitution>()],
        };
    }

    /// <summary>The program and its parameters for two files (%1 / %2 replaced).</summary>
    public (string Program, string Arguments) ExternalCommand(string left, string right) =>
        (ExternalProgram, ExternalArguments.Replace("%1", left, StringComparison.Ordinal).Replace("%2", right, StringComparison.Ordinal));
}
