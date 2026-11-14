using Avalonia.Input.Platform;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Search;

/// <summary>The result list: icons by file kind, the context menu (open, show the folder, view, edit, copy the path, delete ...) and export.</summary>
public sealed partial class FindFilesWindow
{
    /// <summary>The export formats: a list of paths (also for LOADLIST) or a table for a spreadsheet.</summary>
    public enum ExportFormat
    {
        PathList,
        Csv,
    }

    /// <summary>Asks before deleting (tests switch it off).</summary>
    public bool ConfirmDelete { get; set; } = true;

    /// <summary>The selected results (files and folders, not group headers).</summary>
    public IReadOnlyList<VfsEntry> SelectedEntries =>
        [.. (_results.SelectedItems?.OfType<SearchResultItem>() ?? []).Where(r => r.Entry is not null).Select(r => r.Entry!)];

    /// <summary>Selects results (tests).</summary>
    public void Select(params VfsEntry[] entries)
    {
        _results.SelectedItems?.Clear();
        foreach (var item in Results.Where(r => r.Entry is not null && entries.Any(e => e.Path == r.Entry.Path)))
        {
            _results.SelectedItems?.Add(item);
        }
    }

    /// <summary>The context menu of the result list (tests).</summary>
    public ContextMenu ResultMenu => _results.ContextMenu!;

    /// <summary>A row: the icon of the file kind, the path, size and date.</summary>
    private static Control ResultRow(SearchResultItem? item)
    {
        if (item is null)
        {
            return new Panel();
        }
        var name = new TextBlock { Text = item.Display, FontWeight = item.Weight, TextTrimming = TextTrimming.LeadingCharacterEllipsis, Classes = { "noloc" } };
        var detail = new TextBlock { Text = item.Detail, Opacity = 0.7, Margin = new Thickness(12, 0, 0, 0), FontSize = 12, Classes = { "noloc" } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (item.Entry is { } entry && FileKinds.GeometryFor(FileKinds.Classify(entry)) is { } glyph)
        {
            var icon = new Viewbox
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = IconPacks.Draw(glyph, FileKinds.BrushFor(FileKinds.Classify(entry))),
            };
            grid.Children.Add(icon);
        }
        Grid.SetColumn(name, 1);
        Grid.SetColumn(detail, 2);
        grid.Children.AddRange([name, detail]);
        if (item.Hit.Length == 0)
        {
            return grid;
        }
        // a content search: the line of the first match under the name
        grid.RowDefinitions = new RowDefinitions("Auto,Auto");
        var hit = new TextBlock
        {
            Text = item.Hit, Opacity = 0.75, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" },
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
        };
        Grid.SetRow(hit, 1);
        Grid.SetColumn(hit, 1);
        Grid.SetColumnSpan(hit, 2);
        grid.Children.Add(hit);
        return grid;
    }

    // ---------------------------------------------------------------- context menu

    private readonly Dictionary<MenuItem, string> _menuIcons = [];

