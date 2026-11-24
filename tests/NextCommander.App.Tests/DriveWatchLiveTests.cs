using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The real drive watch: a drive letter made with subst appears in the panels' drive lists and goes away again (Windows).</summary>
public sealed class DriveWatchLiveTests
{
    private static void Subst(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo("subst", args) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
    }

    [AvaloniaFact]
    public async Task Subst_drive_appears_and_disappears_in_the_drive_lists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "QRSTUVWXYZ".First(c => !used.Contains(c));
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync(new ComputerFileSystemProvider(PlatformServiceFactory.Create()));
        vm.Settings.ShowDriveButtons = true;
        vm.Settings.ShowDriveCombo = true;
        vm.ApplySettings();
        vm.StartDriveWatch();
        // the right panel shows the drive overview: it lists the new drive too
        await vm.Right.ChangeDriveAsync(DriveItem.ComputerEntry);
        Assert.Equal(ComputerFileSystemProvider.Root, vm.Right.CurrentPath);
        bool Listed() => vm.Left.Drives.Any(d => char.ToUpperInvariant(d.Name[0]) == letter)
            && vm.Right.Drives.Any(d => char.ToUpperInvariant(d.Name[0]) == letter);
        bool InOverview() => vm.Right.Items.Any(i => i.FullName.EndsWith($"({letter}:)", StringComparison.OrdinalIgnoreCase));
        int Buttons() => w.GetVisualDescendants().OfType<ItemsControl>().Where(c => c.Name is "SharedDriveBar" or "DriveButtons" && c.IsEffectivelyVisible)
            .SelectMany(c => c.GetVisualDescendants().OfType<Button>()).Count(b => b.Tag is DriveItem d && char.ToUpperInvariant(d.Name[0]) == letter);
        try
        {
            Subst($"{letter}:", fx.Root);
            await WaitUntilAsync(Listed, 10000);
            Assert.True(Listed(), "the new drive is not in the drive lists");
            await WaitUntilAsync(() => Buttons() > 0, 3000);
            Assert.True(Buttons() > 0, "the new drive has no drive button");
            // the drive box's list (opened as with Alt+F1) lists it too
            var panels = w.GetVisualDescendants().OfType<Views.FilePanelView>().Where(p => p.IsEffectivelyVisible).ToList(); // not the hidden second panels
            Assert.NotEmpty(panels);
            foreach (var panel in panels)
            {
                panel.OpenDriveBox();
                Assert.True(panel.IsDriveBoxOpen);
                Assert.Contains(panel.DriveBoxEntries, d => char.ToUpperInvariant(d.Name[0]) == letter);
                panel.CloseDriveBox();
            }
            await WaitUntilAsync(InOverview, 5000);
            Assert.True(InOverview(), "the new drive is not in the Computer view");
        }
        finally
        {
            Subst($"{letter}:", "/D");
        }
        await WaitUntilAsync(() => !Listed() && !InOverview(), 10000);
        Assert.False(Listed(), "the removed drive is still in the drive lists");
        Assert.False(InOverview(), "the removed drive is still in the Computer view");
        vm.Dispose();
    }
}
