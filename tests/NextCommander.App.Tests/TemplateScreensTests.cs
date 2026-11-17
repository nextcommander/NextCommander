using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The windows built after the templates (copy dialog, find files, multi-rename), light and dark.</summary>
[Collection("Appearance")]
public sealed class TemplateScreensTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task The_copy_dialog_follows_the_template()
    {
        await _fx.OpenAsync();
        var dialog = _fx.Track(new CopyDialog(false, "Copy \"Movie\" to", _fx.R(), false, [_fx.L()], new CopyDialogOptions
        {
            Settings = new AppSettings(),
            Summary = new CopySummary(35_300_000_000, 35, 2),
            PickFolder = (_, _) => Task.FromResult<string?>(null),
            FindFiles = _ => Task.FromResult<IReadOnlyList<Core.Vfs.VfsEntry>?>(null),
        }));
        dialog.Show();
        await SettleAsync();
        await DesignShots.CaptureAsync(dialog, "copy");
        dialog.ShowAdvanced(true);
        Assert.True(dialog.AdvancedShown);
        await DesignShots.CaptureAsync(dialog, "copy-advanced");

        // a quick filter puts its patterns into the filter, and takes them out again
        dialog.FilterBox.Text = "| sample\\";
        var video = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .Single(b => b.Content as string == "Video");
        video.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal("*.mkv *.mp4 *.avi *.mov | sample\\", dialog.FilterBox.Text);
        Assert.True(video.IsChecked);
        video.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(" | sample\\", dialog.FilterBox.Text);
        Assert.False(video.IsChecked);
    }

    [AvaloniaFact]
    public async Task Find_files_follows_the_template()
    {
        var (_, vm) = await _fx.OpenAsync();
        var find = _fx.Track(new Views.Search.FindFilesWindow(new FindFilesRequest(vm.Tools, _fx.L(), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync)));
        find.Show();
        find.SetFields("file* *.log", _fx.L());
        await find.StartAsync();
        Assert.Equal(["b.log", "file10.txt", "file2.txt"], find.ResultEntries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.False(find.IsSearching);

        // a click on a column head sorts by it, a second click the other way
        find.SortBy("size");
        Assert.Equal("file2.txt", find.ResultEntries[0].Name); // 1 byte; the others have 2
        find.SortBy("name");
        Assert.Equal(["b.log", "file10.txt", "file2.txt"], find.ResultEntries.Select(e => e.Name));
        find.SortBy("name");
        Assert.Equal(["file2.txt", "file10.txt", "b.log"], find.ResultEntries.Select(e => e.Name));
        await SettleAsync();
        await DesignShots.CaptureAsync(find, "find-files");
    }

    [AvaloniaFact]
    public async Task Multi_rename_follows_the_template()
    {
        var (_, vm) = await _fx.OpenAsync();
        var dir = Core.Vfs.VfsPath.Local(_fx.L());
        var entries = (await vm.Tools.Registry.Resolve(dir).ListAsync(dir, CancellationToken.None))
            .Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        var window = _fx.Track(new Views.Tools.MultiRenameWindow(entries, vm.Tools, () => vm.Left.RefreshAsync()));
        window.Show();
        await SettleAsync(4);
        window.Apply(new Core.Rename.MultiRenameOptions { NameMask = "[N]", Search = "file", Replace = "report_", Case = Core.Rename.CaseMode.Title });
        Assert.Equal(Core.Rename.CaseMode.Title, window.Options.Case);
        Assert.Contains(window.Rows, r => r.NewName == "Report_10.txt" && r.State == "ok");
        Assert.Contains(window.Rows, r => r.NewName == "A.txt" && r.State == "ok");
        await SettleAsync();
        await DesignShots.CaptureAsync(window, "multi-rename");

        window.Apply(new Core.Rename.MultiRenameOptions { NameMask = "same" });
        Assert.Contains(window.Rows, r => r.State == "warn" && r.Note == "same name twice");
    }
}
