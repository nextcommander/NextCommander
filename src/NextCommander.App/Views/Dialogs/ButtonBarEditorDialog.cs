using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Result of the button bar editor: the buttons and the icon size.</summary>
public sealed record ButtonBarEdit(List<ButtonDefinition> Buttons, int IconSize);

/// <summary>
/// The button bar editor: the bar as a preview strip (click a button to edit it), add / delete / separator /
/// move, the icon size, and the fields of the selected button (command, parameters, start path, icon, name, tool tip).
/// Works on a copy; OK returns it.
/// </summary>
public sealed class ButtonBarEditorDialog : Window
{
    private readonly List<ButtonDefinition> _buttons;
    private readonly Func<ButtonDefinition, int, Control?> _icon;
    private readonly Func<string, Task<string?>> _pickFile;
    private readonly ListBox _strip = new() { Height = 96, SelectionMode = SelectionMode.Single };
    private readonly ComboBox _size = new() { MinWidth = 90 };
    private readonly TextBox _command = new();
    private readonly TextBox _parameters = new();
    private readonly TextBox _startPath = new() { PlaceholderText = "Empty = active panel's folder" };
    private readonly TextBox _iconFile = new() { PlaceholderText = "Empty = the command's icon; built-in name (refresh, star ...) or program / icon file" };
    private readonly TextBox _label = new() { PlaceholderText = "Short name shown next to the icon on the button (optional)" };
    private readonly TextBox _tooltip = new() { PlaceholderText = "Tip shown when the mouse is over the button" };
    private readonly ComboBox _window = new() { MinWidth = 220, Margin = new Thickness(0, 3) };

    private static readonly (string Text, ProgramWindowState State)[] s_windowStates =
    [
        ("Normal", ProgramWindowState.Normal), ("Run minimized", ProgramWindowState.Minimized), ("Run maximized", ProgramWindowState.Maximized),
    ];
    private readonly Control _fields;
    private bool _loading;

