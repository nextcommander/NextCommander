using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Result of the button bar editor: the buttons and the icon size.</summary>
public sealed record ButtonBarEdit(List<ButtonDefinition> Buttons, int IconSize);

/// <summary>
/// The button bar editor: a header, a tool row (add, separator, move, delete; the icon size), the bar as a preview
/// (click a button to edit it, drag it to move it), the fields of the selected button and Save / Cancel.
/// Works on a copy; Save returns it.
/// </summary>
public sealed class ButtonBarEditorDialog : Window
{
    private readonly List<ButtonDefinition> _buttons;
    private readonly Func<ButtonDefinition, int, Control?> _icon;
    private readonly ListBox _strip = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent, Padding = new Thickness(6), MinHeight = 62 };
    private readonly StackPanel _sizes = new() { Orientation = Orientation.Horizontal };
    private readonly ButtonFieldsEditor _fields;
    private int _size;
    private bool _loading;
    private int _dragFrom = -1;

    public ButtonBarEditorDialog(string title, IEnumerable<ButtonDefinition> buttons, int iconSize, bool vertical,
        Func<ButtonDefinition, int, Control?> icon, Func<string, Task<string?>> pickFile)
    {
        // Deep copy: Cancel leaves the bar as it was.
        _buttons = JsonSerializer.Deserialize<List<ButtonDefinition>>(JsonSerializer.Serialize(buttons.ToList())) ?? [];
        _icon = icon;
        _size = Controls.ButtonBarControl.NormalizeSize(iconSize);
        Title = title;
        Width = 780;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // the tools: add, separator, move, delete; the icon size on the right
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tools.Children.AddRange([
            DialogLook.IconButton("MiPlus", "Add button", Add),
            DialogLook.IconButton("MiStacked", "Add separator", AddSeparator),
            DialogLook.IconButton("MiArrowLeft", "Move left", () => Move(-1)),
            DialogLook.IconButton("MiArrowRight", "Move right", () => Move(1)),
            DialogLook.IconButton("MiTrash", "Delete", Delete, danger: true),
        ]);
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        sizeRow.Children.AddRange([new TextBlock { Text = "Icon size", FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center }, _sizes]);
        RenderSizes();
        var toolRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(sizeRow, Dock.Right);
        toolRow.Children.AddRange([sizeRow, tools]);

        // the bar: a dashed frame around the buttons, wrapping like the bar
        _strip.ItemsPanel = new FuncTemplate<Panel?>(() => vertical
            ? new StackPanel { Orientation = Orientation.Horizontal } // shown across like the horizontal bar: easier to edit
            : new WrapPanel { Orientation = Orientation.Horizontal });
        _strip.ItemTemplate = new FuncDataTemplate<ButtonDefinition>((b, _) => b is null ? new TextBlock() : StripItem(b));
        _strip.SelectionChanged += (_, _) => LoadSelected();
        ScrollViewer.SetHorizontalScrollBarVisibility(_strip, ScrollBarVisibility.Auto);
        Avalonia.Automation.AutomationProperties.SetName(_strip, "Button bar preview");
        _strip.AddHandler(PointerPressedEvent, OnStripPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _strip.AddHandler(PointerReleasedEvent, OnStripReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _strip.KeyDown += OnStripKeyDown;
        var frame = new Rectangle { StrokeThickness = 1, StrokeDashArray = [4, 3], RadiusX = 10, RadiusY = 10 };
        frame[!Shape.StrokeProperty] = new DynamicResourceExtension("SystemControlForegroundBaseMediumLowBrush");
        var stripBack = new Border { CornerRadius = new CornerRadius(10) };
        stripBack[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var stripBox = new Panel();
        stripBox.Children.AddRange([stripBack, frame, _strip]);

        var barSection = new StackPanel { Margin = new Thickness(20, 16) };
        barSection.Children.AddRange([toolRow, stripBox]);
        var barBorder = new Border { Child = barSection, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        _fields = new ButtonFieldsEditor(pickFile, (b, size) => _icon(b, size)) { Margin = new Thickness(20, 16) };
        _fields.Changed += (_, _) => StoreSelected();

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "dialog" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        save.Click += (_, _) =>
        {
            Result = new ButtonBarEdit(_buttons.Where(b => b.IsSeparator || !string.IsNullOrWhiteSpace(b.Command)).ToList(), SelectedSize);
            Close();
        };
        cancel.Click += (_, _) => Close();

        var root = new DockPanel();
        var header = DialogLook.Header("MiHammer", title, "Drag to move, select a button to edit it");
        var footer = DialogLook.Footer("nc_ / uc_ commands, programs and folders; parameters: %P %N %T %S %L ...", cancel, save);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        var body = new StackPanel();
        body.Children.AddRange([barBorder, _fields]);
        root.Children.AddRange([header, footer, body]);
        Content = root;

        RefreshStrip(select: _buttons.FindIndex(b => !b.IsSeparator));
    }

    public ButtonBarEdit? Result { get; private set; }

    /// <summary>The buttons being edited (tests).</summary>
    public IReadOnlyList<ButtonDefinition> Buttons => _buttons;

    /// <summary>The fields of the selected button (tests).</summary>
    public ButtonFieldsEditor Fields => _fields;

    public int SelectedSize => _size;

    public void SelectSize(int size)
    {
        _size = Controls.ButtonBarControl.NormalizeSize(size);
        RenderSizes();
        RefreshStrip();
    }

    public void Select(int index) => _strip.SelectedIndex = index;

    /// <summary>Window state of the selected button (tests).</summary>
    public void SetWindowState(ProgramWindowState state) => _fields.SetWindowState(state);

    /// <summary>Sets the fields of the selected button (tests).</summary>
    public void SetFields(string command, string? label, string? tooltip)
    {
        _fields.Command.Text = command;
        _fields.Label.Text = label;
        _fields.Tooltip.Text = tooltip;
    }

    public void Add()
    {
        var index = InsertAt();
        _buttons.Insert(index, new ButtonDefinition { Command = "nc_RereadSource" });
        RefreshStrip(index);
        _fields.Command.Focus();
        _fields.Command.SelectAll();
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

    public void Move(int delta) => MoveTo(_strip.SelectedIndex, _strip.SelectedIndex + delta);

    /// <summary>Moves the button at <paramref name="from"/> to <paramref name="to"/> (dragging in the preview).</summary>
    public void MoveTo(int from, int to)
    {
        if (from < 0 || from >= _buttons.Count || to < 0 || to >= _buttons.Count || from == to)
        {
            return;
        }
        var button = _buttons[from];
        _buttons.RemoveAt(from);
        _buttons.Insert(to, button);
        RefreshStrip(to);
    }

    private int InsertAt() => _strip.SelectedIndex >= 0 ? _strip.SelectedIndex + 1 : _buttons.Count;

    /// <summary>The icon sizes as a segmented choice.</summary>
    private void RenderSizes()
    {
        _sizes.Children.Clear();
        var sizes = Controls.ButtonBarControl.IconSizes;
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var first = i == 0;
            var last = i == sizes.Length - 1;
            var button = new ToggleButton
            {
                Content = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsChecked = size == _size,
                FontSize = 12,
                MinWidth = 38,
                Height = 30,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(first ? 8 : 0, last ? 8 : 0, last ? 8 : 0, first ? 8 : 0),
                Margin = new Thickness(first ? 0 : -1, 0, 0, 0),
                Classes = { "noloc" },
            };
            ToolTip.SetTip(button, $"{size} x {size}");
            button.Click += (_, _) => SelectSize(size);
            _sizes.Children.Add(button);
        }
    }

    private Control StripItem(ButtonDefinition button)
    {
        var height = _size + 16;
        if (button.IsSeparator)
        {
            var line = new Border { Width = 1, Height = Math.Max(20, _size + 4), Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
            line[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseMediumLowBrush");
            ToolTip.SetTip(line, "Separator");
            return line;
        }
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = height, Margin = new Thickness(2, 0) };
        if (_icon(button, _size) is { } icon)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            stack.Children.Add(icon);
        }
        var text = button.Label ?? (stack.Children.Count == 0 ? button.Command : null);
        if (!string.IsNullOrEmpty(text))
        {
            stack.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
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
        _fields.IsEnabled = button is { IsSeparator: false };
        _fields.Opacity = _fields.IsEnabled ? 1 : 0.45;
        _fields.Load(button);
        _loading = false;
    }

    private void StoreSelected()
    {
        if (_loading || Selected is not { IsSeparator: false } button)
        {
            return;
        }
        _fields.Store(button);
        // redraw only the edited button (keeps the caret in the text box)
        if (_strip.ContainerFromIndex(_strip.SelectedIndex) is ListBoxItem item)
        {
            item.Content = null;
            item.Content = button;
        }
    }

    // ---- dragging a button to another place of the bar

    private int IndexAt(Point point) =>
        _strip.GetRealizedContainers().OfType<ListBoxItem>()
            .FirstOrDefault(c => c.TranslatePoint(default, _strip) is { } at && new Rect(at, c.Bounds.Size).Contains(point)) is { } item
            ? _strip.IndexFromContainer(item)
            : -1;

    private void OnStripPressed(object? sender, PointerPressedEventArgs e) =>
        _dragFrom = e.GetCurrentPoint(_strip).Properties.IsLeftButtonPressed ? IndexAt(e.GetPosition(_strip)) : -1;

    private void OnStripReleased(object? sender, PointerReleasedEventArgs e)
    {
        var from = _dragFrom;
        _dragFrom = -1;
        var to = IndexAt(e.GetPosition(_strip));
        if (from >= 0 && to >= 0 && from != to)
        {
            MoveTo(from, to);
            e.Handled = true;
        }
    }

    private void OnStripKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.Right when e.KeyModifiers == KeyModifiers.Control:
                Move(e.Key == Key.Left ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Delete:
                Delete();
                e.Handled = true;
                break;
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
