using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.Core.Localization;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The shipped languages (German, Spanish, French, Chinese) translate the main window.</summary>
[Collection("Appearance")]
public sealed class ShippedLanguagesTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        Localizer.Current = null;
        _fx.Dispose();
    }

    [AvaloniaTheory]
    [InlineData("de", "_Dateien")]
    [InlineData("es", "_Archivos")]
    [InlineData("fr", "_Fichiers")]
    [InlineData("zh", "文件(_F)")]
    public async Task The_main_window_in_the_language(string code, string filesMenu)
    {
        UiLocalization.Install(); // the TextBlock hook (as at the program's start)
        Localizer.Current = UiLocalization.Read(code);
        Assert.NotNull(Localizer.Current);
        var (w, vm) = await _fx.OpenAsync();
        await SettleAsync(4);
        var files = w.GetLogicalDescendants().OfType<Menu>().First(m => m.Name == "MainMenu").Items.OfType<MenuItem>().First();
        // what is drawn (the translation happens in the TextBlock of the menu item's template)
        var header = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(files).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        Assert.Equal(filesMenu.Replace("_", string.Empty, StringComparison.Ordinal), (header ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal));
        // a setting and a message with a {0} hole
        Assert.NotEqual("Show hidden files", Localizer.Current!.Translate("Show hidden files"));
        Assert.Contains("12", Localizer.Current.Translate("12 files"), StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            files.IsSubMenuOpen = true;
            await SettleAsync(4);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, $"language-{code}.png"));
            files.IsSubMenuOpen = false;
        }
    }
}
