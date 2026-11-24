using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.Core.Search;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Saved searches opened as folders of their results (Commands > Saved searches; user's request, 23 November 2026).</summary>
public sealed class SavedSearchFolderTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_saved_search_opens_as_a_folder_and_searches_again_on_refresh()
    {
        Directory.CreateDirectory(_fx.L("sub", "deep"));
        File.WriteAllText(_fx.L("sub", "deep", "notes.txt"), "x");
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_OpenSavedSearch");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("No search is saved yet", StringComparison.Ordinal));

        vm.Settings.SavedSearches["Texts"] = new SearchOptions { NameMask = "*.txt", SearchIn = _fx.L() };
        await vm.ExecuteAsync("nc_OpenSavedSearch texts");
        var panel = vm.Left;
        Assert.True(panel.IsVirtual);
        Assert.Equal("Search: Texts", panel.VirtualTitle);
        var names = panel.Items.Where(i => !i.IsParent).Select(i => i.FullName).ToList();
        Assert.Contains("a.txt", names);
        Assert.Contains(Path.Combine("sub", "deep", "notes.txt"), names);

        File.WriteAllText(_fx.L("new.txt"), "y"); // found when the folder is read again
        await panel.RefreshAsync();
        Assert.Contains("new.txt", panel.Items.Select(i => i.FullName));

        await vm.ExecuteAsync("nc_OpenSavedSearch Missing");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("Missing", StringComparison.Ordinal));

        w.FillSavedSearchesMenu();
        var menu = w.FindControl<MenuItem>("SavedSearchesMenu")!;
        var item = menu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Texts");
        Assert.Contains("noloc", item.Classes);
    }

    [AvaloniaFact]
    public async Task A_search_without_a_place_looks_in_the_panels_folder()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.SavedSearches["Here"] = new SearchOptions { NameMask = "*.txt", MaxDepth = 0 };
        _fx.Dialogs.QuickListHandler = items => new Services.QuickListResult(items.Single(i => i.Text == "Here"), -1);
        await vm.ExecuteAsync("nc_OpenSavedSearch");
        Assert.Equal("Search: Here", vm.Left.VirtualTitle);
        Assert.Contains("a.txt", vm.Left.Items.Select(i => i.FullName));
        Assert.Equal("*.txt in the panel's folder", ViewModels.MainWindowViewModel.SearchDetail(vm.Settings.SavedSearches["Here"]));
    }
}
