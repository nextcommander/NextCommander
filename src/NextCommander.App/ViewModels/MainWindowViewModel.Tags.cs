using NextCommander.App.Services;
using NextCommander.Core.Commands;
using NextCommander.Core.Operations;
using NextCommander.Core.Tags;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Tags and color labels of files (Mark > Tags; user's request, 23 November 2026): a tag on the selected files, their
/// names in the tag's color, a "Tags" column for the custom column sets and a filter by tag.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The tags of the files (tags.json in the settings folder).</summary>
    public TagStore Tags { get; }

    /// <summary>The names of the tags when the settings were applied last: a tag renamed there is renamed on the files.</summary>
    private List<string> _tagNames;

    private bool _tagRefreshPosted;

    private void RegisterTagCommands(CommandRegistry c)
    {
        c.Register("nc_Tag", "Tag the selected files (parameter: the tag; again: the tag comes off)", ToggleTagAsync);
        c.Register("nc_Tags", "Tags of the selected files: a list of the tags to put on or take off", _ => ChooseTagAsync());
        c.Register("nc_TagFilter", "Show only the files with a tag (parameter: the tag; none: a list)", FilterByTagAsync);
        c.Register("nc_ClearTags", "Take every tag off the selected files", ClearTags);
        c.Register("nc_EditTags", "Edit the tags and their colors", _ => ShowSettingsAsync("Tags"));
        Tags.Changed += (_, _) => RefreshTagsLater();
    }

    /// <summary>The panels show the tags again (once, after the changes of one action).</summary>
    private void RefreshTagsLater()
    {
        if (_tagRefreshPosted)
        {
            return;
        }
        _tagRefreshPosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _tagRefreshPosted = false;
            foreach (var tab in LeftSide.Tabs.Concat(RightSide.Tabs))
            {
                tab.Reapply();
            }
        });
    }

    /// <summary>The paths of the selected files and folders (or the one under the cursor).</summary>
    private List<VfsPath> SelectedPaths() => [.. ActivePanel.GetSelectedEntries().Select(e => e.Path)];

    private async Task ToggleTagAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            await ChooseTagAsync();
            return;
        }
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }
        var known = _settings.Tags.FirstOrDefault(t => string.Equals(t.Name, tag.Trim(), StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            await _dialogs.ShowMessageAsync("Tags", $"There is no tag named \"{tag.Trim()}\". Mark > Tags > Edit tags... adds one.");
            return;
        }
        Tags.Toggle(paths, known.Name);
    }

    /// <summary>The tags with their colors; the ones all the selected files have are marked; Enter puts one on or takes it off.</summary>
    private async Task ChooseTagAsync()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            return;
        }
        if (_settings.Tags.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Tags", "There are no tags. Mark > Tags > Edit tags... adds them.");
            return;
        }
        var items = _settings.Tags.Select(t => new QuickListItem(t.Name,
            paths.All(p => Tags.TagsOf(p).Contains(t.Name, StringComparer.OrdinalIgnoreCase)) ? "✓" : null, t)).ToList();
        var result = await _dialogs.ShowQuickListAsync("Tags", items);
        if (result?.Item.Value is TagDefinition chosen)
        {
            Tags.Toggle(paths, chosen.Name);
        }
    }

    private void ClearTags()
    {
        var paths = SelectedPaths();
        if (paths.Count > 0)
        {
            Tags.Clear(paths);
        }
    }

    private async Task FilterByTagAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            var items = new List<QuickListItem> { new("All files", null, string.Empty) };
            items.AddRange(_settings.Tags.Select(t => new QuickListItem(t.Name, string.Equals(t.Name, ActivePanel.TagFilter, StringComparison.OrdinalIgnoreCase) ? "✓" : null, t.Name)));
            var result = await _dialogs.ShowQuickListAsync("Show only the files with a tag", items);
            if (result?.Item.Value is not string chosen)
            {
                return;
            }
            tag = chosen;
        }
        ActivePanel.TagFilter = string.Equals(ActivePanel.TagFilter, tag, StringComparison.OrdinalIgnoreCase) ? null : tag;
    }

    /// <summary>A copy, move or multi-rename that finished: the tags go with the files.</summary>
    private void CarryTags(FileJob job)
    {
        if (Tags.IsEmpty)
        {
            return;
        }
        switch (job)
        {
            case CopyMoveJob copy:
                foreach (var done in copy.Done.Where(d => d.Kind is CopyMoveDoneKind.File or CopyMoveDoneKind.Directory))
                {
                    if (copy.IsMove && !done.KeptSource)
                    {
                        Tags.Moved(done.Source, done.Target);
                    }
                    else
                    {
                        Tags.Copied(done.Source, done.Target);
                    }
                }
                break;
            case Core.Rename.MultiRenameJob rename:
                foreach (var (from, to) in rename.Done)
                {
                    Tags.Moved(from, to);
                }
                break;
        }
    }

    /// <summary>After the settings: a tag renamed there (same place in the list) is renamed on the files too.</summary>
    private void ApplyTagSettings()
    {
        var names = _settings.Tags.Select(t => t.Name).ToList();
        if (names.Count == _tagNames.Count)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (!string.Equals(names[i], _tagNames[i], StringComparison.Ordinal) && names[i].Length > 0)
                {
                    Tags.RenameTag(_tagNames[i], names[i]);
                }
            }
        }
        _tagNames = names;
        RefreshTagsLater(); // a color may have changed
    }
}
