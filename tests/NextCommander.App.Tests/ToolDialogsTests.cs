using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The system information, folder history and folder tree windows.</summary>
[Collection("Appearance")]
public sealed class ToolDialogsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task The_system_information_shows_the_drives_and_the_uptime()
    {
        var info = new SystemInfo("26.11.0042", ".NET 10.0.0", "Windows 11 Pro 10.0.26300", "X64", "WORKSTATION", "user", 16,
            32L << 30, _fx.L("settings"),
            [new SystemDrive("C:", "System", "NTFS", 120L << 30, 1000L << 30), new SystemDrive("D:", "Data", "exFAT", 400L << 30, 500L << 30)],
            "Operating system: Windows 11");
        var opened = 0;
        var dialog = _fx.Track(new SystemInfoDialog(info, () =>
        {
            opened++;
            return Task.CompletedTask;
        }));
        dialog.Show();
        await SettleAsync(4);
        Shot(dialog, "system-info");
        Assert.Equal("3 h 5 min", SystemInfoDialog.UptimeText(new TimeSpan(0, 3, 5, 0)));
        Assert.Equal("1 day 0 h 1 min", SystemInfoDialog.UptimeText(new TimeSpan(1, 0, 1, 0)));
        Assert.Equal("4 days 2 h 0 min", SystemInfoDialog.UptimeText(new TimeSpan(4, 2, 0, 0)));
        Assert.Equal(0, opened);
    }

    [AvaloniaFact]
    public async Task The_folder_history_groups_pins_searches_and_undoes()
    {
        var now = DateTime.Now;
        VfsPath P(string name) => VfsPath.Local(_fx.L(name));
        var entries = new List<FolderHistoryEntry>
        {
            new(P("today"), now.AddMinutes(-5), 7),
            new(P("yesterday"), now.AddDays(-1), 2),
            new(P("old"), now.AddDays(-30), 1),
            new(P("unknown"), null, 0),
        };
        var pinned = new List<string>();
        var saved = 0;
        IReadOnlyList<VfsPath>? history = null;
        var forgotten = new List<VfsPath>();
        VfsPath? opened = null;
        var request = new FolderHistoryRequest(entries, pinned, () => saved++, _ => "C:",
            path =>
            {
                opened = path;
                return Task.CompletedTask;
            }, _ => Task.CompletedTask, _ => Task.CompletedTask, forgotten.Add, paths => history = paths);
        var dialog = _fx.Track(new FolderHistoryDialog(request));
        dialog.Show();
        await SettleAsync(4);
        Assert.Equal(["Today", "Yesterday", "Earlier", "Earlier"], dialog.Sections);

        // a pin goes to the top and into the setting
        dialog.TogglePin(P("old"));
        await SettleAsync();
        Assert.Equal(P("old"), dialog.Shown[0]);
        Assert.Equal("Pinned", dialog.Sections[0]);
        Assert.Equal([P("old").ToString()], pinned);
        Assert.True(saved > 0);
        Shot(dialog, "folder-history");

        // the search matches the path
        dialog.Search("yester");
        await SettleAsync();
        Assert.Equal([P("yesterday")], dialog.Shown);
        dialog.Search(string.Empty);
        await SettleAsync();

        // clearing keeps the pins; Undo puts the history back
        dialog.ClearHistory();
        await SettleAsync();
        Assert.Equal([P("old")], dialog.Shown);
        Assert.Empty(history!);
        var undo = dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "Undo");
        Assert.True(undo.IsVisible);
        undo.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Assert.Equal(4, history!.Count);
        Assert.Equal(4, dialog.Shown.Count);
        Assert.Null(opened);
    }

    [AvaloniaFact]
    public async Task The_folder_tree_filters_loaded_folders_and_hides_hidden_ones()
    {
        var root = _fx.L("tree");
        foreach (var name in new[] { "alpha", "beta", ".secret" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
        }
        var secret = new DirectoryInfo(Path.Combine(root, ".secret"));
        secret.Attributes |= FileAttributes.Hidden;
        var provider = new LocalFileSystemProvider();
        async Task<IReadOnlyList<VfsEntry>> List(VfsPath path) =>
            [.. (await provider.ListAsync(path, CancellationToken.None)).Where(e => e.IsDirectory)];
        var dialog = _fx.Track(new TreeDialog());
        dialog.Show();
        await dialog.InitializeAsync(VfsPath.Local(root), List);
        await SettleAsync(4);

        TreeNodeViewModel? Find(string name)
        {
            TreeNodeViewModel? Walk(TreeNodeViewModel node) =>
                node.Name == name ? node : node.Children.Select(Walk).FirstOrDefault(n => n is not null);
            return dialog.RootNode is { } r ? Walk(r) : null;
        }
        await WaitUntilAsync(() => Find("tree") is not null);
        Find("tree")!.IsExpanded = true; // the chosen folder is selected, not opened
        await WaitUntilAsync(() => Find(".secret") is not null);
        await SettleAsync(4);
        Assert.True(dialog.ShowHidden); // on at first: the tree shows every folder
        Assert.True(Find(".secret")!.IsShown);
        Shot(dialog, "folder-tree");

        dialog.ShowHidden = false;
        await SettleAsync();
        Assert.False(Find(".secret")!.IsShown);
        Assert.True(Find("beta")!.IsShown);

        dialog.FilterText = "alp";
        await SettleAsync();
        Assert.True(Find("alpha")!.IsShown);
        Assert.False(Find("beta")!.IsShown);

        dialog.FilterText = string.Empty;
        dialog.ShowHidden = true;
        await SettleAsync();
        Assert.True(Find(".secret")!.IsShown);
        Assert.True(Find("beta")!.IsShown);
    }

    [AvaloniaFact]
    public async Task The_folder_tree_menu_has_icons_and_runs_its_commands()
    {
        var root = _fx.L("treemenu");
        Directory.CreateDirectory(Path.Combine(root, "a"));
        var provider = new LocalFileSystemProvider();
        async Task<IReadOnlyList<VfsEntry>> List(VfsPath path) =>
            [.. (await provider.ListAsync(path, CancellationToken.None)).Where(e => e.IsDirectory)];

        // nobody to run its commands: no right click menu (none that does nothing)
        var plain = _fx.Track(new TreeDialog());
        plain.Show();
        await plain.InitializeAsync(VfsPath.Local(root), List);
        Assert.Null(plain.TreeMenu);

        var calls = new List<(Window Owner, string Command, VfsPath Path)>();
        var dialog = _fx.Track(new TreeDialog("Folder to scan")
        {
            Commands = (owner, command, path) =>
            {
                calls.Add((owner, command, path));
                return Task.CompletedTask;
            },
        });
        dialog.Show();
        await dialog.InitializeAsync(VfsPath.Local(root), List);
        var menu = dialog.TreeMenu!;
        menu.Open(); // on the tree it is attached to
        await SettleAsync();
        Assert.Equal(5, menu.Items.Count);
        Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.Icon)); // icons in the menu
        menu.Close();

        // a command runs with the folder tree window as the owner of its questions
        await dialog.RunCommandAsync("tree_mkdir", VfsPath.Local(root));
        var (owner, command, path) = Assert.Single(calls);
        Assert.Same(dialog, owner);
        Assert.Equal(("tree_mkdir", VfsPath.Local(root)), (command, path));
    }

    [AvaloniaFact]
    public async Task The_folder_tree_shows_every_fixed_drive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // drive letters
        }
        var root = _fx.L("drives");
        var provider = new LocalFileSystemProvider();
        async Task<IReadOnlyList<VfsEntry>> List(VfsPath path) =>
            Directory.Exists(path.Path) ? [.. (await provider.ListAsync(path, CancellationToken.None)).Where(e => e.IsDirectory)] : [];
        var own = Path.GetPathRoot(root)!;
        var other = own.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\";
        var dialog = _fx.Track(new TreeDialog { Drives = () => [VfsPath.Local(own), VfsPath.Local(other)] });
        dialog.Show();
        await dialog.InitializeAsync(VfsPath.Local(root), List);
        await SettleAsync();

        // the folder's own drive (opened down to it) and the other drives, each once, by name
        Assert.Equal(new[] { own, other }.Order(StringComparer.OrdinalIgnoreCase), dialog.RootNodes.Select(r => r.Path.Path));
        Assert.Equal(VfsPath.Local(root), dialog.Selected);
        Assert.True(dialog.RootNodes.Single(r => r.Path.Path == own).IsExpanded);
        Assert.False(dialog.RootNodes.Single(r => r.Path.Path == other).IsExpanded);
    }

    [AvaloniaFact]
    public async Task The_commands_window_searches_and_changes_shortcuts()
    {
        var settings = new NextCommander.Core.Config.AppSettings();
        var hotkeys = new NextCommander.App.Services.HotkeyMap(settings);
        Task Nothing(string? _) => Task.CompletedTask;
        NextCommander.Core.Commands.CommandDefinition[] commands =
        [
            new("nc_Copy", "Copy files", Nothing), new("nc_RenMov", "Rename or move files", Nothing), new("nc_SrcThumbs", "Thumbnails view", Nothing),
            new("nc_SwitchHidSys", "Show or hide hidden and system files", Nothing), new("nc_About", "About", Nothing), new("nc_LinkConnect", "Connect to a PC", Nothing),
        ];
        var saved = 0;
        string? ran = null;
        (string, string)? added = null;
        var request = new CommandBrowserRequest(commands, hotkeys, settings, () => saved++, c =>
        {
            ran = c;
            return Task.CompletedTask;
        }, (c, d) => added = (c, d),
            new Dictionary<string, string> { ["nc_Copy"] = "Edit", ["nc_RenMov"] = "Edit", ["nc_SrcThumbs"] = "Show", ["nc_SwitchHidSys"] = "Show", ["nc_About"] = "Help" },
            new HashSet<string> { "nc_Copy" });
        var dialog = _fx.Track(new CommandBrowserWindow(request));
        dialog.Show();
        await SettleAsync(4);
        Assert.Equal(6, dialog.Shown.Count);
        dialog.Select("nc_Copy");
        await SettleAsync();
        Shot(dialog, "commands");

        // categories from the menus; commands in no menu are "Other"
        dialog.Category = "Show";
        Assert.Equal(["nc_SrcThumbs", "nc_SwitchHidSys"], dialog.Shown);
        dialog.Category = "Other";
        Assert.Equal(["nc_LinkConnect"], dialog.Shown);
        dialog.Category = "all";

        // words in any order, over names, descriptions and shortcuts
        dialog.Search("files hidden");
        Assert.Equal(["nc_SwitchHidSys"], dialog.Shown);
        dialog.Search("F5");
        Assert.Equal("nc_Copy", dialog.Shown[0]);
        dialog.Search(string.Empty);
        dialog.ByShortcut = true;
        dialog.Search("F6");
        Assert.Equal(["nc_RenMov"], dialog.Shown);
        dialog.ByShortcut = false;

        // keys of another command: a warning first, then they move
        dialog.Select("nc_SrcThumbs");
        dialog.AssignShortcut("F5");
        Assert.Equal(("F5", "nc_Copy"), dialog.Conflict);
        Assert.DoesNotContain("F5", dialog.KeysOf("nc_SrcThumbs"));
        dialog.AssignShortcut("F5", force: true);
        Assert.Contains("F5", dialog.KeysOf("nc_SrcThumbs"));
        Assert.DoesNotContain("F5", dialog.KeysOf("nc_Copy"));
        Assert.True(dialog.IsModified("nc_SrcThumbs"));
        Assert.True(saved > 0);
        dialog.Category = "mod";
        Assert.Contains("nc_SrcThumbs", dialog.Shown);
        dialog.Category = "all";

        // back to the defaults; a default key can be turned off
        dialog.ResetShortcuts();
        Assert.DoesNotContain("F5", dialog.KeysOf("nc_SrcThumbs"));
        Assert.False(dialog.IsModified("nc_SrcThumbs"));
        dialog.Select("nc_RenMov");
        dialog.RemoveShortcut("F6");
        Assert.DoesNotContain("F6", dialog.KeysOf("nc_RenMov"));
        Assert.Equal(string.Empty, settings.Hotkeys["F6"]);

        // favorites, the button bar; Run closes and remembers the command
        dialog.ToggleFavorite("nc_About");
        dialog.Category = "fav";
        Assert.Equal(["nc_About"], dialog.Shown);
        dialog.Category = "all";
        dialog.Select("nc_About");
        await dialog.RunAsync();
        Assert.Equal("nc_About", ran);
        Assert.Equal(["nc_About"], settings.RecentCommands);
        Assert.Null(added);
    }

    [AvaloniaFact]
    public async Task The_hotlist_window_edits_submenus_separators_and_commands()
    {
        List<NextCommander.Core.Config.HotlistEntry> entries =
        [
            new() { Name = "&Movie", Path = "D:\\Movie" },
            new() { Name = "&Downloads", Path = "%USERPROFILE%\\Downloads" },
            new() { Name = "-" },
            new() { Name = "Projects/&Website", Path = "N:\\Projects\\Website", Target = "N:\\Backup\\Website" },
            new() { Name = "Projects/W&MS", Path = "N:\\Projects\\WMS" },
            new() { Name = "Projects/Archive", Path = string.Empty },
            new() { Name = "Open &terminal here", Path = "nc_ExecuteTerminal" },
        ];
        var dialog = _fx.Track(new HotlistEditorDialog(entries, _fx.L(), _fx.R()));
        dialog.Show();
        await SettleAsync(4);
        Assert.Equal(["&Movie", "&Downloads", "-", "Projects", "  &Website", "  W&MS", "  Archive", "Open &terminal here"], dialog.Rows);
        dialog.Select("Website");
        await SettleAsync();
        Shot(dialog, "hotlist");

        // nothing changes before OK: the same entries come back out
        Assert.Equal(entries.Select(e => (e.Name, e.Path, e.Target)), dialog.Entries.Select(e => (e.Name, e.Path, e.Target)));

        // a second "M" menu key in the same menu is a warning
        dialog.Select("Downloads");
        dialog.Add("folder");
        var box = dialog.GetLogicalDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Name");
        box.Text = "&Music";
        await SettleAsync();
        Assert.Contains("“M”", dialog.NameNote, StringComparison.Ordinal);
        Assert.Equal("&Music", dialog.Entries[2].Name);

        // out of the submenu, back into it by dragging, a separator, delete and Undo
        dialog.Select("Archive");
        dialog.MoveOut();
        Assert.Equal("Archive", dialog.Entries.First(e => e.Leaf() == "Archive").Name);
        dialog.DropOn("Archive", "Projects", "in");
        Assert.Equal("Projects/Archive", dialog.Entries.First(e => e.Leaf() == "Archive").Name);
        dialog.Add("separator");
        Assert.Contains(dialog.Entries, e => e.Name == "Projects/-");
        dialog.Select("Movie");
        dialog.Delete();
        Assert.DoesNotContain(dialog.Entries, e => e.Name == "&Movie");
        dialog.Undo();
        Assert.Equal("&Movie", dialog.Entries[0].Name);

        // a submenu emptied stays as "Projects/"; the filter shows the matching entries with their submenu
        dialog.FilterText = "wms";
        await SettleAsync();
        Assert.Equal(["Projects", "  W&MS"], dialog.Rows);
        dialog.FilterText = string.Empty;
        dialog.Save();
        Assert.True(dialog.Saved);
        Assert.Contains(entries, e => e.Name == "&Music");
        Assert.Contains(entries, e => e.Name == "Projects/&Website" && e.Target == "N:\\Backup\\Website");
    }

    [AvaloniaFact]
    public async Task The_progress_window_shows_the_files_pauses_skips_and_sums_up()
    {
        var source = _fx.L("iso");
        Directory.CreateDirectory(source);
        var target = _fx.R();
        string[] names = ["Fedora-Workstation-Live.iso", "ubuntu-26.04-desktop-amd64.iso", "debian-netinst.iso", "README.txt", "SHA256SUMS"];
        foreach (var name in names)
        {
            await File.WriteAllBytesAsync(Path.Combine(source, name), new byte[name.EndsWith(".iso", StringComparison.Ordinal) ? 160 * 1024 : 2 * 1024]);
        }
        var local = new LocalFileSystemProvider();
        var entries = (await local.ListAsync(VfsPath.Local(source), CancellationToken.None)).OrderBy(e => Array.IndexOf(names, e.Name)).ToList();
        var job = new NextCommander.Core.Operations.CopyMoveJob(new ProviderRegistry([local]), entries, VfsPath.Local(target), move: false,
            new NextCommander.Core.Operations.CopyOptions { SpeedLimit = 96 * 1024, BufferSize = 4096 });
        var closeSetting = (bool?)null;
        var opened = false;
        var window = _fx.Track(new ProgressWindow(job, new ProgressOptions(false, v => closeSetting = v, () =>
        {
            opened = true;
            return Task.CompletedTask;
        })));
        var running = Task.Run(job.RunAsync);
        window.Show();
        await WaitUntilAsync(() => job.Progress.Snapshot().DoneFiles >= 1 && job.Progress.Snapshot().CurrentFileDone > 0);
        await SettleAsync(8);
        Shot(window, "progress");

        // pause: the window says so; the cancel question; the small window
        window.Focus();
        Press(window, Avalonia.Input.Key.Space);
        Assert.True(job.Pause.IsPaused);
        await SettleAsync(4);
        Shot(window, "progress-paused");
        Press(window, Avalonia.Input.Key.Escape);
        Assert.True(window.IsConfirmingCancel);
        await SettleAsync(2);
        Shot(window, "progress-cancel");
        Press(window, Avalonia.Input.Key.Escape);
        Assert.False(window.IsConfirmingCancel);
        window.SetMini(true);
        await SettleAsync(2);
        Shot(window, "progress-mini");
        window.SetMini(false);

        // skip the file being copied, go on, done: the summary stays ("close when done" off)
        Press(window, Avalonia.Input.Key.Space);
        Assert.False(job.Pause.IsPaused);
        job.SkipCurrentFile();
        job.SpeedLimit.BytesPerSecond = 0;
        await running;
        window.ShowFinished();
        await SettleAsync(4);
        Shot(window, "progress-done");
        Assert.Equal(NextCommander.Core.Operations.JobState.Completed, job.State);
        Assert.Contains(job.Progress.Items, i => i.State == NextCommander.Core.Operations.ProgressItemState.Skipped);
        Assert.False(window.CloseWhenDone);
        var open = window.GetLogicalDescendants().OfType<Button>().First(b => b.Content is StackPanel sp && sp.Children.OfType<TextBlock>().Any(t => t.Text == "Open destination"));
        open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Assert.True(opened);
        Assert.Null(closeSetting);
    }

    [AvaloniaFact]
    public async Task The_column_sets_window_edits_a_copy_until_applied()
    {
        var sets = new List<NextCommander.Core.Columns.ColumnSet>
        {
            new()
            {
                Name = "Fotoğraflar", Icon = "image", AutoPathMask = "*Pictures*;*DCIM*", TabColor = "#2E7D32", EnterCommand = "nc_SrcThumbs",
                Columns = [new() { Field = "image.taken", Width = 130 }, new() { Field = "image.dimensions", Width = 110 }, new() { Field = "image.camera", Width = 150 }],
            },
            new() { Name = "Müzik", Icon = "audio", Columns = [new() { Field = "media.artist", Width = 160 }, new() { Field = "media.duration", Header = "Time", Width = 70 }] },
        };
        NextCommander.Core.Columns.ColumnField[] fields =
        [
            new("image.taken", "Date taken", NextCommander.Core.Columns.ColumnKind.Date), new("image.dimensions", "Dimensions (W×H)"),
            new("image.camera", "Camera"), new("media.artist", "Artist"), new("media.duration", "Duration"), new("file.created", "Created"),
        ];
        var applied = 0;
        var dialog = _fx.Track(new ColumnSetDialog(sets, fields, () => applied++));
        dialog.Show();
        await SettleAsync(4);
        Assert.Equal("Fotoğraflar", dialog.Current!.Name);
        Assert.Equal(["Name", "Size", "Date", "Date taken", "Dimensions (W×H)", "Camera"], dialog.PreviewHeaders);
        Assert.False(dialog.IsDirty);
        Shot(dialog, "column-sets");
        dialog.Height = 1500; // the whole page: masks, look, commands
        await SettleAsync(4);
        Shot(dialog, "column-sets-full");
        dialog.Height = 800;

        // changes stay in the copy
        dialog.AddColumn("file.created");
        dialog.MoveColumn(3, -3);
        Assert.Equal("file.created", dialog.Current.Columns[0].Field);
        dialog.AddMask("*Resimler*; *Fotoğraf*");
        Assert.Equal("*Pictures*;*DCIM*;*Resimler*;*Fotoğraf*", dialog.Current.AutoPathMask);
        dialog.RemoveMask("*DCIM*");
        Assert.True(dialog.IsDirty);
        Assert.Equal(3, sets[0].Columns.Count);

        // a copy has no folders (two sets would fight over them); deleting picks the next one
        dialog.DuplicateSet();
        Assert.Equal("Fotoğraflar (copy)", dialog.Current.Name);
        Assert.Null(dialog.Current.AutoPathMask);
        dialog.DeleteSet();
        Assert.Equal(2, dialog.WorkingSets.Count);

        // Apply writes copies back
        dialog.Apply();
        Assert.Equal(1, applied);
        Assert.False(dialog.IsDirty);
        Assert.Equal(4, sets[0].Columns.Count);
        Assert.Equal("*Pictures*;*Resimler*;*Fotoğraf*", sets[0].AutoPathMask);
        Assert.NotSame(dialog.WorkingSets[0], sets[0]);

        // after that, changes without Apply are dropped on closing
        dialog.SelectSet(1);
        dialog.RemoveColumn(0);
        dialog.Close();
        Assert.Equal(2, sets[1].Columns.Count);
    }
}
