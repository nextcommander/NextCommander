using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Operations;
using NextCommander.Core.Rename;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>Preview row of the multi-rename tool.</summary>
public sealed class RenameRow(RenamePlanItem item)
{
    public RenamePlanItem Item { get; } = item;

    public string OldName => Item.Source.Name;

    public string NewName => Item.NewName;

    public string Note => Item.Issue switch
    {
        RenameIssue.Duplicate => "same name twice",
        RenameIssue.Exists => "this name already exists",
        RenameIssue.Invalid => "invalid name",
        _ => Item.Changes ? string.Empty : "unchanged",
    };

    public IBrush Foreground => Item.Issue != RenameIssue.None ? Brushes.IndianRed : Item.Changes ? Brushes.Transparent : Brushes.Gray;
}

/// <summary>Ctrl+M: multi-rename tool with live preview, presets and undo.</summary>
public sealed class MultiRenameWindow : Window
{
    private static readonly (string Text, string Insert)[] s_placeholders =
    [
        ("[N] ad", "[N]"), ("[N1-3] characters 1-3 of the name", "[N1-3]"), ("[E] extension", "[E]"), ("[C] counter", "[C]"),
        ("[YMD] date", "[YMD]"), ("[hms] time", "[hms]"), ("[P] parent folder", "[P]"), ("[G] grandparent folder", "[G]"), ("[S] size", "[S]"),
    ];

    private readonly ToolContext _context;
    private readonly Func<Task> _refresh;
    private readonly TextBox _nameMask = new() { Text = "[N]" };
    private readonly TextBox _extMask = new() { Text = "[E]", Width = 120 };
    private readonly TextBox _search = new() { PlaceholderText = "Search for (| for multiple)" };
    private readonly TextBox _replace = new() { PlaceholderText = "Replace with" };
    private readonly CheckBox _regex = new() { Content = "Regex" };
    private readonly CheckBox _caseSensitive = new() { Content = "Case sensitive" };
    private readonly CheckBox _firstOnly = new() { Content = "First only" };
    private readonly CheckBox _includeExt = new() { Content = "Include extension" };
    private readonly ComboBox _case = new() { MinWidth = 160 };
    private readonly NumericUpDown _start = new() { Value = 1, Minimum = -1_000_000, Maximum = 1_000_000_000, Width = 120, FormatString = "0" };
    private readonly NumericUpDown _step = new() { Value = 1, Minimum = -1000, Maximum = 1000, Width = 110, FormatString = "0" };
    private readonly NumericUpDown _digits = new() { Value = 1, Minimum = 1, Maximum = 12, Width = 100, FormatString = "0" };
    private readonly ComboBox _presets = new() { MinWidth = 180, PlaceholderText = "Saved settings" };
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(10, 4), FontSize = 12 };
    private readonly Button _run = new() { Content = "Rename", IsDefault = true };
    private readonly Button _undo = new() { Content = "Undo", IsEnabled = false, Classes = { "dialog" } };
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
        Width = 980;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _case.ItemsSource = new List<string> { "Replacement", "lowercase", "UPPERCASE", "First letter uppercase", "Every Word Capitalized" };
        _case.SelectedIndex = 0;
        _list.ItemsSource = Rows;
        _list.ItemTemplate = new FuncDataTemplate<RenameRow>((_, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*,140") };
            TextBlock Cell(string path, int column, double opacity = 1)
            {
                var t = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Opacity = opacity };
                t.Bind(TextBlock.TextProperty, new Binding(path));
                Grid.SetColumn(t, column);
                grid.Children.Add(t);
                return t;
            }
            Cell(nameof(RenameRow.OldName), 0, 0.75);
            grid.Children.Add(new TextBlock { Text = "→", Opacity = 0.5, [Grid.ColumnProperty] = 1 });
            Cell(nameof(RenameRow.NewName), 2);
            var note = Cell(nameof(RenameRow.Note), 3);
            note.Bind(TextBlock.ForegroundProperty, new Binding(nameof(RenameRow.Foreground)));
            note.FontSize = 12;
            return grid;
        });

        foreach (var control in new Control[] { _nameMask, _extMask, _search, _replace })
        {
            ((TextBox)control).TextChanged += (_, _) => Recompute();
        }
        foreach (var check in new[] { _regex, _caseSensitive, _firstOnly, _includeExt })
        {
            check.IsCheckedChanged += (_, _) => Recompute();
        }
        _case.SelectionChanged += (_, _) => Recompute();
        _start.ValueChanged += (_, _) => Recompute();
        _step.ValueChanged += (_, _) => Recompute();
        _digits.ValueChanged += (_, _) => Recompute();
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

    private Control BuildLayout()
    {
        static TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
        Button PlaceholderButton(TextBox target)
        {
            var button = new Button { Content = "[...]" };
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
        StackPanel Row(params Control[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.AddRange(children);
            return panel;
        }

        var masks = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        void At(Control c, int column)
        {
            Grid.SetColumn(c, column);
            masks.Children.Add(c);
        }
        At(Caption("Name mask:"), 0);
        At(_nameMask, 1);
        At(PlaceholderButton(_nameMask), 2);
        At(Caption("Extension:"), 3);
        At(_extMask, 4);
        At(PlaceholderButton(_extMask), 5);

        var replace = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), ColumnSpacing = 8 };
        replace.Children.Add(Caption("Find:"));
        Grid.SetColumn(_search, 1);
        replace.Children.Add(_search);
        var replaceCaption = Caption("Replace with:");
        Grid.SetColumn(replaceCaption, 2);
        replace.Children.Add(replaceCaption);
        Grid.SetColumn(_replace, 3);
        replace.Children.Add(_replace);

        var save = new Button { Content = "Save..." };
        save.Click += async (_, _) => await SavePresetAsync();
        var delete = new Button { Content = "Delete" };
        delete.Click += (_, _) =>
        {
            if (_presets.SelectedItem is string name)
            {
                _context.Settings.RenamePresets.Remove(name);
                RefreshPresets();
            }
        };
        var close = new Button { Content = "Close", Classes = { "dialog" } };
        close.Click += (_, _) => Close();

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        top.Children.Add(masks);
        top.Children.Add(replace);
        top.Children.Add(Row(_regex, _caseSensitive, _firstOnly, _includeExt));
        top.Children.Add(Row(Caption("Case:"), _case, Caption("Counter start:"), _start, Caption("step:"), _step, Caption("digits:"), _digits));
        top.Children.Add(Row(_presets, save, delete));
        var buttons = Row(_run, _undo, close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(10, 6);

        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(statusBar);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        return root;
    }

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
        Case = (CaseMode)Math.Max(0, _case.SelectedIndex),
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
        _case.SelectedIndex = (int)o.Case;
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
            _run.IsEnabled = false;
            return;
        }
        Rows.Clear();
        foreach (var item in Plan)
        {
            Rows.Add(new RenameRow(item));
        }
        var changes = Plan.Count(p => p.Changes && p.Issue == RenameIssue.None);
        var problems = Plan.Count(p => p.Issue != RenameIssue.None);
        _status.Text = problems > 0
            ? $"{changes} items will be renamed; {problems} items have problems (these will be skipped)."
            : $"{changes} items will be renamed.";
        _run.IsEnabled = changes > 0;
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
        if (!e.Handled && e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
