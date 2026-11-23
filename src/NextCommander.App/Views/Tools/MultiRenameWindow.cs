using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Services.Design;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Operations;
using NextCommander.Core.Rename;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>Preview row of the multi-rename tool.</summary>
public sealed class RenameRow(RenamePlanItem item, int number = 0)
{
    public RenamePlanItem Item { get; } = item;

    /// <summary>The row's number in the list (1, 2, ...).</summary>
    public int Number { get; } = number;

    public string OldName => Item.Source.Name;

    public string NewName => Item.NewName;

    public string Note => Item.Issue switch
    {
        RenameIssue.Duplicate => "same name twice",
        RenameIssue.Exists => "this name already exists",
        RenameIssue.Invalid => "invalid name",
        _ => Item.Changes ? "will rename" : "unchanged",
    };

    /// <summary>The color of the state: "ok" renamed, "warn" twice, "bad" exists / invalid, "" unchanged.</summary>
    public string State => Item.Issue switch
    {
        RenameIssue.Duplicate => "warn",
        RenameIssue.Exists or RenameIssue.Invalid => "bad",
        _ => Item.Changes ? "ok" : string.Empty,
    };

    /// <summary>The brush key of the state's color.</summary>
    public string StateBrush => State switch
    {
        "ok" => "NcOkBrush",
        "warn" => "NcWarnBrush",
        "bad" => "NcBadBrush",
        _ => "NcFaintBrush",
    };

    /// <summary>Whether the row is shown with "Only changed": a new name or a problem.</summary>
    public bool Changed => Item.Changes || Item.Issue != RenameIssue.None;
}

/// <summary>Ctrl+M: multi-rename tool with live preview, presets and undo (laid out after the template).</summary>
public sealed class MultiRenameWindow : Window
{
    private static readonly (string Text, string Insert)[] s_placeholders =
    [
        ("[N] name", "[N]"), ("[N1-3] characters 1-3 of the name", "[N1-3]"), ("[E] extension", "[E]"), ("[C] counter", "[C]"),
        ("[YMD] date", "[YMD]"), ("[hms] time", "[hms]"), ("[P] parent folder", "[P]"), ("[G] grandparent folder", "[G]"), ("[S] size", "[S]"),
    ];

    /// <summary>The letter case buttons, in the template's order, and the mode of each.</summary>
    private static readonly (string Text, CaseMode Mode)[] s_cases =
    [
        ("Unchanged", CaseMode.Unchanged), ("lowercase", CaseMode.Lower), ("UPPERCASE", CaseMode.Upper),
        ("Title Case", CaseMode.Title), ("First letter", CaseMode.FirstUpper),
    ];

