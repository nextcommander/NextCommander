using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace NextCommander.App.Views.Dialogs;

/// <summary>What a field of a list entry looks like in <see cref="ListEditor{T}"/>.</summary>
public enum ListFieldKind
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>A color (#RRGGBB) with a swatch; must be set.</summary>
    Color,

    /// <summary>A whole number; empty = none.</summary>
    Number,

    /// <summary>A check box ("True" / "False").</summary>
    Bool,

    /// <summary>A program path with a "..." button; the options are offered as quick choices (internal, system ...).</summary>
    Program,

    /// <summary>One of <see cref="ListField{T}.Options"/> (value, shown text).</summary>
    Choice,
}

/// <summary>A field of a list entry: label, kind and how its text is read and written.</summary>
public sealed record ListField<T>(string Label, ListFieldKind Kind, Func<T, string?> Get, Action<T, string?> Set)
{
    /// <summary>Grey hint in an empty box.</summary>
    public string? Watermark { get; init; }

    /// <summary>Choice: the values with their texts; Program: quick values (e.g. "internal", "system") with their texts.</summary>
    public IReadOnlyList<(string Value, string Text)> Options { get; init; } = [];

    /// <summary>Shown only for some entries (e.g. not for a separator).</summary>
    public Func<T, bool>? IsVisibleFor { get; init; }

    /// <summary>Program: a "Command..." button chooses a nc_ / uc_ command from a list.</summary>
    public bool PickCommand { get; init; }
}

/// <summary>How <see cref="ListEditor{T}"/> shows and makes the entries of one list.</summary>
public sealed class ListEditorOptions<T>
{
    public required Func<T> Create { get; init; }

    /// <summary>One line describing an entry in the list.</summary>
    public required Func<T, string> Summary { get; init; }

    public required IReadOnlyList<ListField<T>> Fields { get; init; }

    /// <summary>A copy of an entry (the editor never changes the list it was given).</summary>
    public required Func<T, T> Clone { get; init; }

    /// <summary>What is wrong with an entry, or null.</summary>
    public Func<T, string?>? Validate { get; init; }

    /// <summary>Colors shown before an entry's text (e.g. the light and dark color of a color rule).</summary>
    public Func<T, IReadOnlyList<string?>>? Swatches { get; init; }

    /// <summary>When set, an "Add separator" button adds this entry.</summary>
    public Func<T>? CreateSeparator { get; init; }

    /// <summary>Text of the add button ("Add rule", "Add program" ...).</summary>
    public string AddText { get; init; } = "Add";

    /// <summary>Picks a program file for <see cref="ListFieldKind.Program"/> fields (title → path, null when cancelled).</summary>
    public Func<string, Task<string?>>? PickProgram { get; init; }
}

