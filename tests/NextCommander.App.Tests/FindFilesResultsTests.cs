using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Views.Search;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The result list of Find files: icons by kind, the context menu, deleting and exporting.</summary>
public sealed class FindFilesResultsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<FindFilesWindow> SearchAsync(string mask = "*")
    {
        var (_, vm) = await _fx.OpenAsync();
        var find = _fx.Track(new FindFilesWindow(new FindFilesRequest(vm.Tools, _fx.L(), null, false, vm.FeedSearchResultsAsync, vm.GoToFileAsync)));
        find.Show();
        find.SetFields(mask, _fx.L());
        await find.StartAsync();
        await SettleAsync(4);
        return find;
    }

    [AvaloniaFact]
    public async Task Results_show_an_icon_for_files_and_folders()
    {
        var find = await SearchAsync();
        Assert.Contains(find.ResultEntries, e => e.IsDirectory);
        // one icon per visible row (files and the "sub" folder alike)
        var rows = find.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.NotEmpty(row.GetVisualDescendants().OfType<Viewbox>()));
    }

    [AvaloniaFact]
    public async Task Context_menu_has_icons_and_follows_the_selection()
    {
        var find = await SearchAsync();
        var file = find.ResultEntries.First(e => e.Name == "a.txt");
        var folder = find.ResultEntries.First(e => e.IsDirectory);
        var menu = find.ResultMenu;

        find.Select(file);
        menu.Open();
        await SettleAsync(4);
        var items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header!);
        Assert.All(items.Values, i => Assert.True(i.Icon is Control, $"no icon: {i.Header}"));
        Assert.True(items["Open"].IsEnabled);
        Assert.True(items["View"].IsEnabled);
        Assert.True(items["Show in the system file manager"].IsEnabled);
        menu.Close();

        find.Select(folder);
        menu.Open();
        await SettleAsync(4);
        Assert.False(items["View"].IsEnabled); // a folder is not viewed or edited
        Assert.False(items["Edit"].IsEnabled);
        Assert.True(items["Go to the file in the panel"].IsEnabled);
        menu.Close();

        find.Select(file, folder);
        menu.Open();
        await SettleAsync(4);
        Assert.False(items["Open"].IsEnabled); // one at a time
        Assert.True(items["Delete (to the trash)"].IsEnabled);
        Assert.True(items["Copy the full path"].IsEnabled);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_and_removing_take_results_out_of_the_list()
    {
        var find = await SearchAsync("*.txt");
        find.ConfirmDelete = false;
        var a = find.ResultEntries.First(e => e.Name == "a.txt");
        var b = find.ResultEntries.First(e => e.Name == "file2.txt");
        find.Select(a);
        await find.DeleteSelectedAsync(useTrash: false);
        Assert.False(File.Exists(_fx.L("a.txt")));
        Assert.DoesNotContain(find.ResultEntries, e => e.Name == "a.txt");

        find.RemoveFromList([b]);
        Assert.DoesNotContain(find.ResultEntries, e => e.Name == "file2.txt");
        Assert.True(File.Exists(_fx.L("file2.txt"))); // only out of the list
    }

    [AvaloniaFact]
    public async Task Results_are_exported_as_a_path_list_or_a_table()
    {
        var find = await SearchAsync("*.txt");
        var list = Path.Combine(_fx.Root, "results.txt");
        find.ExportTo(list, FindFilesWindow.ExportFormat.PathList);
        var paths = File.ReadAllLines(list);
        Assert.Equal(find.ResultEntries.Count, paths.Length);
        Assert.Contains(_fx.L("sub", "inner.txt"), paths);

        var table = Path.Combine(_fx.Root, "results.csv");
        find.ExportTo(table, FindFilesWindow.ExportFormat.Csv);
        var bytes = File.ReadAllBytes(table);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // UTF-8 with BOM for spreadsheets
        var lines = File.ReadAllLines(table);
        var separator = CultureInfo.CurrentCulture.TextInfo.ListSeparator.Trim() is { Length: > 0 } s ? s : ",";
        Assert.Equal(string.Join(separator, "Path", "Name", "Folder", "Type", "Size", "Modified"), lines[0]);
        Assert.Equal(find.ResultEntries.Count + 1, lines.Length);
        var inner = lines.Single(l => l.Contains("inner.txt", StringComparison.Ordinal)).Split(separator);
        Assert.Equal(["inner.txt", _fx.L("sub"), "File", "1"], inner[1..5]);
    }
}
