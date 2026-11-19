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
}
