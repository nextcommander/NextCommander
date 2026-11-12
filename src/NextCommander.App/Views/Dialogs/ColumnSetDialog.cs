using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using NextCommander.Core.Columns;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Edits the custom column sets (view modes): columns, headers, widths, automatic folder mask.</summary>
public sealed class ColumnSetDialog : DialogWindow
{
    private readonly List<ColumnSet> _sets;
    private readonly IReadOnlyList<ColumnField> _fields;
    private readonly ListBox _setList = new() { Height = 120 };
    private readonly TextBox _name = new();
    private readonly TextBox _mask = new() { Watermark = "Automatic: folder path mask (*Pictures*;*Resimler*)" };
    private readonly TextBox _background = new() { Watermark = "List background (#RRGGBB, empty = normal)", Width = 250 };
    private readonly TextBox _tabColor = new() { Watermark = "Tab color (#RRGGBB)", Width = 180 };
    private readonly TextBox _enter = new() { Watermark = "Command run on entering (e.g. nc_SrcThumbs)" };
    private readonly TextBox _exit = new() { Watermark = "Command run on leaving" };
    private readonly CheckBox _keepStandard = new() { Content = "Also show size / date / attribute columns" };
    private readonly ListBox _columnList = new() { Height = 150 };
    private readonly ComboBox _fieldCombo = new() { MinWidth = 260 };
    private readonly TextBox _header = new() { Watermark = "Header (empty = field name)" };
    private readonly TextBox _width = new() { Width = 70 };
    private ColumnSet? _current;
    private bool _loading;

