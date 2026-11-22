using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Views;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The main window after the user's template (22 November 2026): the command line's history completion and the drive box.</summary>
public sealed class MainWindowRedesignTests
{
    [AvaloniaFact]
    public void History_matches_put_prefixes_first_and_leave_out_the_text_itself()
    {
        var items = new[] { "git status", "dotnet build", "git log", "echo git", "GIT STATUS", "git" };
        Assert.Equal(["git status", "git log", "echo git"], HistoryAutoComplete.Matches("git", items));
        Assert.Empty(HistoryAutoComplete.Matches("  ", items));
        Assert.Empty(HistoryAutoComplete.Matches("zzz", items));
    }

    [AvaloniaFact]
    public async Task Typing_in_the_command_line_offers_the_recent_commands()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        vm.Settings.CommandLineHistory.InsertRange(0, ["dir /w", "dotnet build", "echo hello"]);
        var box = w.FindControl<TextBox>("CommandLine")!;
        box.Focus();
        Dispatcher();
        box.Text = "do";
        Dispatcher();
        Assert.True(w.CommandHistoryComplete.IsOpen);
        Assert.Equal(["dotnet build"], w.CommandHistoryComplete.Suggestions);
        // ↑ chooses from below, Tab takes it into the box
        Press(w, Key.Up);
        Press(w, Key.Tab);
        Assert.Equal("dotnet build", box.Text);
        Assert.False(w.CommandHistoryComplete.IsOpen);
        // a path is left to the path completion
        box.Text = "dir C:\\";
        Dispatcher();
        Assert.False(w.CommandHistoryComplete.IsOpen);
        box.Text = "ech";
        Dispatcher();
        Assert.True(w.CommandHistoryComplete.IsOpen);
        Press(w, Key.Escape);
        Assert.False(w.CommandHistoryComplete.IsOpen);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Drive_box_lists_drives_and_places_and_changes_the_drive()
    {
        using var fx = new UiFixture();
        var (w, vm) = await fx.OpenAsync();
        vm.Settings.ShowDriveCombo = true;
        vm.ApplySettings();
        var panel = w.GetVisualDescendants().OfType<FilePanelView>().First(p => ReferenceEquals(p.DataContext, vm.Left));
        panel.OpenDriveBox();
        Dispatcher();
        Assert.True(panel.IsDriveBoxOpen);
        var entries = panel.DriveBoxEntries;
        Assert.Contains(DriveItem.ComputerEntry, entries);
        Assert.Contains(entries, d => d.Kind == DriveKind.Trash);
        Assert.All(vm.Left.Drives, d => Assert.Contains(d, entries));
        panel.CloseDriveBox();
        Assert.False(panel.IsDriveBoxOpen);
        // two tabs on the left: the strip shows them, the panel's corners are cut round (FM_SCREENSHOT_DIR: a picture)
        vm.Settings.AlwaysShowTabs = true;
        vm.ApplySettings();
        await vm.ExecuteAsync("nc_NewTab");
        await SettleAsync(6);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "main-window-redesign.png"));
        }
        Assert.IsType<RectangleGeometry>(panel.FindControl<DockPanel>("FrameContent")!.Clip);
        // Alt+F1 opens it on the left panel
        Press(w, Key.F1, RawInputModifiers.Alt);
        await SettleAsync(4);
        Assert.True(panel.IsDriveBoxOpen);
        panel.CloseDriveBox();
        vm.Dispose();
    }

    private static void Dispatcher() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
