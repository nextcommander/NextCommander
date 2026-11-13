using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Archive;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The Configuration window (changed marks, Reset page, dependent options, search) and further options.</summary>
[Collection("Appearance")]
public sealed class ConfigurationWindowTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        CopyDialog.DefaultPolicy = ConflictPolicy.Ask;
        PanelViewModel.InBackground = false;
        _fx.Dispose();
    }

    private static CheckBox Box(Window window, string text) =>
        window.GetLogicalDescendants().OfType<CheckBox>().First(c => Equals(c.Content, text));

    [AvaloniaFact]
    public async Task Changed_marks_reset_page_dependent_options_and_search()
    {
        var settings = new AppSettings();
        var window = _fx.Track(new SettingsWindow(settings, () => { }, []));
        window.Show();

        // every page with a "/" in its name is in the tree (Copy and delete, Edit and view)
        window.SelectPage("Operation/Copy and delete");
        await SettleAsync(3);
        Assert.Equal("Copy and delete", window.PageTitle);
        Assert.Contains("When a target file exists", window.VisibleLabels);
        Assert.Empty(window.ChangedLabels);

        // a change is marked and counted; Reset page brings the defaults back (Apply writes them)
        Box(window, "Verify after copying").IsChecked = true;
        await SettleAsync();
        Assert.Equal(["Verify after copying"], window.ChangedLabels);
        Assert.True(window.IsDirty);
        window.ResetPage();
        await SettleAsync();
        Assert.Empty(window.ChangedLabels);
        Assert.False(Box(window, "Verify after copying").IsChecked);

        // indented options are greyed out while the option above them is off
        window.SelectPage("Refresh");
        await SettleAsync(3);
        var auto = Box(window, "Refresh file lists automatically");
        var network = Box(window, "On network drives too");
        var background = Box(window, "Not while the program is in the background");
        Assert.True(auto.IsChecked);
        Assert.True(network.IsEffectivelyEnabled);
        auto.IsChecked = false;
        await SettleAsync();
        Assert.False(network.IsEffectivelyEnabled);
        Assert.False(background.IsEffectivelyEnabled);

        // a search result's title opens its page
        window.Search("solid");
        await SettleAsync(2);
        Assert.Equal(["Create solid archives"], window.VisibleLabels);
        var legend = window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Text?.Contains("7-Zip compression", StringComparison.Ordinal) == true);
        legend.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(legend, new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true), window,
            default, 0, new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed),
            Avalonia.Input.KeyModifiers.None));
        await SettleAsync(2);
        Assert.Equal("7-Zip", window.PageTitle);

        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Box(window, "Create solid archives").IsChecked = false;
            await SettleAsync(3);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "settings-7zip.png"));
            window.SelectPage("Refresh");
            await SettleAsync(3);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "settings-refresh.png"));
        }
    }

    [AvaloniaFact]
    public void SevenZip_switches_follow_the_options()
    {
        var args = SevenZipCli.Arguments(CompressionLevel.Optimal, "pw", new SevenZipOptions(9, "PPMd", Solid: false, Threads: 4, EncryptNames: false, "-mqs=on \"-w=C:\\my temp\""));
        Assert.Contains("-mx=9", args);
        Assert.Contains("-m0=PPMd", args);
        Assert.Contains("-ms=off", args);
        Assert.Contains("-mmt=4", args);
        Assert.Contains("-ppw", args);
        Assert.DoesNotContain("-mhe=on", args);
        Assert.Equal(["-mqs=on", "-w=C:\\my temp"], args.TakeLast(2));

        var defaults = SevenZipCli.Arguments(CompressionLevel.Fastest, "pw", new SevenZipOptions());
        Assert.Contains("-mx=1", defaults); // the level of the pack dialog
        Assert.Contains("-ms=on", defaults);
        Assert.Contains("-mhe=on", defaults);
        Assert.DoesNotContain(defaults, a => a.StartsWith("-m0=", StringComparison.Ordinal) || a.StartsWith("-mmt", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Log_rotates_daily_and_by_size_keeping_the_newest()
    {
        var dir = _fx.L("logs");
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "ops.log");
        var settings = new AppSettings { LogRotation = LogRotation.Daily, LogKeepCopies = 2 };

        File.WriteAllText(log, "today");
        OperationLog.Rotate(settings, log, DateTime.Now);
        Assert.True(File.Exists(log)); // written today: kept

        foreach (var day in new[] { 3, 2, 1 })
        {
            File.WriteAllText(log, "old");
            File.SetLastWriteTime(log, DateTime.Now.AddDays(-day));
            OperationLog.Rotate(settings, log, DateTime.Now);
            Assert.False(File.Exists(log));
        }
        var kept = Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, kept.Count); // the oldest one went
        Assert.Equal("ops-" + DateTime.Now.AddDays(-1).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".log", kept[^1]);

        settings.LogRotation = LogRotation.BySize;
        settings.LogMaxSizeKb = 1;
        File.WriteAllText(log, new string('x', 900));
        OperationLog.Rotate(settings, log, DateTime.Now);
        Assert.True(File.Exists(log)); // below the limit
        File.WriteAllText(log, new string('x', 2000));
        OperationLog.Rotate(settings, log, DateTime.Now);
        Assert.False(File.Exists(log));
    }

    [AvaloniaFact]
    public async Task Archive_date_drag_and_drop_and_the_copy_dialog_default()
    {
        var (_, vm) = await _fx.OpenAsync();

        // the archive gets the date of its newest file
        var newest = new DateTime(2021, 3, 4, 5, 6, 8);
        File.SetLastWriteTime(_fx.L("a.txt"), newest);
        File.SetLastWriteTime(_fx.L("b.log"), newest.AddDays(-10));
        await vm.Left.RefreshAsync();
        var sources = vm.Left.Items.Where(i => i.FullName is "a.txt" or "b.log").Select(i => i.Entry!).ToList();
        var zip = _fx.R("dated.zip");
        await new PackJob(vm.Tools.Registry, sources, VfsPath.Local(zip), new PackOptions { SetArchiveDate = true }).RunAsync();
        Assert.Equal(newest, File.GetLastWriteTime(zip));

        // drop without the Copy dialog: copied at once
        vm.Settings.ConfirmDragDrop = false;
        vm.Settings.DefaultConflictPolicy = ConflictPolicy.Overwrite;
        vm.ApplySettings();
        Assert.Equal(ConflictPolicy.Overwrite, CopyDialog.DefaultPolicy);
        var a = vm.Left.Items.Single(i => i.FullName == "a.txt").Entry!;
        await vm.DropAsync(new DropRequest([a], VfsPath.Local(_fx.L("sub")), Move: false, vm.Left));
        await WaitUntilAsync(() => File.Exists(_fx.L("sub", "a.txt")));
        Assert.Null(_fx.Dialogs.LastCopyTarget); // no dialog

        // the copy dialog opens with the chosen answer
        var dialog = _fx.Track(new CopyDialog(false, "Copy 1 items:", _fx.R(), false));
        Assert.Contains(dialog.GetLogicalDescendants().OfType<ComboBox>(), c => Equals(c.SelectedItem, "2. Overwrite all"));

        // no refresh in the background: the change waits for the focus
        vm.Settings.NoRefreshInBackground = true;
        vm.ApplySettings();
        vm.OnWindowDeactivated();
        File.WriteAllText(_fx.L("later.txt"), "x");
        await WaitUntilAsync(() => vm.Left.RefreshPending, 5000);
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == "later.txt");
        await vm.OnWindowActivatedAsync();
        Assert.Contains(vm.Left.Items, i => i.FullName == "later.txt");
    }
}