    public ColumnSetDialog(List<ColumnSet> sets, IReadOnlyList<ColumnField> fields)
        : base("Column sets", 620)
    {
        _sets = sets;
        _fields = fields;
        _fieldCombo.ItemsSource = fields.Select(f => $"{f.DisplayName}  ({f.Id})").ToList();
        _fieldCombo.SelectedIndex = fields.Count > 0 ? 0 : -1;

        StackPanel Row(params Control[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.AddRange(children);
            return panel;
        }
        Button B(string text, Action action)
        {
            var b = new Button { Content = text };
            b.Click += (_, _) => action();
            return b;
        }

        Body.Children.Add(Label("Sets", bold: true));
        Body.Children.Add(_setList);
        Body.Children.Add(Row(B("New", NewSet), B("Copy", CopySet), B("Delete", DeleteSet)));
        Body.Children.Add(_name);
        Body.Children.Add(_mask);
        Body.Children.Add(_keepStandard);
        Body.Children.Add(Row(_background, _tabColor));
        Control WithPicker(TextBox box)
        {
            var pick = CommandPickerDialog.ButtonFor(box);
            pick.Margin = new Avalonia.Thickness(6, 0, 0, 0);
            var line = new DockPanel();
            DockPanel.SetDock(pick, Dock.Right);
            line.Children.AddRange([pick, box]);
            pick.Bind(IsEnabledProperty, new Avalonia.Data.Binding(nameof(IsEnabled)) { Source = box });
            return line;
        }
        Body.Children.Add(WithPicker(_enter));
        Body.Children.Add(WithPicker(_exit));
        Body.Children.Add(Label("Columns", bold: true));
        Body.Children.Add(_columnList);
        Body.Children.Add(Row(_fieldCombo, B("Add", AddColumn), B("Remove", RemoveColumn), B("▲", () => MoveColumn(-1)), B("▼", () => MoveColumn(1))));
        Body.Children.Add(Row(new TextBlock { Text = "Selected column:", VerticalAlignment = VerticalAlignment.Center }, _header,
            new TextBlock { Text = "Width:", VerticalAlignment = VerticalAlignment.Center }, _width));
        _header.MinWidth = 220;

        _setList.SelectionChanged += (_, _) => LoadSet(_setList.SelectedIndex >= 0 && _setList.SelectedIndex < _sets.Count ? _sets[_setList.SelectedIndex] : null);
        _columnList.SelectionChanged += (_, _) => LoadColumn();
        _name.TextChanged += (_, _) => Store(() => _current!.Name = (_name.Text ?? string.Empty).Trim(), refreshSets: true);
        _mask.TextChanged += (_, _) => Store(() => _current!.AutoPathMask = string.IsNullOrWhiteSpace(_mask.Text) ? null : _mask.Text.Trim());
        _background.TextChanged += (_, _) => Store(() => _current!.Background = Empty(_background.Text));
        _tabColor.TextChanged += (_, _) => Store(() => _current!.TabColor = Empty(_tabColor.Text));
        _enter.TextChanged += (_, _) => Store(() => _current!.EnterCommand = Empty(_enter.Text));
        _exit.TextChanged += (_, _) => Store(() => _current!.ExitCommand = Empty(_exit.Text));
        _keepStandard.IsCheckedChanged += (_, _) => Store(() => _current!.KeepStandardColumns = _keepStandard.IsChecked == true);
        _header.TextChanged += (_, _) => StoreColumn(c => c.Header = string.IsNullOrWhiteSpace(_header.Text) ? null : _header.Text);
        _width.TextChanged += (_, _) => StoreColumn(c =>
        {
            if (double.TryParse(_width.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var w) && w >= 30)
            {
                c.Width = w;
            }
        });

        AddButton("OK", Close, isDefault: true);
        RefreshSets(sets.FirstOrDefault());
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private string FieldName(string id) => _fields.FirstOrDefault(f => f.Id == id)?.DisplayName ?? id;

    private void RefreshSets(ColumnSet? select)
    {
        _loading = true;
        _setList.ItemsSource = _sets.Select(s => s.Name + (string.IsNullOrEmpty(s.AutoPathMask) ? string.Empty : "   (automatic)")).ToList();
        _setList.SelectedIndex = select is null ? -1 : _sets.IndexOf(select);
        _loading = false;
        LoadSet(select);
    }

    private void LoadSet(ColumnSet? set)
    {
        if (_loading)
        {
            return;
        }
        _current = set;
        _loading = true;
        _name.Text = set?.Name;
        _mask.Text = set?.AutoPathMask;
        _keepStandard.IsChecked = set?.KeepStandardColumns ?? true;
        _background.Text = set?.Background;
        _tabColor.Text = set?.TabColor;
        _enter.Text = set?.EnterCommand;
        _exit.Text = set?.ExitCommand;
        _name.IsEnabled = _mask.IsEnabled = _keepStandard.IsEnabled = set is not null;
        _background.IsEnabled = _tabColor.IsEnabled = _enter.IsEnabled = _exit.IsEnabled = set is not null;
        _loading = false;
        RefreshColumns(0);
    }

    private void RefreshColumns(int select)
    {
        _loading = true;
        _columnList.ItemsSource = _current?.Columns.Select(c => $"{FieldName(c.Field)}   —   {c.Header ?? "(field name)"}, {c.Width:0} px").ToList() ?? [];
        _columnList.SelectedIndex = _current is { Columns.Count: > 0 } ? Math.Clamp(select, 0, _current.Columns.Count - 1) : -1;
        _loading = false;
        LoadColumn();
    }

    private ColumnSpec? SelectedColumn => _current is not null && _columnList.SelectedIndex >= 0 && _columnList.SelectedIndex < _current.Columns.Count
        ? _current.Columns[_columnList.SelectedIndex] : null;

    private void LoadColumn()
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        _header.Text = SelectedColumn?.Header;
        _width.Text = SelectedColumn?.Width.ToString("0", CultureInfo.CurrentCulture);
        _header.IsEnabled = _width.IsEnabled = SelectedColumn is not null;
        _loading = false;
    }

    private void Store(Action action, bool refreshSets = false)
    {
        if (_loading || _current is null)
        {
            return;
        }
        action();
        if (refreshSets)
        {
            var index = _setList.SelectedIndex;
            _loading = true;
            _setList.ItemsSource = _sets.Select(s => s.Name).ToList();
            _setList.SelectedIndex = index;
            _loading = false;
        }
    }

    private void StoreColumn(Action<ColumnSpec> action)
    {
        if (_loading || SelectedColumn is not { } column)
        {
            return;
        }
        action(column);
        var index = _columnList.SelectedIndex;
        RefreshColumns(index);
    }

    private void NewSet()
    {
        var set = new ColumnSet { Name = "New view " + (_sets.Count + 1) };
        _sets.Add(set);
        RefreshSets(set);
    }

    private void CopySet()
    {
        if (_current is null)
        {
            return;
        }
        var copy = new ColumnSet
        {
            Name = _current.Name + " (kopya)",
            KeepStandardColumns = _current.KeepStandardColumns,
            AutoPathMask = null,
            Background = _current.Background,
            TabColor = _current.TabColor,
            EnterCommand = _current.EnterCommand,
            ExitCommand = _current.ExitCommand,
            Columns = _current.Columns.Select(c => new ColumnSpec { Field = c.Field, Header = c.Header, Width = c.Width }).ToList(),
        };
        _sets.Add(copy);
        RefreshSets(copy);
    }

    private void DeleteSet()
    {
        if (_current is not null)
        {
            _sets.Remove(_current);
            RefreshSets(_sets.FirstOrDefault());
        }
    }

    private void AddColumn()
    {
        if (_current is null || _fieldCombo.SelectedIndex < 0)
        {
            return;
        }
        var field = _fields[_fieldCombo.SelectedIndex];
        _current.Columns.Add(new ColumnSpec { Field = field.Id, Width = field.Kind is ColumnKind.Number ? 80 : 120 });
        RefreshColumns(_current.Columns.Count - 1);
    }

    private void RemoveColumn()
    {
        if (_current is not null && SelectedColumn is { } column)
        {
            var index = _columnList.SelectedIndex;
            _current.Columns.Remove(column);
            RefreshColumns(index);
        }
    }

    private void MoveColumn(int delta)
    {
        if (_current is null || SelectedColumn is not { } column)
        {
            return;
        }
        var index = _columnList.SelectedIndex;
        var target = Math.Clamp(index + delta, 0, _current.Columns.Count - 1);
        _current.Columns.RemoveAt(index);
        _current.Columns.Insert(target, column);
        RefreshColumns(target);
    }
}