    private ContextMenu BuildResultMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string icon, Func<IReadOnlyList<VfsEntry>, bool> enabled, Func<Task> action, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture), Tag = enabled };
            item.Click += async (_, _) => await action();
            _menuIcons[item] = icon;
            menu.Items.Add(item);
            return item;
        }
        static bool One(IReadOnlyList<VfsEntry> s) => s.Count == 1;
        static bool OneLocal(IReadOnlyList<VfsEntry> s) => s.Count == 1 && s[0].Path.IsLocal;
        static bool OneFile(IReadOnlyList<VfsEntry> s) => s.Count == 1 && !s[0].IsDirectory;
        static bool Any(IReadOnlyList<VfsEntry> s) => s.Count > 0;

        Item("Open", "find.open", OneLocal, () => Run(e => _request.Context.Platform.OpenWithDefaultApp(e.Path.Path)), "Shift+Enter");
        Item("Go to the file in the panel", "find.goto", One, GoToSelectedAsync, "Enter");
        Item("Show in the system file manager", "nc_RevealInExplorer", OneLocal, () => Run(e => _request.Context.Platform.RevealInSystemFileManager(e.Path.Path)));
        menu.Items.Add(new Separator());
        Item("View", "nc_List", OneFile, () =>
        {
            ViewSelected();
            return Task.CompletedTask;
        }, "F3");
        Item("Edit", "nc_Edit", OneFile, () => SelectedEntries is [{ IsDirectory: false } entry] ? _request.Context.Edit(entry) : Task.CompletedTask, "F4");
        menu.Items.Add(new Separator());
        Item("Copy the full path", "nc_CopyFullNamesToClip", Any, () => CopyToClipboardAsync(fullPath: true), "Ctrl+C");
        Item("Copy the name", "nc_CopyNamesToClip", Any, () => CopyToClipboardAsync(fullPath: false));
        menu.Items.Add(new Separator());
        Item("Delete (to the trash)", "nc_Delete", Any, () => DeleteSelectedAsync(useTrash: true), "Delete");
        Item("Delete permanently", "find.deletepermanently", Any, () => DeleteSelectedAsync(useTrash: false), "Shift+Delete");
        Item("Remove from the list", "find.remove", Any, () =>
        {
            RemoveFromList(SelectedEntries);
            return Task.CompletedTask;
        });
        if (OperatingSystem.IsWindows())
        {
            menu.Items.Add(new Separator());
            Item("Properties", "nc_Properties", OneLocal, () => Run(e => _request.Context.Platform.ShowSystemProperties(e.Path.Path)), "Alt+Enter");
        }
        menu.Items.Add(new Separator());
        Item("Export the results...", "find.export", _ => Results.Any(r => r.Entry is not null), () => ExportAsync());

        menu.Opening += (_, _) => UpdateResultMenu(menu);
        menu.Opened += (_, _) => UpdateResultMenu(menu);
        return menu;
    }

    /// <summary>What the selection allows, and the icons (as the "icons in the menus" setting says).</summary>
    private void UpdateResultMenu(ContextMenu menu)
    {
        var selected = SelectedEntries;
        var show = _request.Context.Settings.ShowMenuIcons;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is Func<IReadOnlyList<VfsEntry>, bool> enabled)
            {
                item.IsEnabled = enabled(selected);
            }
            item.Icon = show && _menuIcons.TryGetValue(item, out var key) ? MenuIcons.Create(key) : null;
        }
    }

    private Task Run(Action<VfsEntry> action)
    {
        if (SelectedEntries is [var entry])
        {
            action(entry);
        }
        return Task.CompletedTask;
    }

    public async Task CopyToClipboardAsync(bool fullPath)
    {
        var lines = SelectedEntries.Select(e => fullPath ? e.Path.ToDisplayString() : e.Name).ToList();
        if (lines.Count > 0 && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
        }
    }

    /// <summary>Deletes the selected results (to the trash, or permanently) and takes them out of the list.</summary>
    public async Task DeleteSelectedAsync(bool useTrash)
    {
        var selected = SelectedEntries;
        if (selected.Count == 0)
        {
            return;
        }
        if (ConfirmDelete)
        {
            var what = selected.Count == 1 ? selected[0].Path.ToDisplayString() : string.Format(CultureInfo.CurrentCulture, "{0:N0} files and folders", selected.Count);
            var question = string.Format(CultureInfo.CurrentCulture, useTrash ? "Move to the trash?\n{0}" : "Delete permanently?\n{0}", what);
            var dialog = new ChoiceDialog<bool>(useTrash ? "Delete" : "Delete permanently", question, [(useTrash ? "Delete" : "Delete permanently", true), ("Cancel", false)],
                cancelValue: false, width: 520);
            await dialog.ShowDialog(this);
            if (!dialog.Result)
            {
                return;
            }
        }
        var job = new DeleteJob(_request.Context.Registry, selected, useTrash ? _request.Context.Platform : null);
        _status.Text = "Deleting...";
        await Task.Run(job.RunAsync);
        var failed = job.Errors.Select(e => e.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = selected.Where(e => !failed.Contains(e.Path.ToDisplayString())).ToList();
        RemoveFromList(deleted);
        _status.Text = failed.Count == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} deleted", deleted.Count)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} deleted, {1:N0} could not be deleted: {2}", deleted.Count, failed.Count, job.Errors[0].Message);
    }

    /// <summary>Takes entries out of the result list (a duplicate group left with one file loses its header too).</summary>
    public void RemoveFromList(IReadOnlyList<VfsEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }
        var gone = entries.Select(e => e.Path).ToHashSet();
        var index = _results.SelectedIndex;
        foreach (var item in Results.Where(r => r.Entry is not null && gone.Contains(r.Entry.Path)).ToList())
        {
            Results.Remove(item);
        }
        // headers without files under them
        for (var i = Results.Count - 1; i >= 0; i--)
        {
            if (Results[i].IsHeader && (i + 1 >= Results.Count || Results[i + 1].IsHeader))
            {
                Results.RemoveAt(i);
            }
        }
        if (Results.Count > 0)
        {
            _results.SelectedIndex = Math.Clamp(index, 0, Results.Count - 1);
        }
    }

    // ---------------------------------------------------------------- export

    /// <summary>Asks for a file and writes the results to it (the format follows the chosen type).</summary>
    public async Task ExportAsync()
    {
        if (!Results.Any(r => r.Entry is not null))
        {
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the results",
            SuggestedFileName = "search-results",
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("List of paths (*.txt)") { Patterns = ["*.txt", "*.lst"] },
                new FilePickerFileType("Table for a spreadsheet (*.csv)") { Patterns = ["*.csv"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }
        try
        {
            var format = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Csv : ExportFormat.PathList;
            ExportTo(path, format);
            _status.Text = string.Format(CultureInfo.CurrentCulture, "{0:N0} results exported: {1}", Results.Count(r => r.Entry is not null), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    /// <summary>
    /// Writes the results: a list of paths (one a line, folders ending with a separator; opened again with LOADLIST),
    /// or a table (path, name, folder, type, size, modified; the duplicate group when there are groups) for a spreadsheet.
    /// </summary>
    public void ExportTo(string path, ExportFormat format)
    {
        var lines = new List<string>();
        if (format == ExportFormat.PathList)
        {
            lines.AddRange(Results.Where(r => r.Entry is not null).Select(r => r.Display));
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return;
        }
        var separator = CultureInfo.CurrentCulture.TextInfo.ListSeparator.Trim() is { Length: > 0 } s ? s : ",";
        string Cell(string text) =>
            text.Contains(separator, StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)
                ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
        var grouped = Results.Any(r => r.IsHeader);
        var header = new List<string> { "Path", "Name", "Folder", "Type", "Size", "Modified" };
        if (grouped)
        {
            header.Add("Group");
        }
        lines.Add(string.Join(separator, header.Select(Cell)));
        var group = 0;
        foreach (var row in Results)
        {
            if (row.IsHeader)
            {
                group++;
                continue;
            }
            var entry = row.Entry!;
            var cells = new List<string>
            {
                entry.Path.ToDisplayString(),
                entry.Name,
                entry.Path.Parent?.ToDisplayString() ?? string.Empty,
                entry.IsDirectory ? "Folder" : "File",
                entry.IsDirectory ? string.Empty : entry.Size.ToString(CultureInfo.InvariantCulture),
                entry.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            };
            if (grouped)
            {
                cells.Add(group.ToString(CultureInfo.InvariantCulture));
            }
            lines.Add(string.Join(separator, cells.Select(Cell)));
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(true)); // the BOM tells spreadsheets it is UTF-8
    }
}
