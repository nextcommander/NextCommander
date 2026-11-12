using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The right-click menu of the folder tabs shows the command icons.</summary>
public sealed class TabMenuIconTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_tab_menu_has_icons()
    {
        var (w, vm) = await _fx.OpenAsync();
        var strip = w.GetVisualDescendants().OfType<Control>().First(c => c.ContextMenu?.Items.OfType<MenuItem>().Any(m => m.Tag as string == "nc_CloseCurrentTab") == true);
        strip.ContextMenu!.Open(strip);
        await SettleAsync(4);
        var items = strip.ContextMenu.Items.OfType<MenuItem>().ToList();
        Assert.All(items, i => Assert.NotNull(i.Icon)); // every item of this menu has a command with an icon
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "tab-menu-icons.png"));
        }
        strip.ContextMenu.Close();
        await SettleAsync();

        // Configuration > Icons: no icons in the menus
        vm.Settings.ShowMenuIcons = false;
        strip.ContextMenu.Open(strip);
        await SettleAsync(4);
        Assert.All(items, i => Assert.Null(i.Icon));
        strip.ContextMenu.Close();
    }
}
