using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The Quick View (Ctrl+Q) in place of the other panel, or below / right of the file list with both panels kept.</summary>
public sealed class QuickViewPlacementTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_Quick_View_goes_below_or_right_of_the_file_list_and_follows_the_active_side()
    {
        var (w, vm) = await _fx.OpenAsync();
        var left = w.FindControl<PanelSideView>("LeftSideView")!;
        var right = w.FindControl<PanelSideView>("RightSideView")!;
        var host = w.FindControl<Border>("QuickViewHost")!;
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "a.txt");

        // as before: in place of the other panel
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(4);
        Assert.True(host.IsVisible);
        Assert.Null(left.Preview);
        Assert.Same(vm.RightSide, vm.QuickViewSide);
        Assert.Equal("a.txt", vm.QuickViewEntry!.Name);

        // ⇄: below the file list of the active side; the other panel shows again
        var place = w.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Tag, "quickviewplace"));
        place.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(4);
        Assert.Equal(QuickViewPlacement.Below, vm.Settings.QuickViewPlacement);
        Assert.False(host.IsVisible);
        Assert.NotNull(left.Preview);
        Assert.False(left.PreviewIsRight);
        Assert.True(right.Panel.IsEffectivelyVisible);
        Assert.Equal("a.txt", vm.QuickViewEntry!.Name);
        Assert.True(left.Panel.Bounds.Height < left.Bounds.Height * 0.75); // the list shares the side
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "quickview-below.png"));
        }

        // the other side can become active now: the Quick View goes with it and shows its file
        vm.ActivateSide(vm.RightSide);
        await SettleAsync(4);
        Assert.Null(left.Preview);
        Assert.NotNull(right.Preview);
        Assert.Same(vm.RightSide, vm.QuickViewSide);

        // ⇄ again: right of the file list
        vm.ActivateSide(vm.LeftSide);
        place.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(4);
        Assert.Equal(QuickViewPlacement.Right, vm.Settings.QuickViewPlacement);
        w.UpdateLayout();
        Assert.True(left.PreviewIsRight);
        Assert.True(left.Panel.Bounds.Width < left.Bounds.Width * 0.75);

        // closed: the side is the file list alone; opened again it comes back at its place
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(4);
        Assert.Null(left.Preview);
        Assert.Null(right.Preview);
        Assert.False(host.IsVisible);
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(4);
        Assert.NotNull(left.Preview);
        Assert.True(left.PreviewIsRight);

        // ⇄ once more: back in place of the other panel
        place.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(4);
        Assert.Equal(QuickViewPlacement.OtherPanel, vm.Settings.QuickViewPlacement);
        Assert.Null(left.Preview);
        Assert.True(host.IsVisible);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Moving_the_Quick_View_again_and_again_and_resizing_the_window_lays_out()
    {
        // the user's report (25 November 2026): after a few moves, making the window small threw in the grid's layout
        var (w, vm) = await _fx.OpenAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "a.txt");
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(2);
        var sizes = new[] { (1100d, 600d), (400d, 260d), (1, 1), (900d, 500d), (640d, 400d) };
        for (var round = 0; round < 9; round++)
        {
            vm.SetQuickViewPlacement((QuickViewPlacement)(round % 3));
            if (round % 4 == 3)
            {
                vm.ActivateSide(round % 8 == 3 ? vm.RightSide : vm.LeftSide);
            }
            await SettleAsync(1);
            foreach (var (width, height) in sizes)
            {
                w.Width = width;
                w.Height = height;
                w.UpdateLayout();
            }
        }
        await vm.ExecuteAsync("nc_QuickView"); // closed: the sides are their file lists again
        await SettleAsync(2);
        w.Width = 300;
        w.UpdateLayout();
        w.Width = 1100;
        w.UpdateLayout();
        Assert.Null(w.FindControl<PanelSideView>("LeftSideView")!.Preview);
        vm.Dispose();
    }
}