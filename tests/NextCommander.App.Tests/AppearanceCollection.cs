namespace NextCommander.App.Tests;

/// <summary>
/// Tests that change application-wide appearance (theme, color scheme, panel font, translation) run alone: tests share
/// one UI thread and interleave at awaits, so another test's ApplySettings could undo their changes mid-test.
/// </summary>
[CollectionDefinition("Appearance", DisableParallelization = true)]
public sealed class AppearanceCollection;