    private readonly ToolContext _context;
    private readonly Func<Task> _refresh;
    private readonly TextBox _nameMask = new() { Text = "[N]", Classes = { "nc-mono", "noloc" } };
    private readonly TextBox _extMask = new() { Text = "[E]", Classes = { "nc-mono", "noloc" } };
    private readonly TextBox _search = new() { PlaceholderText = "Text, or a|b for several", Classes = { "nc-mono", "noloc" } };
    private readonly TextBox _replace = new() { PlaceholderText = "Leave empty to remove", Classes = { "nc-mono", "noloc" } };
    private readonly ToggleButton _regex = Nc.Chip("Regex");
    private readonly ToggleButton _caseSensitive = Nc.Chip("Case sensitive");
    private readonly ToggleButton _firstOnly = Nc.Chip("First only");
    private readonly ToggleButton _includeExt = Nc.Chip("Include extension");
    private readonly RadioButton[] _cases;
    private readonly Border _caseBox;
    private readonly NcNumber _start = new(1, -1_000_000, 1_000_000_000);
    private readonly NcNumber _step = new(1, -1000, 1000);
    private readonly NcNumber _digits = new(1, 1, 12);
    private readonly ComboBox _presets = new() { MinWidth = 200, PlaceholderText = "None", Classes = { "nc-select", "sm" } };
    private readonly ListBox _list = new() { Classes = { "nc-table" } };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _run = Nc.Button("Rename", "primary", "Ctrl+Enter");
    private readonly Button _undo = Nc.Button("Undo", "ghost", icon: Nc.Icons.Undo);
    private readonly ToggleButton _onlyChanged = Nc.Chip("Only changed");
    private readonly StackPanel _counts = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _maskSample = new() { Classes = { "nc-hint", "nc-mono", "noloc" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
    private readonly TextBlock _counterSample = new() { Classes = { "nc-hint", "nc-mono", "noloc" } };
    private readonly TextBlock _findError = new() { FontSize = 12, Classes = { "noloc" } };
    private List<VfsEntry> _entries;
    private HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
    private MultiRenameJob? _lastJob;
    private bool _loading;

    public MultiRenameWindow(IReadOnlyList<VfsEntry> entries, ToolContext context, Func<Task> refresh)
    {
        _entries = [.. entries];
        _context = context;
        _refresh = refresh;
        Title = $"Multi-Rename Tool — {entries.Count} items";
        Width = 1080;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Nc.Apply(this);

        (_caseBox, _cases) = Nc.Segmented([.. s_cases.Select(c => c.Text)], 0);
        _run.IsDefault = true;
        _undo.IsEnabled = false;
        _list.ItemsSource = Rows;
        _list.ItemTemplate = new FuncDataTemplate<RenameRow>((row, _) => PreviewRow(row));
        _findError[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcBadBrush");

        foreach (var box in new[] { _nameMask, _extMask, _search, _replace })
        {
            box.TextChanged += (_, _) => Recompute();
        }
        foreach (var check in new[] { _regex, _caseSensitive, _firstOnly, _includeExt })
        {
            check.IsCheckedChanged += (_, _) => Recompute();
        }
        for (var i = 0; i < _cases.Length; i++)
        {
            var index = i;
            var button = _cases[i];
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                {
                    // the button just checked, not the first checked one: the one checked before is unchecked only
                    // after this event (clicking UPPERCASE applied the previous choice; user's report, 23 November 2026)
                    _caseIndex = index;
                    Recompute();
                }
            };
        }
        _start.ValueChanged += (_, _) => Recompute();
        _step.ValueChanged += (_, _) => Recompute();
        _digits.ValueChanged += (_, _) => Recompute();
        _onlyChanged.IsCheckedChanged += (_, _) => ShowRows();
        _presets.SelectionChanged += (_, _) =>
        {
            if (_presets.SelectedItem is string name && _context.Settings.RenamePresets.TryGetValue(name, out var preset))
            {
                Apply(preset);
            }
        };
        _run.Click += async (_, _) => await RunAsync();
        _undo.Click += async (_, _) => await UndoAsync();

        Content = BuildLayout();
        RefreshPresets();
        Opened += async (_, _) =>
        {
            await LoadExistingNamesAsync();
            Recompute();
            _nameMask.Focus();
            _nameMask.SelectAll();
        };
    }

    public ObservableCollection<RenameRow> Rows { get; } = [];

    public string StatusText => _status.Text ?? string.Empty;

    private int _caseIndex;

    /// <summary>The chosen letter case.</summary>
    private CaseMode CaseChoice
    {
        get => s_cases[_caseIndex].Mode;
        set
        {
            _caseIndex = Math.Max(0, Array.FindIndex(s_cases, c => c.Mode == value));
            _cases[_caseIndex].IsChecked = true;
        }
    }

    private Control BuildLayout()
    {
        Button PlaceholderButton(TextBox target, string name)
        {
            var button = new Button { Content = new TextBlock { Text = "[…]", Classes = { "noloc" } }, Classes = { "nc-tail" } };
            button.Classes.Add("nc-mono");
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            var flyout = new MenuFlyout();
            foreach (var (text, insert) in s_placeholders)
            {
                var item = new MenuItem { Header = text };
                item.Click += (_, _) =>
                {
                    var caret = target.CaretIndex;
                    var current = target.Text ?? string.Empty;
                    target.Text = current.Insert(Math.Clamp(caret, 0, current.Length), insert);
                    target.CaretIndex = caret + insert.Length;
                    target.Focus();
                };
                flyout.Items.Add(item);
            }
            button.Flyout = flyout;
            return button;
        }
        static Grid Columns(string columns, params Control[] cells)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 10 };
            for (var i = 0; i < cells.Length; i++)
            {
                Grid.SetColumn(cells[i], i);
                grid.Children.Add(cells[i]);
            }
            return grid;
        }
        static WrapPanel Chips(params Control[] chips)
        {
            var panel = new WrapPanel();
            foreach (var chip in chips)
            {
                chip.Margin = new Thickness(0, 0, 6, 6);
                panel.Children.Add(chip);
            }
            return panel;
        }

        // the rules
        var mask = Nc.Card(_maskSample, "Mask",
            Columns("*,132",
                Nc.Field("Name", Nc.Combo(_nameMask, null, PlaceholderButton(_nameMask, "Insert placeholder into name"))),
                Nc.Field("Extension", Nc.Combo(_extMask, null, PlaceholderButton(_extMask, "Insert placeholder into extension")))));
        var find = Nc.Card(_findError, "Find & replace",
            Columns("*,*", Nc.Field("Find", Nc.Combo(_search, null)), Nc.Field("Replace with", Nc.Combo(_replace, null))),
            Chips(_regex, _caseSensitive, _firstOnly, _includeExt));
        var letterCase = Nc.Card("Letter case", _caseBox);
        var counter = Nc.Card(_counterSample, "Counter",
            Columns("*,*,*", Nc.Field("Start", _start), Nc.Field("Step", _step), Nc.Field("Digits", _digits)));
        var row1 = Columns("*,*", mask, find);
        row1.ColumnSpacing = 16;
        var row2 = Columns("2*,*", letterCase, counter);
        row2.ColumnSpacing = 16;

        // saved settings
        var save = Nc.Button("Save...");
        save.Click += async (_, _) => await SavePresetAsync();
        var delete = Nc.Button("Delete", "ghost");
        delete.IsEnabled = false;
        _presets.SelectionChanged += (_, _) => delete.IsEnabled = _presets.SelectedItem is string;
        delete.Click += (_, _) =>
        {
            if (_presets.SelectedItem is string name)
            {
                _context.Settings.RenamePresets.Remove(name);
                RefreshPresets();
            }
        };
        var reset = Nc.Button("Reset rules", "ghost", icon: Nc.Icons.Reset);
        reset.Click += (_, _) =>
        {
            Apply(new MultiRenameOptions());
            _presets.SelectedItem = null;
        };
        var presets = new DockPanel();
        DockPanel.SetDock(reset, Dock.Right);
        presets.Children.Add(reset);
        presets.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "Saved settings", Classes = { "nc-label" }, VerticalAlignment = VerticalAlignment.Center }, _presets, save, delete },
        });

        var rules = new StackPanel { Spacing = 14, Margin = new Thickness(16, 14, 16, 16), Children = { row1, row2, presets } };

        // the preview: counts, Only changed, the table
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { new TextBlock { Text = "Preview", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _counts },
        };
        var tools = new DockPanel { Margin = new Thickness(16, 10) };
        DockPanel.SetDock(_onlyChanged, Dock.Right);
        tools.Children.AddRange([_onlyChanged, title]);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(PreviewColumns), Margin = new Thickness(16, 0) };
        var heads = new[] { "#", "Current name", string.Empty, "New name", "Status" };
        for (var i = 0; i < heads.Length; i++)
        {
            var cell = new TextBlock { Text = heads[i], Classes = { "nc-section" }, Padding = new Thickness(10, 8), TextAlignment = i == 0 ? TextAlignment.Right : TextAlignment.Left };
            Grid.SetColumn(cell, i);
            head.Children.Add(cell);
        }
        var headBox = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBox[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcLineBrush");
        _list.Padding = new Thickness(16, 0);
        var previewTop = new StackPanel { Children = { tools, headBox } };
        var preview = new DockPanel();
        DockPanel.SetDock(previewTop, Dock.Top);
        preview.Children.AddRange([previewTop, _list]);
        var previewBox = new Border { Child = preview, BorderThickness = new Thickness(0, 1, 0, 0) };
        previewBox[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcSurface2Brush");
        previewBox[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcLineBrush");

        // the footer: the summary, Undo, Close, Rename
        var close = Nc.Button("Close");
        close.Click += (_, _) => Close();
        var footer = Nc.Footer([_status], _undo, close, _run);
        footer.Padding = new Thickness(16, 12);
        footer[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcSurfaceBrush");
        _status[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcMutedBrush");

        var root = new DockPanel();
        DockPanel.SetDock(rules, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([rules, footer, previewBox]);
        return root;
    }

    /// <summary>The columns of the preview: number, current name, arrow, new name, status.</summary>
    private const string PreviewColumns = "52,*,30,*,140";

    /// <summary>A preview row; the changed part of the new name is marked, the state has its color.</summary>
    private static Control PreviewRow(RenameRow? row)
    {
        if (row is null)
        {
            return new Panel();
        }
        TextBlock Cell(string text, string brush, int column, bool right = false)
        {
            var cell = new TextBlock
            {
                Text = text,
                Padding = new Thickness(10, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "noloc" },
            };
            cell[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(brush);
            Grid.SetColumn(cell, column);
            return cell;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(PreviewColumns) };
        var newName = Cell(string.Empty, row.Item.Issue == RenameIssue.None ? row.Item.Changes ? "NcTextBrush" : "NcFaintBrush" : "NcBadBrush", 3);
        newName.Text = null;
        MarkChange(newName, row.OldName, row.NewName);
        ToolTip.SetTip(newName, row.NewName);
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        dot[!Avalonia.Controls.Shapes.Shape.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(row.StateBrush);
        var note = new TextBlock { Text = row.Note, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        note[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(row.StateBrush);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0), Children = { dot, note } };
        Grid.SetColumn(status, 4);
        grid.Children.AddRange([
            Cell(row.Number.ToString(System.Globalization.CultureInfo.CurrentCulture), "NcFaintBrush", 0, right: true),
            Cell(row.OldName, "NcMutedBrush", 1),
            Cell("→", "NcFaintBrush", 2),
            newName,
            status,
        ]);
        return grid;
    }

    /// <summary>The new name with the part that differs from the old one marked (the same start and end stay plain).</summary>
    private static void MarkChange(TextBlock block, string oldName, string newName)
    {
        if (oldName == newName)
        {
            block.Text = newName;
            return;
        }
        var start = 0;
        while (start < oldName.Length && start < newName.Length && oldName[start] == newName[start])
        {
            start++;
        }
        var end = 0;
        while (end < oldName.Length - start && end < newName.Length - start && oldName[^(end + 1)] == newName[^(end + 1)])
        {
            end++;
        }
        block.Inlines ??= [];
        block.Inlines.Add(new Avalonia.Controls.Documents.Run(newName[..start]));
        var middle = newName[start..(newName.Length - end)];
        if (middle.Length > 0)
        {
            var mark = new Avalonia.Controls.Documents.Run(middle) { FontWeight = FontWeight.SemiBold };
            mark[!Avalonia.Controls.Documents.TextElement.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcAccentSoftBrush");
            block.Inlines.Add(mark);
        }
        block.Inlines.Add(new Avalonia.Controls.Documents.Run(newName[(newName.Length - end)..]));
    }

    /// <summary>The list shows all rows, or with Only changed the renamed ones and those with a problem.</summary>
    private void ShowRows() => _list.ItemsSource = _onlyChanged.IsChecked == true ? Rows.Where(r => r.Changed).ToList() : Rows;

    /// <summary>Current settings of the window.</summary>
    public MultiRenameOptions Options => new()
    {
        NameMask = _nameMask.Text ?? string.Empty,
        ExtensionMask = _extMask.Text ?? string.Empty,
        Search = string.IsNullOrEmpty(_search.Text) ? null : _search.Text,
        Replace = _replace.Text,
        SearchIsRegex = _regex.IsChecked == true,
        SearchCaseSensitive = _caseSensitive.IsChecked == true,
        ReplaceFirstOnly = _firstOnly.IsChecked == true,
        SearchIncludesExtension = _includeExt.IsChecked == true,
        Case = CaseChoice,
        CounterStart = (long)(_start.Value ?? 1),
        CounterStep = (long)(_step.Value ?? 1),
        CounterDigits = (int)(_digits.Value ?? 1),
    };

    public void Apply(MultiRenameOptions o)
    {
        _loading = true;
        _nameMask.Text = o.NameMask;
        _extMask.Text = o.ExtensionMask;
        _search.Text = o.Search;
        _replace.Text = o.Replace;
        _regex.IsChecked = o.SearchIsRegex;
        _caseSensitive.IsChecked = o.SearchCaseSensitive;
        _firstOnly.IsChecked = o.ReplaceFirstOnly;
        _includeExt.IsChecked = o.SearchIncludesExtension;
        CaseChoice = o.Case;
        _start.Value = o.CounterStart;
        _step.Value = o.CounterStep;
        _digits.Value = o.CounterDigits;
        _loading = false;
        Recompute();
    }

    /// <summary>Names in the folders that are not renamed (conflict check).</summary>
    private async Task LoadExistingNamesAsync()
    {
        var keys = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var dir in _entries.Select(e => e.Path.Parent).OfType<VfsPath>().Distinct())
        {
            try
            {
                foreach (var e in await _context.Registry.Resolve(dir).ListAsync(dir, CancellationToken.None))
                {
                    keys.Add(MultiRename.Key(e, e.Name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // conflicts in this folder are found when renaming
            }
        }
        _existing = keys;
    }

    public IReadOnlyList<RenamePlanItem> Plan { get; private set; } = [];

    private void Recompute()
    {
        if (_loading)
        {
            return;
        }
        try
        {
            Plan = MultiRename.Plan(_entries, Options, _existing);
        }
        catch (ArgumentException ex)
        {
            _status.Text = "Regex error: " + ex.Message;
            _findError.Text = Core.Localization.Localizer.T("Regex is not valid");
            _run.IsEnabled = false;
            return;
        }
        _findError.Text = null;
        Rows.Clear();
        var number = 1;
        foreach (var item in Plan)
        {
            Rows.Add(new RenameRow(item, number++));
        }
        ShowRows();
        ShowCounts();
        _maskSample.Text = Plan.Count > 0 ? "→ " + Plan[0].NewName : string.Empty;
        var o = Options;
        _counterSample.Text = string.Join(", ", Enumerable.Range(0, 3).Select(i =>
            (o.CounterStart + i * o.CounterStep).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(Math.Max(1, o.CounterDigits), '0'))) + " …";
        var changes = Plan.Count(p => p.Changes && p.Issue == RenameIssue.None);
        var problems = Plan.Count(p => p.Issue != RenameIssue.None);
        _status.Text = problems > 0
            ? $"{changes} items will be renamed; {problems} items have problems (these will be skipped)."
            : $"{changes} items will be renamed.";
        _run.IsEnabled = changes > 0;
    }

    /// <summary>The counts above the preview: to rename, unchanged, duplicate, invalid (or existing).</summary>
    private void ShowCounts()
    {
        _counts.Children.Clear();
        void Add(int count, string text, string kind)
        {
            if (count > 0 || kind is "ok" or "")
            {
                var label = new TextBlock { Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, Core.Localization.Localizer.T(text), count), Classes = { "noloc" } };
                _counts.Children.Add(Nc.Count(label, kind));
            }
        }
        Add(Rows.Count(r => r.State == "ok"), "{0} to rename", "ok");
        Add(Rows.Count(r => r.State.Length == 0), "{0} unchanged", "");
        Add(Rows.Count(r => r.State == "warn"), "{0} duplicate", "warn");
        Add(Rows.Count(r => r.State == "bad"), "{0} invalid", "bad");
    }

    public async Task RunAsync()
    {
        var job = new MultiRenameJob(_context.Registry, Plan) { Interaction = new UiJobInteraction(_context.Dialogs) };
        await job.RunAsync();
        await AfterJobAsync(job);
        _lastJob = job;
        _undo.IsEnabled = job.Done.Count > 0;
    }

    public async Task UndoAsync()
    {
        if (_lastJob is null)
        {
            return;
        }
        var undo = _lastJob.CreateUndo();
        undo.Interaction = new UiJobInteraction(_context.Dialogs);
        await undo.RunAsync();
        await AfterJobAsync(undo);
        _lastJob = null;
        _undo.IsEnabled = false;
    }

    /// <summary>Follows the renamed files so the preview keeps working on them.</summary>
    private async Task AfterJobAsync(MultiRenameJob job)
    {
        var moved = job.Done.ToDictionary(d => d.From, d => d.To);
        var updated = new List<VfsEntry>();
        foreach (var entry in _entries)
        {
            var path = moved.GetValueOrDefault(entry.Path, entry.Path);
            if (await _context.Registry.Resolve(path).GetEntryAsync(path, CancellationToken.None) is { } fresh)
            {
                updated.Add(fresh);
            }
        }
        _entries = updated;
        await LoadExistingNamesAsync();
        Recompute();
        if (job.Errors.Count > 0)
        {
            _status.Text = $"{job.Done.Count} items renamed, {job.Errors.Count} errors: {job.Errors[0].Item}: {job.Errors[0].Message}";
        }
        else
        {
            _status.Text = $"{job.Done.Count} items renamed." + (job.Done.Count > 0 ? " Use \"Undo\" to revert." : string.Empty);
        }
        await _refresh();
    }

    private async Task SavePresetAsync()
    {
        var dialog = new InputDialog("Save settings", "Name:", _presets.SelectedItem as string ?? string.Empty, false);
        await dialog.ShowDialog(this);
        if (string.IsNullOrWhiteSpace(dialog.Result))
        {
            return;
        }
        _context.Settings.RenamePresets[dialog.Result.Trim()] = Options;
        RefreshPresets();
        _presets.SelectedItem = dialog.Result.Trim();
    }

    private void RefreshPresets() =>
        _presets.ItemsSource = _context.Settings.RenamePresets.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _run.IsEnabled)
        {
            _ = RunAsync();
            e.Handled = true;
            return;
        }
        if (!e.Handled && e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
