using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The compare colors of the color themes (keys Compare.*; user's request, 25 November 2026).</summary>
[Collection("Appearance")] // switches the color theme
public sealed class CompareColorsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        App.ApplyColorTheme(null);
        ColorThemes.UserDirectory = null;
        _fx.Dispose();
    }

    [AvaloniaFact]
    public async Task A_theme_colors_the_compare_window_and_the_defaults_are_the_design_s()
    {
        var (_, vm) = await _fx.OpenAsync();
        Assert.Contains(ColorThemes.Keys, k => k.Key == "Compare.Added" && k.Targets[0] == "FmCompareAddedBrush");
        Assert.Equal(Color.Parse("#E9F7EE"), ColorThemes.DefaultColor(ColorThemes.Keys.First(k => k.Key == "Compare.Added"), dark: false));

        File.WriteAllText(_fx.L("a.txt"), "first()\nsecond()\nend\n");
        File.WriteAllText(_fx.R("a.txt"), "second()\nend\nfirst()\nnew\n");
        var window = _fx.Track(new CompareWindow((await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L("a.txt"))))!,
            (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.R("a.txt"))))!, vm.Tools));
        window.AskSaveOnClose = _ => Task.FromResult(SaveChoice.Discard);
        window.Show();
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });
        Assert.Equal(Color.Parse("#E9F7EE"), window.AddedColor); // the design's color without a theme
        Assert.Contains(window.Rows, r => r.MovedTo is not null);

        // a theme of the user's with its own compare colors: the open window takes them
        var themes = Path.Combine(_fx.Root, "themes");
        Directory.CreateDirectory(themes);
        File.WriteAllText(Path.Combine(themes, "compare-test.json"),
            """{ "Name": "Compare test", "Base": "Light", "Colors": { "Compare.Added": "#123456", "Compare.Moved": "#654321" } }""");
        ColorThemes.UserDirectory = themes;
        App.ApplyColorTheme("compare-test");
        await SettleAsync();
        Assert.Equal(Color.Parse("#123456"), window.AddedColor);
        Assert.Equal(Color.Parse("#654321"), window.MovedColor);

        App.ApplyColorTheme(null);
        await SettleAsync();
        Assert.Equal(Color.Parse("#E9F7EE"), window.AddedColor);
    }
}
