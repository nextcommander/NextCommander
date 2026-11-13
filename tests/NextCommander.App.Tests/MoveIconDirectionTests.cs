using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Controls;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The Move button of the button bars points from the active panel to the other one.</summary>
public sealed class MoveIconDirectionTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Move_points_to_the_other_panel()
    {
        var (w, vm) = await _fx.OpenAsync();
        await SettleAsync();
        // the default vertical bar has Move
        var bar = w.GetVisualDescendants().OfType<ButtonBarControl>().First(b => b.GetVisualDescendants().OfType<Button>()
            .Any(x => (x.Tag as ButtonDefinition)?.Command == "nc_RenMov"));
        Button Move() => bar.GetVisualDescendants().OfType<Button>().First(x => (x.Tag as ButtonDefinition)?.Command == "nc_RenMov");

        vm.ActivateSide(vm.LeftSide);
        await SettleAsync();
        Assert.Equal("nc_RenMov", ButtonBarControl.DirectionalIcon("nc_RenMov", vm)); // → to the right panel
        var right = Move().Content;

        vm.ActivateSide(vm.RightSide);
        await SettleAsync();
        Assert.Equal("nc_RenMov.left", ButtonBarControl.DirectionalIcon("nc_RenMov", vm));
        Assert.NotSame(right, Move().Content); // drawn again
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "move-left.png"));
        }

        // panels above each other: down / up
        vm.VerticalPanels = true;
        await SettleAsync();
        Assert.Equal("nc_RenMov.up", ButtonBarControl.DirectionalIcon("nc_RenMov", vm));
        vm.ActivateSide(vm.LeftSide);
        Assert.Equal("nc_RenMov.down", ButtonBarControl.DirectionalIcon("nc_RenMov", vm));

        // other commands keep their icon
        Assert.Equal("nc_Copy", ButtonBarControl.DirectionalIcon("nc_Copy", vm));
    }
}