    public ButtonBarEditorDialog(string title, IEnumerable<ButtonDefinition> buttons, int iconSize, bool vertical,
        Func<ButtonDefinition, int, Control?> icon, Func<string, Task<string?>> pickFile)
    {
        // Deep copy: Cancel leaves the bar as it was.
        _buttons = JsonSerializer.Deserialize<List<ButtonDefinition>>(JsonSerializer.Serialize(buttons.ToList())) ?? [];
        _icon = icon;
        _pickFile = pickFile;
        Title = title;
        Width = 820;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _size.ItemsSource = Controls.ButtonBarControl.IconSizes.Select(s => $"{s} x {s}").ToList();
        _size.SelectedIndex = Math.Max(0, Array.IndexOf(Controls.ButtonBarControl.IconSizes, Controls.ButtonBarControl.NormalizeSize(iconSize)));
        _size.SelectionChanged += (_, _) => RefreshStrip();

        _strip.ItemsPanel = new FuncTemplate<Panel?>(() => vertical
            ? new StackPanel { Orientation = Orientation.Horizontal } // shown across like the horizontal bar: easier to edit
            : new WrapPanel { Orientation = Orientation.Horizontal });
        _strip.ItemTemplate = new FuncDataTemplate<ButtonDefinition>((b, _) => b is null ? new TextBlock() : StripItem(b));
        _strip.SelectionChanged += (_, _) => LoadSelected();
        ScrollViewer.SetHorizontalScrollBarVisibility(_strip, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var add = SideButton("Add", Add);
        var separator = SideButton("Add separator", AddSeparator);
        var delete = SideButton("Delete", Delete);
        var left = SideButton("◀ Move", () => Move(-1));
        var right = SideButton("Move ▶", () => Move(1));
        var side = new StackPanel { Spacing = 6, Width = 120 };
        side.Children.AddRange([add, separator, delete, left, right]);

        var appearance = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 0, 0) };
        appearance.Children.Add(new TextBlock { Text = "Display", FontWeight = FontWeight.SemiBold });
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(new TextBlock { Text = "Icon size:", VerticalAlignment = VerticalAlignment.Center });
        sizeRow.Children.Add(_size);
        appearance.Children.Add(sizeRow);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        top.Children.Add(side);
        var stripBox = new Border { Child = _strip, BorderThickness = new Thickness(1), Margin = new Thickness(10, 0, 0, 0), BorderBrush = Brushes.Gray };
        Grid.SetColumn(stripBox, 1);
        top.Children.Add(stripBox);
        Grid.SetColumn(appearance, 2);
        top.Children.Add(appearance);

        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 14, 0, 0) };
        void Field(int row, string label, TextBox box, Func<Task>? browse = null, bool command = false)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            fields.Children.Add(text);
            box.Margin = new Thickness(0, 3);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            fields.Children.Add(box);
            // the property itself (TextChanged comes later): a following add / select must not lose the text
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    StoreSelected();
                }
            };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 3, 0, 3) };
            if (browse is not null)
            {
                var button = new Button { Content = "..." };
                ToolTip.SetTip(button, command ? "Choose a program" : "Choose a file");
                button.Click += async (_, _) => await browse();
                tools.Children.Add(button);
            }
            if (command)
            {
                tools.Children.Add(CommandPickerDialog.ButtonFor(box));
            }
            Grid.SetRow(tools, row);
            Grid.SetColumn(tools, 2);
            fields.Children.Add(tools);
        }
        Field(0, "Command:", _command, async () =>
        {
            if (await _pickFile("Choose program") is { } file)
            {
                _command.Text = file;
            }
        }, command: true);
        Field(1, "Parameters:", _parameters);
        Field(2, "Start folder:", _startPath);
        Field(3, "Icon file:", _iconFile, async () =>
        {
            if (await _pickFile("Choose icon file") is not { } file)
            {
                return;
            }
            _iconFile.Text = file;
            // a file with several icons (exe, dll, icl): choose one of them
            if (OperatingSystem.IsWindows() && Services.Icons.WindowsShellIconService.IconCount(file) > 1)
            {
                var picker = new IconPickerDialog(file);
                await picker.ShowDialog(this);
                if (picker.Result is { } index)
                {
                    _iconFile.Text = $"{file},{index}";
                }
            }
        });
        Field(4, "Name:", _label);
        Field(5, "Tooltip:", _tooltip);
        var windowLabel = new TextBlock { Text = "Window (Windows):", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(windowLabel, 6);
        fields.Children.Add(windowLabel);
        _window.ItemsSource = s_windowStates.Select(w => w.Text).ToList();
        _window.SelectionChanged += (_, _) => StoreSelected();
        Grid.SetRow(_window, 6);
        Grid.SetColumn(_window, 1);
        fields.Children.Add(_window);
        _fields = fields;

        var help = new TextBlock
        {
            Text = "Command: nc_/uc_ command, program path or folder; a '*' prefix runs it as administrator. Parameters: %P %N %T %S %L ...",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            FontSize = 12,
        };

        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "dialog" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        ok.Click += (_, _) =>
        {
            Result = new ButtonBarEdit(_buttons.Where(b => b.IsSeparator || !string.IsNullOrWhiteSpace(b.Command)).ToList(), SelectedSize);
            Close();
        };
        cancel.Click += (_, _) => Close();
        var buttonsRow = new StackPanel { Classes = { "buttonrow" }, Margin = new Thickness(0, 12, 0, 0) };
        buttonsRow.Children.AddRange([ok, cancel]);

        var body = new StackPanel { Margin = new Thickness(14), Spacing = 4 };
        body.Children.AddRange([top, _fields, help, buttonsRow]);
        Content = body;

        RefreshStrip(select: _buttons.FindIndex(b => !b.IsSeparator));
    }

    public ButtonBarEdit? Result { get; private set; }

    /// <summary>The buttons being edited (tests).</summary>
    public IReadOnlyList<ButtonDefinition> Buttons => _buttons;

    public int SelectedSize => Controls.ButtonBarControl.IconSizes[Math.Max(0, _size.SelectedIndex)];

    public void SelectSize(int size) => _size.SelectedIndex = Array.IndexOf(Controls.ButtonBarControl.IconSizes, size);

    public void Select(int index) => _strip.SelectedIndex = index;

    /// <summary>Sets the fields of the selected button (tests).</summary>
    /// <summary>Window state of the selected button (tests).</summary>
    public void SetWindowState(ProgramWindowState state) => _window.SelectedIndex = Array.FindIndex(s_windowStates, w => w.State == state);

    public void SetFields(string command, string? label, string? tooltip)
    {
        _command.Text = command;
        _label.Text = label;
        _tooltip.Text = tooltip;
    }

    public void Add()
    {
        var index = InsertAt();
        _buttons.Insert(index, new ButtonDefinition { Command = "nc_RereadSource" });
        RefreshStrip(index);
        _command.Focus();
        _command.SelectAll();
    }

    public void AddSeparator()
    {
        var index = InsertAt();
        _buttons.Insert(index, ButtonDefinition.Separator());
        RefreshStrip(index);
    }

    public void Delete()
    {
        var index = _strip.SelectedIndex;
        if (index >= 0 && index < _buttons.Count)
        {
            _buttons.RemoveAt(index);
            RefreshStrip(Math.Min(index, _buttons.Count - 1));
        }
    }

    public void Move(int delta)
    {
        var index = _strip.SelectedIndex;
        var to = index + delta;
        if (index < 0 || to < 0 || to >= _buttons.Count)
        {
            return;
        }
        (_buttons[index], _buttons[to]) = (_buttons[to], _buttons[index]);
        RefreshStrip(to);
    }

    private int InsertAt() => _strip.SelectedIndex >= 0 ? _strip.SelectedIndex + 1 : _buttons.Count;

    private static Button SideButton(string text, Action action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => action();
        return button;
    }

    private Control StripItem(ButtonDefinition button)
    {
        if (button.IsSeparator)
        {
            return new Border { Width = 2, Height = SelectedSize, Margin = new Thickness(4, 2), Background = Brushes.Gray };
        }
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(2) };
        if (_icon(button, SelectedSize) is { } icon)
        {
            stack.Children.Add(icon);
        }
        var text = button.Label ?? (stack.Children.Count == 0 ? button.Command : null);
        if (!string.IsNullOrEmpty(text))
        {
            stack.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
        }
        ToolTip.SetTip(stack, button.Tooltip ?? button.Command);
        return stack;
    }

    private void RefreshStrip(int? select = null)
    {
        var index = select ?? _strip.SelectedIndex;
        _loading = true;
        _strip.ItemsSource = null;
        _strip.ItemsSource = _buttons.ToList();
        _loading = false;
        _strip.SelectedIndex = Math.Clamp(index, -1, _buttons.Count - 1);
        LoadSelected();
    }

    private ButtonDefinition? Selected => _strip.SelectedIndex >= 0 && _strip.SelectedIndex < _buttons.Count ? _buttons[_strip.SelectedIndex] : null;

    private void LoadSelected()
    {
        if (_loading)
        {
            return;
        }
        _loading = true;
        var button = Selected;
        var editable = button is { IsSeparator: false };
        _fields.IsEnabled = editable;
        _command.Text = editable ? button!.Command : string.Empty;
        _parameters.Text = editable ? button!.Parameters : string.Empty;
        _startPath.Text = editable ? button!.StartPath : string.Empty;
        _iconFile.Text = editable ? button!.Icon : string.Empty;
        _label.Text = editable ? button!.Label : string.Empty;
        _tooltip.Text = editable ? button!.Tooltip : string.Empty;
        _window.SelectedIndex = Math.Max(0, Array.FindIndex(s_windowStates, w => w.State == (editable ? button!.WindowState : ProgramWindowState.Normal)));
        _loading = false;
    }

    private void StoreSelected()
    {
        if (_loading || Selected is not { IsSeparator: false } button)
        {
            return;
        }
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        button.Command = _command.Text?.Trim() ?? string.Empty;
        button.Parameters = Clean(_parameters.Text);
        button.StartPath = Clean(_startPath.Text);
        button.Icon = Clean(_iconFile.Text);
        button.Label = Clean(_label.Text);
        button.Tooltip = Clean(_tooltip.Text);
        button.WindowState = s_windowStates[Math.Max(0, _window.SelectedIndex)].State;
        // redraw only the edited button (keeps the caret in the text box)
        if (_strip.ContainerFromIndex(_strip.SelectedIndex) is ListBoxItem item)
        {
            item.Content = null;
            item.Content = button;
        }
    }

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
