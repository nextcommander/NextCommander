using NextCommander.Core.Compare;
using NextCommander.Core.Config;

namespace NextCommander.Core.Tests;

/// <summary>The compare settings (the compare and merge plan, phase 3; 25 November 2026).</summary>
public sealed class CompareSettingsTests
{
    [Fact]
    public void The_settings_become_the_engine_s_options()
    {
        var settings = new CompareSettings
        {
            IgnoreCase = true,
            Whitespace = CompareWhitespace.IgnoreAll,
            IgnoreBlankLines = true,
            IgnoreLineEndings = false,
            DetectMoved = false,
            LineFilters = [new CompareLineFilter { Pattern = "^#" }, new CompareLineFilter { Pattern = "^//", Enabled = false }, new CompareLineFilter { Pattern = "(" }],
            Substitutions = [new CompareSubstitution { Pattern = @"\d+", Replacement = "N" }],
        };
        var options = settings.ToOptions();
        Assert.True(options.IgnoreCase);
        Assert.True(options.IgnoreAllWhitespace);
        Assert.False(options.IgnoreWhitespace);
        Assert.True(options.IgnoreBlankLines);
        Assert.False(options.IgnoreLineEndings);
        Assert.False(options.DetectMoved);
        Assert.Equal("^#", Assert.Single(options.IgnoreLines).ToString()); // the one that is off and the broken one left out
        Assert.Equal("N", Assert.Single(options.Substitutions).Replacement);

        // the substitution works in the engine
        Assert.All(TextDiff.Compare(["build 12"], ["build 345"], options), r => Assert.Equal(DiffKind.Equal, r.Kind));
    }

    [Fact]
    public void Patterns_are_checked_and_the_external_command_is_built()
    {
        Assert.Null(CompareSettings.PatternProblem(@"^\s*//"));
        Assert.NotNull(CompareSettings.PatternProblem("(unclosed"));
        Assert.NotNull(CompareSettings.PatternProblem(" "));

        var settings = new CompareSettings { ExternalProgram = "difftool" };
        Assert.Equal(("difftool","\"/a/x.txt\" \"/b/x.txt\""), settings.ExternalCommand("/a/x.txt", "/b/x.txt"));
        settings.ExternalArguments = "/e /u %1 %2";
        Assert.Equal("/e /u l r", settings.ExternalCommand("l", "r").Arguments);
    }
}