/// <summary>
/// A settings list edited with controls instead of JSON: the entries on the left (add, copy, remove, move up / down),
/// the fields of the selected one on the right. Works on a copy of the list and reports every change with the whole new
/// list. An entry with an error is marked and the editor carries <see cref="SettingsWindow.InvalidJsonClass"/> (OK /
/// Apply refuse it) until it is corrected.
/// </summary>
public sealed class ListEditor<T> : UserControl where T : class
{
    private readonly ListEditorOptions<T> _options;
    private readonly Action<List<T>> _changed;
    private readonly List<T> _items;
    private readonly ListBox _list = new() { Height = 200, SelectionMode = SelectionMode.Single };
    private readonly Grid _form = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 6, ColumnSpacing = 10 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _empty = new() { Text = "Add an entry or select one in the list.", Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
    private readonly Button _remove = new() { Content = "Remove" };
    private readonly Button _copy = new() { Content = "Copy" };
    private readonly Button _up = new() { Content = "▲" };
    private readonly Button _down = new() { Content = "▼" };
    private bool _loading;

    public ListEditor(IEnumerable<T> items, ListEditorOptions<T> options, Action<List<T>> changed)
    {
        _options = options;
        _changed = changed;
        _items = items.Select(options.Clone).ToList();

        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => RowView(row));
        _list.SelectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                ShowForm();
            }
        };
        ToolTip.SetTip(_up, "Move up");
        ToolTip.SetTip(_down, "Move down");

        var add = new Button { Content = options.AddText };
        add.Click += (_, _) => Add(options.Create());
        _copy.Click += (_, _) =>
        {
            if (Selected is { } entry)
            {
                Add(options.Clone(entry), SelectedIndex + 1);
            }
        };
        _remove.Click += (_, _) => Remove();
        _up.Click += (_, _) => Move(-1);
        _down.Click += (_, _) => Move(1);
        var buttons = new WrapPanel { ItemSpacing = 6, LineSpacing = 4 };
        buttons.Children.Add(add);
        if (options.CreateSeparator is { } separator)
        {
            var addSeparator = new Button { Content = "Add separator" };
            addSeparator.Click += (_, _) => Add(separator());
            buttons.Children.Add(addSeparator);
        }
        buttons.Children.AddRange([_copy, _remove, _up, _down]);

        var left = new DockPanel { MinWidth = 220 };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 6, 0, 0);
        left.Children.AddRange([buttons, _list]);

        var right = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 0, 0) };
        right.Children.AddRange([_empty, _form, _error]);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*") };
        Grid.SetColumn(right, 1);
        grid.Children.AddRange([left, right]);
        Content = grid;

        Refresh(select: _items.Count > 0 ? 0 : -1);
    }

    /// <summary>The entries as they are now.</summary>
    public IReadOnlyList<T> Items => _items;

    public int SelectedIndex
    {
        get => _list.SelectedIndex;
        set => _list.SelectedIndex = value;
    }

    public T? Selected => SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;

    /// <summary>The error shown under the fields ("Entry 2: Mask is empty"), or empty.</summary>
    public string ErrorText => _error.Text ?? string.Empty;

    /// <summary>The edit box of a field of the selected entry (tests).</summary>
    public Control? FieldEditor(string label) =>
        _form.Children.FirstOrDefault(c => Equals(c.Tag, label) && Grid.GetColumn(c) == 1);

    public void Add(T entry, int? at = null)
    {
        var index = Math.Clamp(at ?? _items.Count, 0, _items.Count);
        _items.Insert(index, entry);
        Changed(select: index);
    }

    public void Remove()
    {
        if (Selected is null)
        {
            return;
        }
        var index = SelectedIndex;
        _items.RemoveAt(index);
        Changed(select: Math.Min(index, _items.Count - 1));
    }

    public void Move(int direction)
    {
        var index = SelectedIndex;
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _items.Count)
        {
            return;
        }
        (_items[index], _items[target]) = (_items[target], _items[index]);
        Changed(select: target);
    }

    // ---------------------------------------------------------------- list

    /// <summary>A line of the list (the entry with its number; recreated when anything changes).</summary>
    private sealed record Row(int Number, T Entry, string Text, bool Invalid);

    private Control RowView(Row? row)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (row is null)
        {
            return panel;
        }
        foreach (var color in _options.Swatches?.Invoke(row.Entry) ?? [])
        {
            if (color is not null && Color.TryParse(color, out var c))
            {
                panel.Children.Add(new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center,
                    BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
                });
            }
        }
        var text = new TextBlock
        {
            Text = row.Text,
            Classes = { "noloc" },
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (row.Invalid)
        {
            text.Foreground = Brushes.IndianRed; // otherwise the list's own text color
        }
        panel.Children.Add(text);
        return panel;
    }

    private void Refresh(int select)
    {
        _loading = true;
        _list.ItemsSource = _items.Select((entry, i) => new Row(i + 1, entry, _options.Summary(entry), _options.Validate?.Invoke(entry) is not null)).ToList();
        _list.SelectedIndex = select;
        _loading = false;
        ShowForm();
        UpdateButtons();
        Validate();
    }

    /// <summary>The selected line's text follows the fields as they are typed (the form keeps its focus).</summary>
    private void RefreshSelectedRow()
    {
        if (Selected is not { } entry || _list.ItemsSource is not List<Row> rows)
        {
            return;
        }
        var index = SelectedIndex;
        rows[index] = rows[index] with { Text = _options.Summary(entry), Invalid = _options.Validate?.Invoke(entry) is not null };
        _loading = true;
        _list.ItemsSource = rows.ToList();
        _list.SelectedIndex = index;
        _loading = false;
    }

    private void UpdateButtons()
    {
        var index = SelectedIndex;
        _remove.IsEnabled = _copy.IsEnabled = index >= 0;
        _up.IsEnabled = index > 0;
        _down.IsEnabled = index >= 0 && index < _items.Count - 1;
    }

    private void Changed(int select)
    {
        Refresh(select);
        _changed(_items.ToList());
    }

    private void Validate()
    {
        var problem = _items.Select((entry, i) => (i, Problem: _options.Validate?.Invoke(entry))).FirstOrDefault(p => p.Problem is not null);
        if (problem.Problem is null)
        {
            _error.Text = string.Empty;
            Classes.Remove(SettingsWindow.InvalidJsonClass);
            return;
        }
        _error.Text = string.Format(CultureInfo.CurrentCulture, "Entry {0}: {1}", problem.i + 1, problem.Problem);
        Classes.Add(SettingsWindow.InvalidJsonClass);
    }

    // ---------------------------------------------------------------- fields of the selected entry

    private void ShowForm()
    {
        _form.Children.Clear();
        _form.RowDefinitions.Clear();
        UpdateButtons();
        var entry = Selected;
        _empty.IsVisible = entry is null;
        if (entry is null)
        {
            return;
        }
        var row = 0;
        foreach (var field in _options.Fields)
        {
            if (field.IsVisibleFor?.Invoke(entry) == false)
            {
                continue;
            }
            _form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center, Tag = field.Label };
            var editor = Editor(entry, field);
            editor.Tag = field.Label;
            Grid.SetRow(label, row);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            _form.Children.Add(label);
            _form.Children.Add(editor);
            row++;
        }
    }

    /// <summary>A field was edited: the entry, the line of the list, the error and the settings follow.</summary>
    private void FieldChanged()
    {
        RefreshSelectedRow();
        Validate();
        _changed(_items.ToList());
    }

    private Control Editor(T entry, ListField<T> field)
    {
        var value = field.Get(entry) ?? string.Empty;
        switch (field.Kind)
        {
            case ListFieldKind.Bool:
            {
                var box = new CheckBox { IsChecked = bool.TryParse(value, out var b) && b };
                box.IsCheckedChanged += (_, _) =>
                {
                    field.Set(entry, (box.IsChecked == true).ToString(CultureInfo.InvariantCulture));
                    FieldChanged();
                };
                return box;
            }
            case ListFieldKind.Choice:
            {
                var combo = new ComboBox { ItemsSource = field.Options.Select(o => o.Text).ToList(), MinWidth = 200 };
                combo.SelectedIndex = Math.Max(0, field.Options.ToList().FindIndex(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)));
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0)
                    {
                        field.Set(entry, field.Options[combo.SelectedIndex].Value);
                        FieldChanged();
                    }
                };
                return combo;
            }
            case ListFieldKind.Color:
            {
                var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)) };
                var box = new TextBox { Text = value, Width = 110, Watermark = field.Watermark ?? "#RRGGBB", Classes = { "noloc" }, VerticalAlignment = VerticalAlignment.Center };
                void Paint() => swatch.Background = Color.TryParse(box.Text, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
                Paint();
                box.TextChanged += (_, _) =>
                {
                    field.Set(entry, box.Text);
                    Paint();
                    FieldChanged();
                };
                var palette = new WrapPanel { ItemSpacing = 2, LineSpacing = 2, VerticalAlignment = VerticalAlignment.Center };
                foreach (var quick in s_quickColors)
                {
                    var pick = new Button
                    {
                        Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(2),
                        Background = new SolidColorBrush(Color.Parse(quick)),
                    };
                    ToolTip.SetTip(pick, quick);
                    pick.Click += (_, _) => box.Text = quick;
                    palette.Children.Add(pick);
                }
                // the quick colors wrap in what is left of the row
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
                Grid.SetColumn(box, 1);
                Grid.SetColumn(palette, 2);
                line.Children.AddRange([swatch, box, palette]);
                return line;
            }
            case ListFieldKind.Program:
            {
                var box = new TextBox { Text = value, Watermark = field.Watermark, Classes = { "noloc" } };
                box.TextChanged += (_, _) =>
                {
                    field.Set(entry, box.Text);
                    FieldChanged();
                };
                var line = new DockPanel();
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(tools, Dock.Right);
                if (_options.PickProgram is { } pick)
                {
                    var browse = new Button { Content = "..." };
                    ToolTip.SetTip(browse, "Choose a program");
                    browse.Click += async (_, _) =>
                    {
                        if (await pick(field.Label) is { } path)
                        {
                            box.Text = path;
                        }
                    };
                    tools.Children.Add(browse);
                }
                if (field.PickCommand)
                {
                    tools.Children.Add(CommandPickerDialog.ButtonFor(box));
                }
                foreach (var (quickValue, text) in field.Options)
                {
                    var quick = new Button { Content = text };
                    ToolTip.SetTip(quick, quickValue);
                    quick.Click += (_, _) => box.Text = quickValue;
                    tools.Children.Add(quick);
                }
                line.Children.AddRange([tools, box]);
                return line;
            }
            default:
            {
                var box = new TextBox { Text = value, Watermark = field.Watermark, Classes = { "noloc" } };
                if (field.Kind == ListFieldKind.Number)
                {
                    box.Width = 110;
                    box.HorizontalAlignment = HorizontalAlignment.Left;
                }
                box.TextChanged += (_, _) =>
                {
                    field.Set(entry, box.Text);
                    FieldChanged();
                };
                return box;
            }
        }
    }

    private static readonly string[] s_quickColors =
    [
        "#000000", "#FFFFFF", "#C62828", "#E65100", "#F9A825", "#2E7D32", "#00838F", "#1565C0", "#6A1B9A", "#8E24AA",
        "#EF9A9A", "#FFB74D", "#FFF176", "#81C784", "#80DEEA", "#90CAF9", "#CE93D8", "#9E9E9E",
    ];
}
