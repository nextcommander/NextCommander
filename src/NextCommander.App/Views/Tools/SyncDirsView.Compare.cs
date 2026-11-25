using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// Synchronize directories as the folder compare (the compare and merge plan of 25 November 2026, phase 4; the user chose to
/// merge the two): how files are found the same (date and size, their bytes, or as text with the compare settings), a
/// list without folders, comparisons kept under a name, the result as an HTML page or a CSV file, and the file compare
/// window going from one different file to the next, its saves updating the rows.
/// </summary>
public sealed partial class SyncDirsView
{
    private readonly ComboBox _method = new()
    {
        ItemsSource = new[] { "Date and size", "Date and size, then the bytes", "Content (bytes)", "Text (compare settings)" },
        SelectedIndex = 0,
        MinWidth = 210,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private readonly ToggleButton _flat = Chip("Flat list", "MiColumns");
    private readonly Button _presets = new() { Classes = { "dialog" } };
    private readonly Button _export = new() { Classes = { "dialog" } };

    /// <summary>The row whose files the compare window shows (for its next / previous file).</summary>
    private SyncRow? _compared;

    /// <summary>The method of the selector (and <see cref="SyncOptions.ByContent"/> for the second entry).</summary>
    private (SyncCompareMethod Method, bool ByContent) MethodChoice => _method.SelectedIndex switch
    {
        1 => (SyncCompareMethod.DateAndSize, true),
        2 => (SyncCompareMethod.Content, false),
        3 => (SyncCompareMethod.Text, false),
        _ => (SyncCompareMethod.DateAndSize, false),
    };

    private static int MethodIndex(SyncOptions options) => options.Method switch
    {
        SyncCompareMethod.Content => 2,
        SyncCompareMethod.Text => 3,
        _ => options.ByContent ? 1 : 0,
    };

    /// <summary>The method of comparing as a choice (tests: 0 date and size ... 3 text).</summary>
    public int MethodSelection
    {
        get => _method.SelectedIndex;
        set => _method.SelectedIndex = value;
    }

    /// <summary>Files listed without their folders (tests).</summary>
    public bool FlatList
    {
        get => _flat.IsChecked == true;
        set => _flat.IsChecked = value;
    }

    /// <summary>The options shown by the controls.</summary>
    private void ShowOptions(SyncOptions o)
    {
        _subdirs.IsChecked = o.Subdirectories;
        _method.SelectedIndex = MethodIndex(o);
        _ignoreDate.IsChecked = o.IgnoreDate;
        _asymmetric.IsChecked = o.Asymmetric;
        _skipHidden.IsChecked = o.SkipHidden;
        _mask.Text = o.Mask;
    }

    private DirectoryComparer Comparer(SyncOptions options) =>
        new(_context.Registry, options, _context.Settings.CompareOptions.ToOptions());

    // ---------------------------------------------------------------- presets

    private void OpenPresetsMenu()
    {
        var menu = new ContextMenu();
        foreach (var preset in _context.Settings.SyncPresets)
        {
            var item = new MenuItem { Header = preset.Name, Icon = MenuIcons.Create("sync.preset") };
            item.Classes.Add("noloc"); // the user's own names
            var chosen = preset;
            item.Click += async (_, _) => await LoadPresetAsync(chosen.Name);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "Save as preset...", Icon = MenuIcons.Create("sync.savepreset") };
        save.Click += async (_, _) =>
        {
            var name = await _context.Dialogs.PromptAsync("Folder compare", "Name of the preset (the two folders and the options):", Path.GetFileName(_leftPath.Text?.TrimEnd('/', '\\')) ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(name))
            {
                SavePreset(name.Trim());
            }
        };
        menu.Items.Add(save);
        if (_context.Settings.SyncPresets.Count > 0)
        {
            var delete = new MenuItem { Header = "Delete preset", Icon = MenuIcons.Create("sync.deletepreset") };
            foreach (var preset in _context.Settings.SyncPresets)
            {
                var item = new MenuItem { Header = preset.Name };
                item.Classes.Add("noloc");
                var chosen = preset;
                item.Click += (_, _) => _context.Settings.SyncPresets.Remove(chosen);
                delete.Items.Add(item);
            }
            menu.Items.Add(delete);
        }
        if (!_context.Settings.ShowMenuIcons)
        {
            MenuIcons.Clear(menu.Items);
        }
        menu.Open(_presets);
    }

    /// <summary>The two folders and the options kept under a name (one of the same name is replaced).</summary>
    public void SavePreset(string name)
    {
        var presets = _context.Settings.SyncPresets;
        presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        presets.Add(new SyncPreset { Name = name, Left = _leftPath.Text ?? string.Empty, Right = _rightPath.Text ?? string.Empty, Options = ReadOptions() });
        presets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        _status.Text = $"Saved the preset {name}.";
    }

    /// <summary>A preset's folders and options, compared at once.</summary>
    public async Task LoadPresetAsync(string name)
    {
        if (_context.Settings.SyncPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)) is not { } preset)
        {
            return;
        }
        _loading = true;
        try
        {
            _leftPath.Text = preset.Left;
            _rightPath.Text = preset.Right;
            ShowOptions(preset.Options);
        }
        finally
        {
            _loading = false;
        }
        await CompareAsync();
    }

    /// <summary>Set while the controls are filled from a preset (their changes do not compare one by one).</summary>
    private bool _loading;

    // ---------------------------------------------------------------- export

    private void OpenExportMenu()
    {
        var menu = new ContextMenu();
        var html = new MenuItem { Header = "HTML report...", Icon = MenuIcons.Create("sync.exporthtml"), IsEnabled = _all2.Count > 0 };
        html.Click += async (_, _) => await ExportAsync(csv: false);
        var csv = new MenuItem { Header = "CSV file...", Icon = MenuIcons.Create("sync.exportcsv"), IsEnabled = _all2.Count > 0 };
        csv.Click += async (_, _) => await ExportAsync(csv: true);
        var patch = new MenuItem { Header = "Patch of the different files...", Icon = MenuIcons.Create("sync.exportpatch"), IsEnabled = _all2.Count > 0 };
        patch.Click += async (_, _) => await ExportPatchAsync();
        menu.Items.Add(html);
        menu.Items.Add(csv);
        menu.Items.Add(patch);
        if (!_context.Settings.ShowMenuIcons)
        {
            MenuIcons.Clear(menu.Items);
        }
        menu.Open(_export);
    }

    /// <summary>The rows shown now (the status filters) as a report.</summary>
    public string Report(bool csv)
    {
        var items = _all2.Where(r => Shown(CategoryOf(r.Item.Status))).Select(r => r.Item).ToList();
        return csv ? SyncReport.Csv(items) : SyncReport.Html(items, _leftPath.Text ?? string.Empty, _rightPath.Text ?? string.Empty, DateTime.Now);
    }

    private async Task ExportAsync(bool csv)
    {
        var path = await _context.Dialogs.PickSaveFileAsync(csv ? "Save the comparison as CSV" : "Save the comparison as HTML", csv ? "folder-compare.csv" : "folder-compare.html");
        if (path is null)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, Report(csv), new System.Text.UTF8Encoding(csv));
            _status.Text = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    /// <summary>
    /// One patch for the files shown that differ (the left folder the old state, the right one the new): changed files,
    /// files only on the right (new) and only on the left (deleted); binary files are named, not patched.
    /// </summary>
    public async Task<string> PatchTextAsync()
    {
        var patch = new System.Text.StringBuilder();
        var rows = _all2.Where(r => !r.Item.IsDirectory && r.Item.Status != SyncStatus.Equal && Shown(CategoryOf(r.Item.Status)));
        foreach (var item in rows.Select(r => r.Item))
        {
            var oldText = item.Left is null ? null : await ReadTextAsync(item.Left);
            var newText = item.Right is null ? null : await ReadTextAsync(item.Right);
            if ((item.Left is not null && oldText is null) || (item.Right is not null && newText is null))
            {
                patch.Append("Binary files a/").Append(item.RelativePath).Append(" and b/").Append(item.RelativePath).Append(" differ\n");
                continue;
            }
            var old = oldText ?? Core.Compare.TextContent.Empty();
            var @new = newText ?? Core.Compare.TextContent.Empty();
            patch.Append(UnifiedDiff.Create(item.Left is null ? "/dev/null" : "a/" + item.RelativePath, item.Right is null ? "/dev/null" : "b/" + item.RelativePath,
                old.Lines, @new.Lines, context: 3, new DiffOptions { MatchSimilarLines = false }, old.EndsWithNewline, @new.EndsWithNewline));
        }
        return patch.ToString();
    }

    /// <summary>A file's text; null for a binary file.</summary>
    private async Task<Core.Compare.TextContent?> ReadTextAsync(VfsEntry entry)
    {
        await using var stream = await _context.Registry.Resolve(entry.Path).OpenReadAsync(entry.Path, CancellationToken.None);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var data = memory.ToArray();
        return Core.Text.EncodingDetector.LooksBinary(data.AsSpan(0, Math.Min(data.Length, 8192))) ? null : Core.Compare.TextContent.Read(data);
    }

    private async Task ExportPatchAsync()
    {
        if (await _context.Dialogs.PickSaveFileAsync("Save the differences as a patch", "folders.patch") is not { } path)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, await PatchTextAsync(), new System.Text.UTF8Encoding(false));
            _status.Text = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
        }
    }

    // ---------------------------------------------------------------- the file compare window

    /// <summary>Opens the compare window for a row's two files; it goes on to the other different files and its saves update the rows.</summary>
    private void OpenCompare(SyncRow row)
    {
        if (row.Item is not { Left: { IsDirectory: false } l, Right: { IsDirectory: false } r })
        {
            return;
        }
        _compared = row;
        var window = new CompareWindow(l, r, _context) { Neighbor = NeighborAsync };
        window.Saved += async (_, _) => await RefreshComparedAsync(window);
        window.Show((Window)TopLevel.GetTopLevel(this)!);
    }

    /// <summary>The pairs of different files in the order shown.</summary>
    private List<SyncRow> DifferentPairs() =>
        [.. _all2.Where(r => r.Item is { Left: { IsDirectory: false }, Right: { IsDirectory: false } } && r.Item.Status != SyncStatus.Equal && Shown(CategoryOf(r.Item.Status)))];

    /// <summary>The next (+1) or previous (-1) different pair after the one compared; null at the end.</summary>
    internal Task<(VfsEntry Left, VfsEntry Right)?> NeighborAsync(int direction)
    {
        var pairs = DifferentPairs();
        var index = _compared is null ? -1 : pairs.IndexOf(_compared);
        var next = index < 0 ? (direction > 0 ? 0 : pairs.Count - 1) : index + direction;
        if (next < 0 || next >= pairs.Count)
        {
            return Task.FromResult<(VfsEntry, VfsEntry)?>(null);
        }
        _compared = pairs[next];
        _list.SelectedItem = Rows.FirstOrDefault(n => n.Row == _compared);
        return Task.FromResult<(VfsEntry, VfsEntry)?>((_compared.Item.Left!, _compared.Item.Right!));
    }

    /// <summary>The compare window saved its files: their row compared again.</summary>
    private async Task RefreshComparedAsync(CompareWindow window)
    {
        var row = _all2.FirstOrDefault(r => r.Item.Left?.Path == window.LeftEntry.Path && r.Item.Right?.Path == window.RightEntry.Path);
        if (row is null)
        {
            return;
        }
        row.Item.Left = window.LeftEntry;
        row.Item.Right = window.RightEntry;
        var comparer = Comparer(ReadOptions());
        try
        {
            row.Item.Status = await comparer.StatusOfAsync(row.Item.Left, row.Item.Right);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = ex.Message;
            return;
        }
        row.SetAction(comparer.ActionFor(row.Item.Status));
        Render();
    }

    /// <summary>Opens the compare window for a row (tests).</summary>
    internal CompareWindow? CompareRow(SyncRow row)
    {
        OpenCompare(row);
        return (TopLevel.GetTopLevel(this) as Window)?.OwnedWindows.OfType<CompareWindow>().LastOrDefault();
    }
}
