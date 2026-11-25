using NextCommander.App.Services;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Commands;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Tools > Merge files... (the compare and merge plan, phase 6; 25 November 2026): three marked files, one of them the common
/// ancestor, merged into one of the others; or the conflict markers of the file under the cursor resolved.
/// </summary>
public partial class MainWindowViewModel
{
    private void RegisterMergeCommands(CommandRegistry c) =>
        c.Register("nc_MergeFiles", "Merge files: three marked files (one of them the common ancestor), or a file with conflict markers", _ => MergeFilesAsync());

    private async Task MergeFilesAsync()
    {
        var marked = ActivePanel.Items.Where(i => i.IsMarked && i.Entry is { IsDirectory: false }).Select(i => i.Entry!).ToList();
        if (marked.Count == 3)
        {
            var choice = await _dialogs.ShowQuickListAsync("Which file is the common ancestor (base)?",
                [.. marked.Select(e => new QuickListItem(e.Name, e.Path.ToDisplayString(), e))]);
            if (choice?.Item.Value is not VfsEntry @base)
            {
                return;
            }
            var others = marked.Where(e => !ReferenceEquals(e, @base)).ToList();
            var output = others[0];
            if (!await _dialogs.ConfirmAsync("Merge files",
                $"The changes of {others[0].Name} and {others[1].Name} since {@base.Name} are merged; saving writes the result to {output.Name}.", "Merge"))
            {
                return;
            }
            await _dialogs.ShowMergeAsync(output, new MergeSources(@base, others[0], others[1]), Tools);
            await ActivePanel.RefreshAsync();
            return;
        }
        var single = marked.Count == 1 ? marked[0] : marked.Count == 0 && ActivePanel.CursorItem?.Entry is { IsDirectory: false } cursor ? cursor : null;
        if (single is not null)
        {
            TextContent text;
            try
            {
                text = await ReadTextAsync(single.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _dialogs.ShowMessageAsync("Merge files", ex.Message);
                return;
            }
            if (ConflictMarkers.Find(text.Lines).Count > 0)
            {
                await _dialogs.ShowMergeAsync(single, null, Tools);
                await ActivePanel.RefreshAsync();
                return;
            }
            await _dialogs.ShowMessageAsync("Merge files", $"{single.Name} has no conflict markers. Mark three files to merge them: the common ancestor and the two versions made from it.");
            return;
        }
        await _dialogs.ShowMessageAsync("Merge files", "Mark three files to merge them (the common ancestor and the two versions made from it), or place the cursor on a file with conflict markers.");
    }
}
