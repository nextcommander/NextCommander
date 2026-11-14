using Avalonia.Headless.XUnit;
using NextCommander.Core.Config;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Operation. Shares the global descript.ion encoding: Appearance collection.</summary>
[Collection("Appearance")]
public sealed class ConfigOperationTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        Descriptions.Encoding = DescriptionEncoding.Auto;
        _fx.Dispose();
    }

    [AvaloniaFact]
    public async Task Drive_change_returns_to_the_last_folder_or_the_root()
    {
        var (_, vm) = await _fx.OpenAsync();
        var sep = Path.DirectorySeparatorChar.ToString();
        var a = new DriveItem("A", _fx.L() + sep, null, 0, 0, DriveKind.Fixed, true);
        var b = new DriveItem("B", _fx.R() + sep, null, 0, 0, DriveKind.Fixed, true);
        var panel = vm.Left;
        panel.Drives = [a, b];
        await panel.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        Assert.Same(a, panel.SelectedDrive);

        await panel.ChangeDriveAsync(b);
        await WaitUntilAsync(() => panel.CurrentPath == VfsPath.Local(_fx.R()));
        await panel.ChangeDriveAsync(a);
        await WaitUntilAsync(() => panel.CurrentPath == VfsPath.Local(_fx.L("sub")));

        vm.Settings.DriveChangeGoesToRoot = true;
        await panel.ChangeDriveAsync(b);
        await WaitUntilAsync(() => panel.CurrentPath == VfsPath.Local(_fx.R()));
        await panel.ChangeDriveAsync(a);
        await WaitUntilAsync(() => panel.CurrentPath == VfsPath.Local(_fx.L()));
    }

    [AvaloniaFact]
    public async Task Minimize_to_tray_and_back()
    {
        var (w, vm) = await _fx.OpenAsync();
        w.WindowState = Avalonia.Controls.WindowState.Minimized;
        Assert.False(w.IsInTray);
        w.WindowState = Avalonia.Controls.WindowState.Normal;
        vm.Settings.MinimizeToTray = true;
        w.WindowState = Avalonia.Controls.WindowState.Minimized;
        Assert.True(w.IsInTray);
        Assert.False(w.IsVisible);
        w.RestoreFromTray();
        Assert.False(w.IsInTray);
        Assert.True(w.IsVisible);
        Assert.Equal(Avalonia.Controls.WindowState.Normal, w.WindowState);
    }

    [AvaloniaFact]
    public async Task Pattern_selects_folders_on_request()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Left.MarkByPattern("*", mark: true);
        Assert.DoesNotContain(vm.Left.Items, i => i.IsDirectory && i.IsMarked);
        vm.Left.MarkByPattern("sub\\", mark: true); // name\ selects a folder either way
        Assert.Contains(vm.Left.Items, i => i.FullName == "sub" && i.IsMarked);
        vm.Left.MarkAll(false);

        // "Select files only" (the default): Num* and Select all leave the folders alone
        await vm.ExecuteAsync("nc_SelectAll");
        Assert.All(vm.Left.Items.Where(i => !i.IsParent), i => Assert.Equal(!i.IsDirectory, i.IsMarked));
        await vm.ExecuteAsync("nc_ExchangeSelection");
        Assert.DoesNotContain(vm.Left.Items, i => i.IsMarked);
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "sub");
        vm.Left.MarkByPattern("sub\\", mark: true);
        await vm.ExecuteAsync("nc_ExchangeSelection");
        Assert.Contains(vm.Left.Items, i => i.FullName == "sub" && i.IsMarked); // a marked folder stays marked
        vm.Left.MarkAll(false);

        // "Also select directories": the pattern, Num* and Select all take the folders too
        vm.Settings.PatternSelectsFolders = true;
        vm.Left.MarkByPattern("*", mark: true);
        Assert.Contains(vm.Left.Items, i => i.FullName == "sub" && i.IsMarked);
        vm.Left.MarkAll(false);
        await vm.ExecuteAsync("nc_SelectAll");
        Assert.All(vm.Left.Items.Where(i => !i.IsParent), i => Assert.True(i.IsMarked, i.FullName));
        await vm.ExecuteAsync("nc_ExchangeSelection");
        Assert.DoesNotContain(vm.Left.Items, i => i.IsMarked);
        await vm.ExecuteAsync("nc_ExchangeSelection");
        Assert.Contains(vm.Left.Items, i => i.FullName == "sub" && i.IsMarked);
    }

    [AvaloniaFact]
    public async Task Comments_follow_renamed_and_copied_files()
    {
        Descriptions.Write(_fx.L(), "a.txt", "first file");
        var (_, vm) = await _fx.OpenAsync();
        await vm.Left.RefreshAsync();
        vm.BeginInlineRename = null; // the name is asked in the dialog (the fallback of the rename in the list)

        // rename: the comment moves with the name; only the base name is selected by default
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "a.txt");
        _fx.Dialogs.NextPrompt = "renamed.txt";
        await vm.ExecuteAsync("nc_RenameOnly");
        Assert.True(_fx.Dialogs.LastSelectBaseName);
        Assert.Equal("first file", Descriptions.Read(_fx.L()).GetValueOrDefault("renamed.txt"));
        Assert.False(Descriptions.Read(_fx.L()).ContainsKey("a.txt"));

        // copy: the target folder gets the comment too
        await vm.Left.RefreshAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "renamed.txt");
        await vm.ExecuteAsync("nc_Copy");
        Assert.True(File.Exists(_fx.R("renamed.txt")));
        Assert.Equal("first file", Descriptions.Read(_fx.R()).GetValueOrDefault("renamed.txt"));
        Assert.Equal("first file", Descriptions.Read(_fx.L()).GetValueOrDefault("renamed.txt"));

        // off: nothing follows; rename selects the whole name
        vm.Settings.CopyCommentsWithFiles = false;
        vm.Settings.RenameSelectsNameOnly = false;
        await vm.Left.RefreshAsync();
        vm.Left.CursorItem = vm.Left.Items.First(i => i.FullName == "renamed.txt");
        _fx.Dialogs.NextPrompt = "again.txt";
        await vm.ExecuteAsync("nc_RenameOnly");
        Assert.False(_fx.Dialogs.LastSelectBaseName);
        Assert.False(Descriptions.Read(_fx.L()).ContainsKey("again.txt"));
    }

    [AvaloniaFact]
    public void Description_file_encodings()
    {
        var dir = _fx.L();
        Descriptions.Encoding = DescriptionEncoding.Utf16;
        Descriptions.Write(dir, "a.txt", "çay");
        var bytes = File.ReadAllBytes(Descriptions.PathIn(dir));
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        Assert.Equal("çay", Descriptions.Read(dir)["a.txt"]);

        Descriptions.Encoding = DescriptionEncoding.Utf8;
        Descriptions.Write(dir, "b.txt", "plain");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(Descriptions.PathIn(dir))[..3]);
    }

    [AvaloniaFact]
    public async Task Save_on_exit_keeps_what_is_switched_off()
    {
        var (_, vm) = await _fx.OpenAsync();
        var store = new SettingsStore(Path.Combine(_fx.Root, "settings"));
        vm.Settings.CommandLineHistory = ["dir"];
        vm.SaveSettings(complete: true); // explicit save: everything

        vm.Settings.SaveDirsOnExit = false;
        vm.Settings.SaveCommandLineHistoryOnExit = false;
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("sub")));
        vm.Left.ViewMode = PanelViewMode.Brief;
        vm.Settings.CommandLineHistory = ["dir", "new command"];
        vm.SaveSettings();
        var saved = store.Load();
        Assert.Equal(_fx.L(), saved.LeftTabs[0].Path);
        Assert.Equal(PanelViewMode.Brief, saved.LeftTabs[0].ViewMode); // views are still saved
        Assert.Equal(["dir"], saved.CommandLineHistory);

        vm.Settings.SaveDirsOnExit = true;
        vm.Settings.SavePanelViewsOnExit = false;
        vm.Left.ViewMode = PanelViewMode.Full;
        vm.SaveSettings(complete: true);
        vm.Left.ViewMode = PanelViewMode.Brief;
        vm.SaveSettings();
        saved = store.Load();
        Assert.Equal(_fx.L("sub"), saved.LeftTabs[0].Path);
        Assert.Equal(PanelViewMode.Full, saved.LeftTabs[0].ViewMode);
    }
}
