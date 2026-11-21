using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using NextCommander.App.Services;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>System colors, the dark theme file (VS Code colors) and the colored menu icons.</summary>
[Collection("Appearance")]
public sealed class ThemeTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        App.ApplyColorTheme(null);
        _fx.Dispose();
    }

    [AvaloniaFact]
    public async Task Every_menu_command_has_an_icon()
    {
        var (w, _) = await _fx.OpenAsync();
        var items = w.GetLogicalDescendants().OfType<MenuItem>().Where(m => m.CommandParameter is string).ToList();
        Assert.True(items.Count > 100);
        Assert.All(items.Where(m => !MenuIcons.WithoutIcon.Contains((string)m.CommandParameter!)),
            m => Assert.True(m.Icon is Control, $"no icon: {m.CommandParameter}"));
        Assert.All(items.Where(m => MenuIcons.WithoutIcon.Contains((string)m.CommandParameter!)), m => Assert.Null(m.Icon));
        foreach (var command in MenuIcons.Commands)
        {
            Assert.NotNull(MenuIcons.Create(command)); // every glyph and badge resource resolves
        }
        Assert.NotNull(MenuIcons.Create("nc_PanelsSplitPercent 50"));
        Assert.Null(MenuIcons.Create("nc_NoSuchCommand"));
    }

    [AvaloniaFact]
    public void Dark_syntax_highlighting_uses_vs_code_palette()
    {
        var light = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinition("C#");
        var dark = Views.Lister.ThemeAwareColors.DarkVersion(light);
        Assert.NotNull(dark);
        Assert.NotSame(light, dark);
        Assert.Equal("C#", dark!.Name);
        Assert.Equal(Color.Parse("#6A9955"), dark.GetNamedColor("Comment").Foreground.GetColor(null));
        Assert.NotEqual(Color.Parse("#6A9955"), light.GetNamedColor("Comment").Foreground.GetColor(null)); // shared light definition untouched
        Assert.Same(dark, Views.Lister.ThemeAwareColors.DarkVersion(light));
        Assert.Equal(Color.Parse("#569CD6"), Views.Lister.ThemeAwareColors.VsCodeColor("Keywords"));
        Assert.Equal(Color.Parse("#CE9178"), Views.Lister.ThemeAwareColors.VsCodeColor("String"));
    }

    [AvaloniaFact]
    public async Task Dark_theme_uses_vs_code_colors()
    {
        var (w, _) = await _fx.OpenAsync();
        App.ApplyColorTheme("dark");
        await SettleAsync();
        Color Resource(string key) =>
            w.TryFindResource(key, ThemeVariant.Dark, out var value) ? value switch
            {
                ISolidColorBrush b => b.Color,
                Color c => c,
                _ => default,
            } : default;
        Assert.Equal(Color.Parse("#1E1E1E"), Resource("SystemAltHighColor"));
        Assert.Equal(Color.Parse("#007ACC"), Resource("SystemAccentColor"));
        Assert.Equal(Color.Parse("#1E1E1E"), Resource("FmMenuBarBrush"));
        Assert.Equal(Color.Parse("#094771"), Resource("DataGridRowSelectedBackgroundBrush"));
        Assert.Equal(ThemeVariant.Dark, w.ActualThemeVariant);

        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(w.CaptureRenderedFrame());
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }
        Directory.CreateDirectory(dir);
        await _fx_NavigateBoth(w);
        w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "theme-dark.png"));
        foreach (var (menu, file) in new[] { ("_Files", "menu-files"), ("_Mark", "menu-mark"), ("_Commands", "menu-commands"), ("_Net", "menu-net"), ("_Show", "menu-show"), ("C_onfiguration", "menu-config") })
        {
            await CaptureMenuAsync(w, menu, Path.Combine(dir, file + "-dark.png"));
        }
        App.ApplyColorTheme(null);
        await SettleAsync();
        w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "theme-light.png"));
        await CaptureMenuAsync(w, "_Files", Path.Combine(dir, "menu-files-light.png"));
    }

    private async Task _fx_NavigateBoth(Window w)
    {
        var vm = (ViewModels.MainWindowViewModel)w.DataContext!;
        await vm.Right.NavigateAsync(VfsPath.Local(_fx.L()));
        await SettleAsync();
    }

    private static async Task CaptureMenuAsync(Window w, string header, string file)
    {
        var item = w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Header as string == header);
        item.IsSubMenuOpen = true;
        await SettleAsync();
        var popup = item.GetLogicalDescendants().OfType<Popup>().FirstOrDefault()
            ?? item.GetVisualDescendantsSafe().OfType<Popup>().First();
        var root = TopLevel.GetTopLevel(popup.Child)!;
        root.CaptureRenderedFrame()!.SavePng(file);
        item.IsSubMenuOpen = false;
        await SettleAsync();
    }

    [AvaloniaFact]
    public async Task System_colors_by_default_and_dark_theme_file_toggle()
    {
        var (w, vm) = await _fx.OpenAsync();
        try
        {
            vm.Settings.ColorThemeFile = string.Empty;
            App.ApplyAppearance(vm.Settings);
            await SettleAsync();
            Assert.Equal(ThemeVariant.Light, w.ActualThemeVariant);
            Assert.Null(ColorThemes.Current);
            Assert.False(App.Current!.Resources.ContainsKey("FmMenuBarBrush")); // no theme colors at the root

            await vm.ExecuteAsync("nc_SwitchDarkNormal");
            await SettleAsync();
            Assert.Equal(ColorTheme.DarkThemeId, vm.Settings.ColorThemeFile);
            Assert.True(vm.IsDarkTheme);
            Assert.Equal(ThemeVariant.Dark, w.ActualThemeVariant);
            Assert.Equal(Color.Parse("#1E1E1E"), ((ISolidColorBrush)App.Current.Resources["FmMenuBarBrush"]!).Color);

            await vm.ExecuteAsync("nc_SwitchDarkmode");
            Assert.Equal(string.Empty, vm.Settings.ColorThemeFile);
            Assert.False(vm.IsDarkTheme);
            Assert.False(App.Current.Resources.ContainsKey("FmMenuBarBrush"));

            await vm.ExecuteAsync("nc_SetColorScheme dark");
            Assert.True(vm.IsDarkTheme);
            await vm.ExecuteAsync("nc_SetColorScheme yok-boyle-bir-tema"); // unknown: unchanged
            Assert.True(vm.IsDarkTheme);
            await vm.ExecuteAsync("nc_SetColorScheme");
            Assert.False(vm.IsDarkTheme);
        }
        finally
        {
            vm.Settings.ColorThemeFile = string.Empty;
            App.ApplyAppearance(vm.Settings);
        }
    }

    [AvaloniaFact]
    public async Task Follow_the_system_is_the_default_and_takes_the_systems_mode()
    {
        var (w, vm) = await _fx.OpenAsync();
        Assert.Equal(ColorTheme.SystemThemeId, new AppSettings().ColorThemeFile);
        try
        {
            ColorThemes.SystemIsDarkOverride = true; // the operating system in dark mode
            await vm.ExecuteAsync("nc_SetColorScheme system");
            await SettleAsync();
            Assert.Equal(ColorTheme.SystemThemeId, vm.Settings.ColorThemeFile);
            Assert.True(ColorThemes.FollowsSystem);
            Assert.True(vm.IsDarkTheme);
            Assert.Equal(ThemeVariant.Dark, w.ActualThemeVariant);
            Assert.Equal(ColorTheme.DarkThemeId, ColorThemes.Current?.Id);

            ColorThemes.SystemIsDarkOverride = false; // light mode: the system colors
            App.ApplyAppearance(vm.Settings);
            await SettleAsync();
            Assert.False(vm.IsDarkTheme);
            Assert.Null(ColorThemes.Current);
            Assert.Equal(ThemeVariant.Light, w.ActualThemeVariant);
        }
        finally
        {
            ColorThemes.SystemIsDarkOverride = null;
            vm.Settings.ColorThemeFile = string.Empty;
            App.ApplyAppearance(vm.Settings);
        }
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<Visual> GetVisualDescendantsSafe(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v);
}
