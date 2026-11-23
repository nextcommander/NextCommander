using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The internal terminal dragged by its title bar into the place of a file panel, and back below the panels.</summary>
public sealed class TerminalDockTests
{
    [AvaloniaFact]
    public async Task The_terminal_goes_into_a_panel_by_dragging_and_back_below()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        vm.Settings.TerminalEmulation = false;
        vm.Settings.TerminalTabs = [];
        w.ToggleTerminal(); // below the panels
        await SettleAsync(4);
        var terminal = w.Terminal!;
        var host = w.FindControl<Border>("TerminalHost")!;
        Assert.True(host.IsVisible);
        var right = w.FindControl<PanelSideView>("RightSideView")!;

        // the title bar dragged onto the right panel
        var title = terminal.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "TERMINAL");
        var from = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), w)!.Value;
        var to = right.TranslatePoint(new Point(right.Bounds.Width / 2, right.Bounds.Height / 2), w)!.Value;
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(from + new Point(30, -10), RawInputModifiers.LeftMouseButton);
        w.MouseMove(to, RawInputModifiers.LeftMouseButton);
        await SettleAsync(2);
        Assert.Contains("droptarget", right.Classes); // marked while dragging
        w.MouseUp(to, MouseButton.Left);
        await SettleAsync(4);
        Assert.Same(right, w.TerminalSide);
        Assert.Same(terminal, right.Overlay);
        Assert.False(host.IsVisible);
        Assert.DoesNotContain("droptarget", right.Classes);
        Assert.Same(vm.LeftSide, vm.ActiveSide); // the panel still shown takes the keys
        Assert.False(vm.IsTerminalOpen); // the command line is shown again
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "terminal-in-panel.png"));
        }

        // ⤓: back below the panels, the panel comes back
        var below = terminal.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Tag, "dockbelow"));
        Assert.True(below.IsVisible);
        below.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(4);
        Assert.Null(w.TerminalSide);
        Assert.Null(right.Overlay);
        Assert.True(host.IsVisible);
        Assert.Same(terminal, host.Child);

        // in the left panel's place, then closed: the panel is back, the terminal hidden
        w.DockTerminalIn(w.FindControl<PanelSideView>("LeftSideView")!);
        await SettleAsync(2);
        Assert.Same(vm.RightSide, vm.ActiveSide);
        var close = terminal.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "✕") && b.IsEffectivelyVisible);
        close.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(4);
        Assert.Null(w.TerminalSide);
        Assert.Null(w.FindControl<PanelSideView>("LeftSideView")!.Overlay);
        Assert.False(host.IsVisible);
        vm.Dispose();
    }
}
