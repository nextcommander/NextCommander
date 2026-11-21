using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Controls;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The right-click menus of the button bars: edit the bar, reset it to the default buttons.</summary>
public sealed class ButtonBarResetTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_right_click_on_the_empty_part_of_the_bar_opens_its_menu()
    {
        var (w, _) = await _fx.OpenAsync();
        var bar = w.GetVisualDescendants().OfType<ButtonBarControl>().Single(b => !b.IsVertical);
        await SettleAsync();

        // the empty part after the last button takes the pointer (it let it through without a background)
        var last = bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Max(b => b.TranslatePoint(new Avalonia.Point(b.Bounds.Width, 0), bar)!.Value.X);
        var empty = new Avalonia.Point((last + bar.Bounds.Width) / 2, bar.Bounds.Height / 2);
        Assert.True(empty.X > last + 4);
        Assert.Same(bar, bar.InputHitTest(empty) is Avalonia.Visual hit ? hit.FindAncestorOfType<ButtonBarControl>(includeSelf: true) : null);

        // a right click there: the bar's menu
        w.MouseDown(bar.TranslatePoint(empty, w)!.Value, Avalonia.Input.MouseButton.Right);
        w.MouseUp(bar.TranslatePoint(empty, w)!.Value, Avalonia.Input.MouseButton.Right);
        await SettleAsync(4);
        Assert.True(bar.ContextMenu!.IsOpen);
        bar.ContextMenu.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_bar_menus_offer_edit_and_reset_with_icons(bool vertical)
    {
        var (w, vm) = await _fx.OpenAsync();
        var bar = w.GetVisualDescendants().OfType<ButtonBarControl>().Single(b => b.IsVertical == vertical);
        var reset = vertical ? "nc_ResetVertButtonBar" : "nc_ResetButtonBar";
        var edit = vertical ? "nc_VertButtonConfig" : "nc_ButtonConfig";

        // the menu of the bar and the menu of a button both have the items, with icons
        var button = bar.GetVisualDescendants().OfType<Button>().First(b => b.ContextMenu is not null);
        var buttonMenu = button.ContextMenu!;
        foreach (var (menu, owner) in new (ContextMenu, Control)[] { (bar.ContextMenu!, bar), (buttonMenu, button) })
        {
            menu.Open(owner);
            await SettleAsync(4);
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Contains(items, i => i.Tag as string == reset && i.Icon is not null);
            Assert.Contains(items, i => i.Tag as string == edit && i.Icon is not null);
            Assert.All(items.Where(i => i.Tag as string != "history"), i => Assert.NotNull(i.Icon));
            if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, $"bar-menu-{(vertical ? "vertical" : "top")}-{(menu == buttonMenu ? "button" : "bar")}.png"));
            }
            menu.Close();
            await SettleAsync();
        }

        // own buttons and size, then reset: the default buttons come back
        var buttons = vertical ? vm.Settings.VerticalButtonBar : vm.Settings.ButtonBar;
        buttons.Clear();
        buttons.Add(new ButtonDefinition { Command = "nc_About" });
        if (vertical)
        {
            vm.Settings.VerticalButtonBarIconSize = 32;
        }
        else
        {
            vm.Settings.ButtonBarIconSize = 32;
        }
        _fx.Dialogs.ConfirmAnswer = false;
        await vm.ExecuteAsync(reset);
        Assert.Single(vertical ? vm.Settings.VerticalButtonBar : vm.Settings.ButtonBar); // "no": nothing changes

        _fx.Dialogs.ConfirmAnswer = true;
        await vm.ExecuteAsync(reset);
        await SettleAsync();
        var defaults = vertical ? ButtonDefinition.VerticalDefaults() : ButtonDefinition.Defaults();
        var now = vertical ? vm.Settings.VerticalButtonBar : vm.Settings.ButtonBar;
        Assert.Equal(defaults.Select(b => b.Command), now.Select(b => b.Command));
        Assert.Equal(16, vertical ? vm.Settings.VerticalButtonBarIconSize : vm.Settings.ButtonBarIconSize);
        Assert.Equal(16, bar.IconSize);
    }
}
