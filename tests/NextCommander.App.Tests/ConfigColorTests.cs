using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Color: list colors from a theme file, inverted selection (Appearance collection).</summary>
[Collection("Appearance")]
public sealed class ConfigColorTests : IDisposable
{
    private readonly UiFixture _fx = new();
    private AppSettings? _settings;

    public void Dispose()
    {
        ColorThemes.UserDirectory = null;
        if (_settings is not null)
        {
            _settings.InvertedSelection = false;
            _settings.ColorThemeFile = string.Empty;
            App.ApplyAppearance(_settings);
        }
        _fx.Dispose();
    }

    private static Color Brush(string key) => ((ISolidColorBrush)App.Current!.Resources[key]!).Color;

    [AvaloniaFact]
    public async Task A_theme_chosen_in_the_configuration_is_applied_with_Apply()
    {
        var (w, vm) = await _fx.OpenAsync();
        _settings = vm.Settings;
        vm.Settings.ColorThemeFile = string.Empty;
        vm.ApplySettings();
        var view = new Views.Dialogs.SettingsView(vm.Settings, vm.ApplySettings, []);
        var shown = w.ShowSettingsAsync(view);
        await SettleAsync(4);
        view.SelectPage("Display/Color");
        await SettleAsync(4);
        var themes = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view).OfType<ComboBox>()
            .First(c => c.ItemsSource is System.Collections.IEnumerable items && items.Cast<object>().Any(i => i.ToString() == "Dark"));
        themes.SelectedIndex = themes.ItemsSource!.Cast<object>().ToList().FindIndex(i => i.ToString() == "Dark");
        await SettleAsync();
        Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Avalonia.Application.Current!.ActualThemeVariant); // not before Apply
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view).OfType<Button>().First(b => Equals(b.Content, "Apply"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(4);
        Assert.Equal("dark", vm.Settings.ColorThemeFile);
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Avalonia.Application.Current.ActualThemeVariant);
        view.Close();
        await shown;
        vm.Settings.ColorThemeFile = string.Empty;
        vm.ApplySettings();
    }

    [AvaloniaFact]
    public async Task Theme_list_colors_and_inverted_selection()
    {
        var (w, vm) = await _fx.OpenAsync();
        _settings = vm.Settings;
        var dir = _fx.L("themes");
        Directory.CreateDirectory(dir);
        ColorThemes.UserDirectory = dir;
        await File.WriteAllTextAsync(Path.Combine(dir, "liste.json"), """
            { "Name": "Liste", "Base": "Dark",
              "Colors": { "FileList.Text": "#00FF00", "FileList.AlternateBackground": "#202020", "FileList.MarkedText": "#FFFF00",
                          "FileList.CursorBackground": "#800000", "Lister.Background": "#000080" } }
            """);
        vm.Settings.ColorThemeFile = "liste";
        var folder = vm.Left.CurrentPath;
        vm.ApplySettings();
        await SettleAsync();
        Assert.Equal(folder, vm.Left.CurrentPath); // applying settings keeps the panels where they are
        Assert.Equal(Color.Parse("#00FF00"), Brush("FmListTextBrush"));
        Assert.Equal(Color.Parse("#202020"), Brush("FmListBackground2Brush"));
        Assert.Equal(Color.Parse("#FFFF00"), Brush("FmMarkedBrush"));
        Assert.Equal(Color.Parse("#800000"), Brush("DataGridRowSelectedBackgroundBrush"));
        Assert.Equal(Color.Parse("#000080"), Brush("FmListerBackgroundBrush"));

        // every second row is "alt" (background 2)
        var rows = w.LeftPanel.GetVisualDescendants().OfType<DataGridRow>().OrderBy(r => r.Index).ToList();
        Assert.True(rows.Count >= 3);
        Assert.All(rows, r => Assert.Equal(r.Index % 2 == 1, r.Classes.Contains("alt")));

        vm.Settings.InvertedSelection = true;
        vm.ApplySettings();
        await SettleAsync();
        Assert.Contains("invsel", w.LeftPanel.GetVisualDescendants().OfType<DataGrid>().First().Classes);

        // no theme: the system colors, nothing of the theme left
        vm.Settings.ColorThemeFile = string.Empty;
        vm.ApplySettings();
        await SettleAsync();
        Assert.False(App.Current!.Resources.ContainsKey("FmListTextBrush"));
        Assert.False(App.Current.Resources.ContainsKey("DataGridRowSelectedBackgroundBrush"));

        var shots = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(shots))
        {
            var settings = _fx.Track(new Views.Dialogs.SettingsWindow(vm.Settings, () => { }, []));
            settings.Show();
            settings.SelectPage("Display/Color");
            await SettleAsync();
            settings.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "settings-color.png"));
        }
    }
}
