using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Synchronize directories as the folder compare (the compare and merge plan, phase 4; 25 November 2026).</summary>
public sealed class FolderCompareTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Methods_list_presets_report_and_the_file_compare_go_together()
    {
        var (_, vm) = await _fx.OpenAsync();
        var time = new DateTime(2024, 5, 1, 12, 0, 0);
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            File.SetLastWriteTime(path, time);
        }
        Write(_fx.L("src", "ends.txt"), "a\r\nb\r\n");
        Write(_fx.R("src", "ends.txt"), "a\nb\n");
        Write(_fx.L("src", "one.txt"), "first\n");
        Write(_fx.R("src", "one.txt"), "FIRST\n");
        Write(_fx.L("two.txt"), "left\n");
        Write(_fx.R("two.txt"), "right\n");

        var view = new SyncDirsView(VfsPath.Local(_fx.L()), VfsPath.Local(_fx.R()), vm.Tools);
        _fx.Track(new Avalonia.Controls.Window { Content = view, Width = 1180, Height = 780 }).Show();
        await view.CompareAsync();
        SyncStatus StatusOf(string path) => view.AllRows.Single(r => r.Item.RelativePath == path).Item.Status;
        Assert.Equal(SyncStatus.Different, StatusOf("src/ends.txt"));

        // as text with the compare settings: other line ends are the same text; case counts unless ignored
        view.MethodSelection = 3;
        await view.CompareAsync();
        Assert.Equal(SyncStatus.Equal, StatusOf("src/ends.txt"));
        Assert.Equal(SyncStatus.Different, StatusOf("src/one.txt"));
        vm.Settings.CompareOptions.IgnoreCase = true;
        await view.CompareAsync();
        Assert.Equal(SyncStatus.Equal, StatusOf("src/one.txt"));
        vm.Settings.CompareOptions.IgnoreCase = false;
        await view.CompareAsync();

        // the list without folders: paths in full
        view.FlatList = true;
        Assert.Contains(view.Rows, n => n.Name == "src/one.txt" && !n.IsDirectory);
        Assert.DoesNotContain(view.Rows, n => n.IsDirectory);

        // a preset keeps the folders and the options
        view.SavePreset("Project");
        view.MethodSelection = 0;
        await view.LoadPresetAsync("Project");
        Assert.Equal(3, view.MethodSelection);
        Assert.Equal(SyncCompareMethod.Text, Assert.Single(vm.Settings.SyncPresets).Options.Method);

        // the report of the rows shown
        Assert.Contains("src/one.txt,Different", view.Report(csv: true), StringComparison.Ordinal);
        Assert.Contains("<h1>Folder compare</h1>", view.Report(csv: false), StringComparison.Ordinal);

        // the file compare goes from one different file to the next; its save updates the row
        var first = view.AllRows.Single(r => r.Item.RelativePath == "src/one.txt");
        var window = _fx.Track(view.CompareRow(first)!);
        window.AskSaveOnClose = _ => Task.FromResult(SaveChoice.Discard);
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });
        Assert.Equal("one.txt", window.LeftEntry.Name);
        await window.CopyAllAsync(toRight: true);
        Assert.True(await window.SaveSideAsync(right: true));
        await WaitUntilAsync(() => StatusOf("src/one.txt") == SyncStatus.Equal);

        File.WriteAllText(_fx.R("src", "one.txt"), "changed again\n"); // still listed as different until compared
        Assert.True(await window.OtherFileAsync(+1));
        Assert.Equal("two.txt", window.LeftEntry.Name);
        Assert.False(await window.OtherFileAsync(+1)); // the last one
    }
}
