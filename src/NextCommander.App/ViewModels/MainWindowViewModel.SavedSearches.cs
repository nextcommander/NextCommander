using NextCommander.App.Services;
using NextCommander.Core.Commands;
using NextCommander.Core.Search;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Saved searches as virtual folders (Commands > Saved searches; user's request, 23 November 2026): a search saved in
/// Find files opens in the active panel like a folder of its results, and is made again each time it is opened or
/// refreshed (Ctrl+R), so it always shows what matches now.
/// </summary>
public partial class MainWindowViewModel
{
    private void RegisterSavedSearchCommands(CommandRegistry c)
    {
        c.Register("nc_OpenSavedSearch", "Open a saved search as a folder of its results (parameter: its name; none: a list)", OpenSavedSearchAsync);
    }

    private async Task OpenSavedSearchAsync(string? name)
    {
        if (_settings.SavedSearches.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Saved searches", "No search is saved yet. Find files (Alt+F7) saves one with \"Save...\".");
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            var items = _settings.SavedSearches.Keys.Order(StringComparer.CurrentCultureIgnoreCase)
                .Select(n => new QuickListItem(n, SearchDetail(_settings.SavedSearches[n]), n)).ToList();
            var result = await _dialogs.ShowQuickListAsync("Saved searches", items);
            if (result?.Item.Value is not string chosen)
            {
                return;
            }
            name = chosen;
        }
        name = name.Trim().Trim('"');
        var key = _settings.SavedSearches.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            await _dialogs.ShowMessageAsync("Saved searches", $"Saved search not found: {name}");
            return;
        }
        await OpenSearchFolderAsync(key, _settings.SavedSearches[key]);
    }

    /// <summary>
    /// The results of <paramref name="preset"/> in the active panel under their common folder; the search runs again on
    /// every refresh. A search without a place looks in the panel's folder of now.
    /// </summary>
    internal async Task OpenSearchFolderAsync(string name, SearchOptions preset)
    {
        var options = preset.Clone();
        if (string.IsNullOrWhiteSpace(options.SearchIn) && ActivePanel.CurrentPath is { } dir)
        {
            options.SearchIn = dir.ToDisplayString();
        }
        var place = options.SearchIn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => VfsPath.TryParse(p, out var path) ? path : null).OfType<VfsPath>().ToList();
        try
        {
            _ = new FileSearcher(_registry, options);
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowMessageAsync("Saved searches", ex.Message); // a regular expression that is not one
            return;
        }
        async Task<IReadOnlyList<VfsEntry>> Search(CancellationToken cancellationToken)
        {
            var found = new List<VfsEntry>();
            await new FileSearcher(_registry, options).RunAsync(entry =>
            {
                lock (found)
                {
                    found.Add(entry);
                }
            }, cancellationToken);
            return found;
        }
        var results = await Search(CancellationToken.None);
        var baseDir = place.Count == 1 ? place[0] : CommonParent(results) ?? place.FirstOrDefault() ?? VfsPath.Local(_platform.HomeDirectory);
        IReadOnlyList<VfsEntry>? first = results;
        await ActivePanel.ShowVirtualListAsync($"Search: {name}", baseDir, async ct =>
        {
            // the first time: the results just found; every refresh: searched again
            if (Interlocked.Exchange(ref first, null) is { } ready)
            {
                return ready;
            }
            return await Search(ct);
        });
    }

    /// <summary>"*.pdf in D:\Docs, containing 'invoice'" for the list of saved searches.</summary>
    internal static string SearchDetail(SearchOptions options)
    {
        var where = string.IsNullOrWhiteSpace(options.SearchIn) ? "the panel's folder" : options.SearchIn;
        var text = string.IsNullOrEmpty(options.ContainingText) ? string.Empty : $", containing \"{options.ContainingText}\"";
        return $"{options.NameMask} in {where}{text}";
    }
}
