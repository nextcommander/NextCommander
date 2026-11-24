using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using NextCommander.Core.Tags;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Tags and color labels in the panels (Mark > Tags; user's request, 23 November 2026).</summary>
[Collection("Appearance")]
public sealed class TagUiTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static ViewModels.FileItemViewModel Item(ViewModels.PanelViewModel panel, string name) => panel.Items.Single(i => i.FullName == name);

    [AvaloniaFact]
    public async Task A_tag_colors_the_name_and_the_filter_shows_only_tagged_files()
    {
        var (_, vm) = await _fx.OpenAsync();
        var panel = vm.Left;
        panel.CursorItem = Item(panel, "a.txt");
        await vm.ExecuteAsync("nc_Tag red");
        await SettleAsync();
        Assert.Equal(["Red"], vm.Tags.TagsOf(VfsPath.Local(_fx.L("a.txt"))));
        var red = Assert.IsAssignableFrom<ISolidColorBrush>(Item(panel, "a.txt").RuleBrush);
        Assert.Equal(Color.Parse("#E53935"), red.Color);

        vm.Settings.Tags[0].Color = "#112233"; // the label's color changed in the settings
        vm.ApplySettings();
        await SettleAsync();
        Assert.Equal(Color.Parse("#112233"), ((ISolidColorBrush)Item(panel, "a.txt").RuleBrush!).Color);

        await vm.ExecuteAsync("nc_TagFilter Red");
        Assert.Equal(["a.txt"], panel.Items.Where(i => !i.IsParent).Select(i => i.FullName));
        Assert.Equal("(tag: Red)", panel.FilterMaskText);
        await vm.ExecuteAsync("nc_TagFilter Red"); // again: all files
        Assert.True(panel.Items.Count(i => !i.IsParent) > 1);

        await vm.ExecuteAsync("nc_Tag Nonsense");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("Nonsense", StringComparison.Ordinal));
        await vm.ExecuteAsync("nc_Tag Red"); // the same tag again: off
        Assert.Empty(vm.Tags.TagsOf(VfsPath.Local(_fx.L("a.txt"))));
    }

    [AvaloniaFact]
    public async Task Tags_go_along_with_copying_renaming_and_a_renamed_tag()
    {
        var (w, vm) = await _fx.OpenAsync();
        var source = VfsPath.Local(_fx.L("a.txt"));
        vm.Tags.Toggle([source], "Blue");
        var job = new Core.Operations.CopyMoveJob(vm.Tools.Registry, [(await vm.Tools.Registry.Resolve(source).GetEntryAsync(source, CancellationToken.None))!],
            VfsPath.Local(_fx.R()), move: false);
        await job.RunAsync();
        vm.RecordUndo(job);
        Assert.Equal(["Blue"], vm.Tags.TagsOf(VfsPath.Local(_fx.R("a.txt"))));
        Assert.Equal(["Blue"], vm.Tags.TagsOf(source)); // a copy: both

        vm.Settings.Tags[4].Name = "Clients"; // Blue renamed in the settings
        vm.ApplySettings();
        Assert.Equal(["Clients"], vm.Tags.TagsOf(source));

        vm.Left.CursorItem = Item(vm.Left, "a.txt");
        w.FillTagsMenu();
        var menu = w.FindControl<MenuItem>("TagsMenu")!;
        var clients = menu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Clients");
        Assert.True(clients.IsChecked);
        Assert.Contains("noloc", clients.Classes);
        Assert.Contains(menu.Items.OfType<MenuItem>(), m => m.Header as string == "Edit tags...");
    }

    [AvaloniaFact]
    public void The_tag_column_shows_the_tags()
    {
        var before = TagStore.Current;
        try
        {
            var store = new TagStore(null);
            TagStore.Current = store;
            var columns = new TagColumns();
            var entry = new VfsEntry { Name = "x", Path = VfsPath.Local(Path.Combine(Path.GetTempPath(), "x")) };
            Assert.False(columns.Supports(entry)); // nothing tagged: no work
            store.Toggle([entry.Path], "Red");
            store.Toggle([entry.Path], "Work");
            Assert.True(columns.Supports(entry));
            Assert.Equal("Red, Work", columns.GetValuesAsync(entry, ["tags.names"], null!, CancellationToken.None).Result["tags.names"]);
        }
        finally
        {
            TagStore.Current = before;
        }
    }
}
