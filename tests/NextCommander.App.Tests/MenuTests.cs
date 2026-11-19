using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Files and Mark menus.</summary>
public sealed class MenuTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static void SetCursor(PanelViewModel panel, string name) =>
        panel.MoveCursorTo(panel.Items.ToList().FindIndex(i => i.FullName == name));

    private static List<string> Headers(Window w, string menu) =>
        w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Header as string == menu)
            .Items.OfType<MenuItem>().Select(m => (string)m.Header!).ToList();

    [AvaloniaFact]
    public async Task Menus_follow_total_commander_order()
    {
        var (w, _) = await _fx.OpenAsync();
        // related commands in submenus (the menu fits on a smaller screen)
        Assert.Equal(["Change attributes...", "Archive", "Compare by content...", "Associations", "Properties...", "Calculate occupied space...",
            "Multi-Rename Tool...", "Edit comment...", "Print", "Split / combine", "Encode / decode", "Checksum", "Exit"], Headers(w, "_Files"));
        Assert.Equal("View", Headers(w, "_Edit")[0]); // the F key commands in their own menu after Files
        Assert.Equal(["Pack...", "Unpack files...", "Test archives"], Headers(w, "Archive"));
        Assert.Equal(["Create checksums (MD5/SHA)...", "Verify checksums"], Headers(w, "Checksum"));
        var mark = Headers(w, "_Mark");
        Assert.Equal(["Select group...", "Unselect group...", "Select all", "Unselect all", "Invert selection", "Select all with the same extension"], mark.Take(6));
        Assert.Equal(["Compare folders", "Mark newer, hide identical"], mark.TakeLast(2));
        Assert.DoesNotContain(w.GetLogicalDescendants().OfType<MenuItem>(), m => m.Header as string == "_Araçlar");
    }

    [AvaloniaFact]
    public async Task Selection_keys_and_same_extension()
    {
        var (w, vm) = await _fx.OpenAsync();
        SetCursor(vm.Left, "a.txt");
        Press(w, Key.Add, RawInputModifiers.Alt);
        Assert.Equal(["a.txt", "file2.txt", "file10.txt"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName));
        Press(w, Key.Subtract, RawInputModifiers.Control);
        Assert.DoesNotContain(vm.Left.Items, i => i.IsMarked);
        Press(w, Key.Add, RawInputModifiers.Control);
        Assert.Equal(4, vm.Left.Items.Count(i => i.IsMarked)); // the 4 files ("Select files only", the default)
        Press(w, Key.Subtract, RawInputModifiers.Control);
        vm.Settings.PatternSelectsFolders = true; // Configuration > Operation > Also select directories
        Press(w, Key.Add, RawInputModifiers.Control);
        Assert.Equal(5, vm.Left.Items.Count(i => i.IsMarked)); // sub + 4 files
    }

    [AvaloniaFact]
    public async Task Compare_directories_marks_newer_and_hides_same()
    {
        var old = new DateTime(2020, 1, 1);
        File.SetLastWriteTime(_fx.L("a.txt"), new DateTime(2024, 1, 1));
        File.WriteAllText(_fx.R("a.txt"), "A");
        File.SetLastWriteTime(_fx.R("a.txt"), old); // left is newer
        File.Copy(_fx.L("b.log"), _fx.R("b.log"));
        File.SetLastWriteTime(_fx.L("b.log"), old);
        File.SetLastWriteTime(_fx.R("b.log"), old); // same
        File.WriteAllText(_fx.R("only-right.txt"), "R");
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.F2, RawInputModifiers.Shift);
        Assert.Equal(["a.txt", "file2.txt", "file10.txt"], vm.Left.Items.Where(i => i.IsMarked).Select(i => i.FullName));
        Assert.Equal(["only-right.txt"], vm.Right.Items.Where(i => i.IsMarked).Select(i => i.FullName));

        await vm.ExecuteAsync("nc_CompareDirsHideSame");
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == "b.log");
        Assert.DoesNotContain(vm.Right.Items, i => i.FullName == "b.log");
        Press(w, Key.R, RawInputModifiers.Control); // re-read shows them again
        await WaitUntilAsync(() => vm.Left.Items.Any(i => i.FullName == "b.log"));
        Assert.Contains(vm.Left.Items, i => i.FullName == "b.log");
    }

    [AvaloniaFact]
    public async Task Comment_space_properties_and_associations()
    {
        var zip = _fx.L("pack.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("inside/doc.txt").Open());
            writer.Write("hello");
        }
        var (w, vm) = await _fx.OpenAsync();

        SetCursor(vm.Left, "a.txt");
        _fx.Dialogs.NextComment = "önemli dosya";
        Press(w, Key.Z, RawInputModifiers.Control);
        await WaitUntilAsync(() => File.Exists(Path.Combine(_fx.L(), "descript.ion")));
        Assert.Equal("önemli dosya", NextCommander.Core.Tools.Descriptions.Read(_fx.L())["a.txt"]);

        SetCursor(vm.Left, "sub");
        Press(w, Key.L, RawInputModifiers.Control);
        await WaitUntilAsync(() => _fx.Dialogs.Messages.Any(m => m.Contains("files", StringComparison.Ordinal)));
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("1 bytes", StringComparison.Ordinal) && m.Contains("1 files", StringComparison.Ordinal));

        // Inside an archive the application's own properties dialog is used (with folder sizes).
        await vm.Left.NavigateAsync(_fx.Archives.GetRoot(VfsPath.Local(zip)));
        SetCursor(vm.Left, "inside");
        Press(w, Key.Enter, RawInputModifiers.Alt);
        await WaitUntilAsync(() => _fx.Dialogs.LastProperties is not null);
        var (entry, measure) = _fx.Dialogs.LastProperties!.Value;
        Assert.Equal("inside", entry.Name);
        var space = await measure!();
        Assert.Equal((5L, 1), (space.Bytes, space.Files));

        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L()));
        SetCursor(vm.Left, "b.log");
        _fx.Dialogs.QuickListHandler = items => new Services.QuickListResult(items.First(i => i.Value as string == "lister"), -1);
        await vm.ExecuteAsync("nc_AssociateWith");
        Assert.Equal(("*.log", "lister"), (vm.Settings.Associations[0].Mask, vm.Settings.Associations[0].Command));
        await vm.ExecuteAsync("nc_InternalAssociate");
        Assert.Equal("associate", _fx.Dialogs.LastSettingsSearch);
    }

    [AvaloniaFact]
    public async Task Encode_and_decode_files()
    {
        var (_, vm) = await _fx.OpenAsync();
        SetCursor(vm.Left, "b.log");
        _fx.Dialogs.QuickListHandler = items => new Services.QuickListResult(items.First(i => i.Text == "UUE"), -1);
        await vm.ExecuteAsync("nc_UUEncode");
        Assert.StartsWith("begin 644 b.log", File.ReadAllText(_fx.R("b.log.uue")), StringComparison.Ordinal);

        await vm.Right.RefreshAsync();
        vm.Activate(vm.Right);
        SetCursor(vm.Right, "b.log.uue");
        Directory.CreateDirectory(_fx.L("decoded"));
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("decoded")));
        await vm.ExecuteAsync("nc_UUDecode");
        Assert.Equal("BB", File.ReadAllText(_fx.L("decoded", "b.log")));
    }

    [AvaloniaFact]
    public async Task Commands_menu_order_and_commands()
    {
        var (w, vm) = await _fx.OpenAsync();
        Assert.Equal(["Folder tree...", "Find files...", "Find files in separate process...", "Volume label...", "System information...", "Synchronize directories...",
            "Hotlist", "Back", "Open command prompt window", "Internal terminal", "Branch View (with subfolders)", "Open Desktop folder",
            "Background transfer manager...", "Source <-> Target", "Target = Source", "Calculate folder sizes", "Show in system file manager", "Duplicate files...", "Folder sizes..."], Headers(w, "_Commands"));

        SetCursor(vm.Left, "b.log");
        await vm.ExecuteAsync("nc_MatchSrc");
        Assert.Equal(_fx.L(), vm.Right.CurrentPath!.Path);
        Assert.Equal("b.log", vm.Right.CursorItem!.FullName);

        await vm.ExecuteAsync("nc_SysInfo");
        Assert.Contains(_fx.Dialogs.Messages, m => m.StartsWith("Operating system:", StringComparison.Ordinal));

        await vm.ExecuteAsync("nc_OpenDesktop");
        Assert.True(Directory.Exists(vm.Left.CurrentPath!.Path));

        await vm.ExecuteAsync("nc_BackgroundTransfers");
        Assert.NotNull(_fx.Dialogs.LastTransferManager);

        // Results of a find window in another process arrive as a list file.
        var list = Path.Combine(_fx.Root, "results.lst");
        File.WriteAllLines(list, [_fx.L("a.txt"), _fx.L("sub", "inner.txt")]);
        await vm.HandleArgumentsAsync(["--loadlist", list]);
        Assert.NotNull(vm.ActivePanel.VirtualTitle);
        Assert.Equal(2, vm.ActivePanel.Items.Count(i => i.Entry is not null));
    }

    private sealed class WaitingJob(string title, TaskCompletionSource release) : NextCommander.Core.Operations.FileJob(title)
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            Progress.SetTotals(100, 2);
            Progress.BeginItem("büyük.iso", 100);
            Progress.AddBytes(40);
            await release.Task.WaitAsync(ct);
        }
    }

    [AvaloniaFact]
    public async Task Transfer_manager_lists_running_and_queued_jobs()
    {
        await using var jobs = new NextCommander.Core.Operations.JobQueue();
        var release = new TaskCompletionSource();
        var running = new WaitingJob("Kopyala: film", release);
        var queued = new WaitingJob("Taşı: yedek", release);
        var task = jobs.RunAsync(running);
        jobs.Enqueue(new WaitingJob("Kuyruktaki ilk", release));
        jobs.Enqueue(queued);
        await WaitUntilAsync(() => running.State == NextCommander.Core.Operations.JobState.Running);

        var window = _fx.Track(new NextCommander.App.Views.Tools.TransferManagerWindow(jobs));
        window.Show();
        window.Refresh();
        Assert.Contains(window.Rows, r => r.StartsWith("Kopyala: film", StringComparison.Ordinal) && r.Contains("%40", StringComparison.Ordinal) && r.Contains("büyük.iso", StringComparison.Ordinal));
        Assert.Contains(window.Rows, r => r.StartsWith("Taşı: yedek", StringComparison.Ordinal) && r.Contains("queued", StringComparison.Ordinal));

        running.TogglePause();
        window.Refresh();
        Assert.Contains(window.Rows, r => r.Contains("paused", StringComparison.Ordinal));
        running.TogglePause();
        release.SetResult();
        await task;
        await WaitUntilAsync(() => jobs.ActiveJobs.Count == 0);
        window.Refresh();
        Assert.Empty(window.Rows);
    }

    [AvaloniaFact]
    public async Task Top_menus_and_show_commands_follow_total_commander()
    {
        var (w, vm) = await _fx.OpenAsync();
        var top = w.GetLogicalDescendants().OfType<Menu>().First().Items.OfType<MenuItem>().Select(m => (string)m.Header!).ToList();
        Assert.Equal(["_Files", "_Edit", "_Mark", "_Commands", "_Net", "_Show", "C_onfiguration", "_Start", "_Help"], top);
        // related options in submenus; navigation and tabs (formerly under "Other") on the menu itself
        Assert.Equal(["Display", "Tree", "Quick view panel", "Panels", "File filter", "Sort", "What is shown", "Navigation", "Tabs",
            "Reread source"], Headers(w, "_Show"));
        Assert.Equal(["Brief", "Full", "Comments", "Thumbnails view", "Custom columns mode", "Custom view modes", "Choose column set..."], Headers(w, "Display"));
        Assert.Equal(["Name", "Extension", "Time", "Size", "Unsorted", "Reversed order"], Headers(w, "Sort"));
        Assert.Equal(["All files", "Programs", "*.*", "Custom...", "Only selected files"], Headers(w, "File filter"));
        Assert.Contains("New folder tab", Headers(w, "Tabs"));
        Assert.Equal(["Options...", "Plugins...", "Button bar...", "Button bar 2 (vertical)...", "Dark <-> Normal",
            "Save position", "Save settings", "Export settings...", "Import settings...", "Restore settings from a backup...", "Commands and shortcuts...", "Add button to button bar...", "Add user command (uc_)...", "Edit Hotlist...",
            "Edit column sets...", "Color theme"], Headers(w, "C_onfiguration"));

        File.WriteAllText(_fx.L("run.exe"), "x");
        await vm.Left.RefreshAsync();
        await vm.ExecuteAsync("nc_SrcExecs");
        Assert.Equal(["..", "sub", "run.exe"], vm.Left.Items.Select(i => i.FullName));
        Assert.Contains("*.exe", vm.Left.FilterMaskText, StringComparison.Ordinal);
        await vm.ExecuteAsync("nc_SrcAllFiles");
        Assert.Equal("*.*", vm.Left.FilterMaskText);
        Assert.Contains(vm.Left.Items, i => i.FullName == "a.txt");

        SetCursor(vm.Left, "b.log");
        await vm.ExecuteAsync("nc_SrcOnlySelected");
        Assert.Equal(["..", "b.log"], vm.Left.Items.Select(i => i.FullName));
        await vm.ExecuteAsync("nc_SrcAllFiles");

        await vm.ExecuteAsync("nc_SrcByName");
        var names = vm.Left.Items.Where(i => !i.IsDirectory).Select(i => i.FullName).ToList();
        await vm.ExecuteAsync("nc_SrcNegOrder");
        Assert.Equal(Enumerable.Reverse(names), vm.Left.Items.Where(i => !i.IsDirectory).Select(i => i.FullName));

        Assert.Equal("[sub]", vm.Left.Items.First(i => i.FullName == "sub").ShownName);
        Assert.Equal("[..]", vm.Left.Items[0].BriefName);

        await vm.ExecuteAsync("nc_VerticalPanels");
        await SettleAsync();
        Assert.True(vm.Settings.VerticalPanels);
        Assert.Equal(2, Grid.GetRow(w.FindControl<Views.PanelSideView>("RightSideView")!));
        await vm.ExecuteAsync("nc_VerticalPanels");
        await SettleAsync();
        Assert.Equal(2, Grid.GetColumn(w.FindControl<Views.PanelSideView>("RightSideView")!));
    }

    [AvaloniaFact]
    public async Task Network_menu_groups_connections_and_shows_product_marks()
    {
        var (w, vm) = await _fx.OpenAsync();
        Assert.Equal(["Connection manager...", "Saved connections", "Connect by address...", "Disconnect",
            "Network (machines of the local network)", "Map network drive...", "Disconnect network drives...", "Share current directory...", "Unshare directory...",
            "Show admin shares", "Download from URL...", "Download the addresses of a list...", "Show hidden files on FTP", "Master password...", "PC link...", "Share with PC link"],
            Headers(w, "_Net"));
        // no "New connection" submenu: the connection manager makes new connections (nc_NewConnection still opens it on a type)
        Assert.DoesNotContain(w.GetLogicalDescendants().OfType<MenuItem>(), m => m.Header as string is "New connection" or "Google Drive...");

        // a new Dropbox connection is saved and opened; a saved one opens by name
        _fx.Dialogs.NextNewConnection = new Core.Config.RemoteConnection
        {
            Name = "Dropbox (kisisel)", Protocol = Core.Config.RemoteProtocol.Dropbox, Host = "dropbox.com", User = "kisisel", ClientId = "k",
        };
        await vm.ExecuteAsync("nc_NewConnection dropbox");
        Assert.Equal(Core.Config.RemoteProtocol.Dropbox, _fx.Dialogs.LastNewConnection);
        Assert.Contains(vm.SavedConnections, c => c.Name == "Dropbox (kisisel)");
        _fx.Dialogs.NextNewConnection = null;
        await vm.ExecuteAsync("nc_NewConnection gdrive");
        Assert.Equal(Core.Config.RemoteProtocol.GoogleDrive, _fx.Dialogs.LastNewConnection);
        Assert.Single(vm.SavedConnections); // cancelled

        await vm.ExecuteAsync("nc_OpenConnection yok");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("yok", StringComparison.Ordinal));
        var saved = w.FindControl<MenuItem>("SavedConnectionsMenu")!;
        saved.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
        await SettleAsync();
        // grouped by type: a Dropbox submenu holding the connection
        var group = Assert.Single(saved.Items.OfType<MenuItem>());
        Assert.Equal("Dropbox (1)", group.Header);
        Assert.NotNull(group.Icon);
        var entry = Assert.Single(group.Items.OfType<MenuItem>());
        Assert.Equal("Dropbox (kisisel)", entry.Header);
        Assert.NotNull(entry.Icon);
    }

    [AvaloniaFact]
    public async Task Hotlist_menu_opens_below_the_star_button_or_at_the_file_list()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.Hotlist.Add(new Core.Config.HotlistEntry { Name = "Proje", Path = _fx.L() });
        var bar = w.RightPanel.GetVisualDescendants().OfType<Views.PathBarView>().First();
        var star = bar.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HotlistButton");
        star.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        var menu = w.HotlistMenu!;
        Assert.True(menu.IsOpen);
        var first = menu.Items.OfType<MenuItem>().First();
        var below = star.PointToScreen(new Avalonia.Point(star.Bounds.Width, star.Bounds.Height));
        var at = first.PointToScreen(default);
        Assert.True(Math.Abs(at.Y - below.Y) < 30, $"menu at {at}, button bottom at {below}");
        Assert.True(Math.Abs(at.X - below.X) < 400 && at.X <= below.X, $"menu at {at}, button right at {below}");
        menu.Hide();

        // from the keyboard: at the top left of the active file list
        await vm.ExecuteAsync("nc_DirectoryHotlist");
        await SettleAsync();
        var list = vm.ActiveSide == vm.RightSide ? w.RightPanel.ListArea : w.LeftPanel.ListArea;
        var corner = list.PointToScreen(default);
        var keyboard = w.HotlistMenu!.Items.OfType<MenuItem>().First().PointToScreen(default);
        Assert.True(Math.Abs(keyboard.X - corner.X) < 30 && Math.Abs(keyboard.Y - corner.Y) < 30, $"menu at {keyboard}, list at {corner}");
        w.HotlistMenu.Hide();
    }
}
