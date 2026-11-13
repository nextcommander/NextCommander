using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The menu bar and its dropdowns follow a theme switch.</summary>
[Collection("Appearance")]
public sealed class MenuThemeTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        App.ApplyColorTheme(null);
        _fx.Dispose();
    }

    private static (Color Bar, Color TopText, Color ItemText, Color DropBackground) Colors(Window w)
    {
        var menu = w.GetLogicalDescendants().OfType<Menu>().First(m => m.Name == "MainMenu");
        var files = menu.Items.OfType<MenuItem>().First();
        files.IsSubMenuOpen = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var item = files.Items.OfType<MenuItem>().First(m => m.IsEffectivelyEnabled); // not one dimmed for want of a selection
        var text = item.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
        var presenter = item.GetVisualAncestors().OfType<Border>().LastOrDefault();
        var result = (
            ((ISolidColorBrush?)menu.Background)?.Color ?? default,
            ((ISolidColorBrush)files.Foreground!).Color,
            ((ISolidColorBrush)text.Foreground!).Color,
            (presenter?.Background as ISolidColorBrush)?.Color ?? default);
        files.IsSubMenuOpen = false;
        return result;
    }

    [AvaloniaFact]
    public async Task Menu_colors_follow_the_theme()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.ColorThemeFile = "dark";
        vm.ApplySettings();
        await SettleAsync(3);
        var dark = Colors(w);
        vm.Settings.ColorThemeFile = string.Empty;
        vm.ApplySettings();
        await SettleAsync(3);
        var light = Colors(w);
        Assert.NotEqual(dark.TopText, light.TopText);
        Assert.NotEqual(dark.ItemText, light.ItemText);
        Assert.NotEqual(dark.DropBackground, light.DropBackground);
        // readable: the dropdown text differs clearly from its background
        Assert.True(Math.Abs(light.ItemText.R - light.DropBackground.R) > 100, $"{light.ItemText} on {light.DropBackground}");
        Assert.True(Math.Abs(dark.ItemText.R - dark.DropBackground.R) > 100, $"{dark.ItemText} on {dark.DropBackground}");

        // the dropdowns take the theme's colors (Menu.* keys, or the window / control colors when a theme leaves them out)
        vm.Settings.ColorThemeFile = "ubuntu-dark";
        vm.ApplySettings();
        await SettleAsync(3);
        var files0 = w.GetLogicalDescendants().OfType<Menu>().First(m => m.Name == "MainMenu").Items.OfType<MenuItem>().First();
        files0.IsSubMenuOpen = true;
        await SettleAsync(3);
        var drop = files0.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First().Child!;
        Assert.Contains(drop.GetSelfAndVisualDescendants().OfType<Border>(), b => b.Background is ISolidColorBrush { Color: var c } && c == Color.Parse("#383838"));
        var firstItem = files0.Items.OfType<MenuItem>().First();
        Assert.Equal(Color.Parse("#F7F7F7"), ((ISolidColorBrush)firstItem.Foreground!).Color);
        files0.IsSubMenuOpen = false;

        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            var menu = w.GetLogicalDescendants().OfType<Menu>().First(m => m.Name == "MainMenu");
            var files = menu.Items.OfType<MenuItem>().First();
            foreach (var theme in new[] { "dark", "ubuntu", "macos-dark", "macos", "" })
            {
                vm.Settings.ColorThemeFile = theme;
                vm.ApplySettings();
                await SettleAsync(3);
                files.IsSubMenuOpen = true;
                await SettleAsync(3);
                w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, $"menutheme-{(theme.Length == 0 ? "system" : theme)}.png"));
                var popup = files.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First();
                TopLevel.GetTopLevel(popup.Child)!.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, $"menutheme-{(theme.Length == 0 ? "system" : theme)}-drop.png"));
                files.IsSubMenuOpen = false;
            }
        }
    }

    private static Color Brush(Window w, string key) =>
        w.TryFindResource(key, w.ActualThemeVariant, out var value) && value is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException(key);

    [AvaloniaFact]
    public async Task Open_top_menu_looks_like_the_selected_tab()
    {
        var (w, vm) = await _fx.OpenAsync();
        foreach (var theme in new[] { string.Empty, "dark", "ubuntu-dark" })
        {
            vm.Settings.ColorThemeFile = theme;
            vm.ApplySettings();
            await SettleAsync(3);
            var files = w.GetLogicalDescendants().OfType<Menu>().First(m => m.Name == "MainMenu").Items.OfType<MenuItem>().First();
            var root = files.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
            if (theme.Length == 0) // before any menu was opened: no line
            {
                Assert.Equal(Avalonia.Media.Colors.Transparent, (root.BorderBrush as ISolidColorBrush)?.Color ?? Avalonia.Media.Colors.Transparent);
            }
            files.IsSubMenuOpen = true;
            await SettleAsync(3);
            var tab = Brush(w, "FmTabActiveBrush");
            var accent = Brush(w, "FmActiveAccentBrush");
            Assert.Equal(tab, ((ISolidColorBrush)root.Background!).Color);
            Assert.Equal(accent, ((ISolidColorBrush)root.BorderBrush!).Color);
            Assert.Equal(2, root.BorderThickness.Bottom); // the line is drawn
            // the items inside the same way: the tab's background and an accent line on the left
            var inner = files.Items.OfType<MenuItem>().First(m => m.IsEffectivelyEnabled);
            inner.IsSelected = true;
            await SettleAsync(2);
            var innerRoot = inner.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");
            Assert.Equal(tab, ((ISolidColorBrush)innerRoot.Background!).Color);
            Assert.Equal(accent, ((ISolidColorBrush)innerRoot.BorderBrush!).Color);
            Assert.Equal(2, innerRoot.BorderThickness.Left);
            if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
            {
                w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, $"menubar-open-{(theme.Length == 0 ? "light" : theme)}.png"));
            }
            files.IsSubMenuOpen = false;
            await SettleAsync();
        }
    }
}
