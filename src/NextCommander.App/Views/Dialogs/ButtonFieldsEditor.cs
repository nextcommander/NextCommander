using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The fields of a button bar button, shared by the button bar editor and the single button dialog: "Action" (command,
/// parameters, start folder) and "Appearance" (icon with its preview, label, tool tip) side by side, below them a strip
/// with the program's window and "Run as administrator" (the '*' prefix of the command).
/// </summary>
public sealed class ButtonFieldsEditor : UserControl
{
    private static readonly (string Text, ProgramWindowState State)[] s_windowStates =
    [
        ("Normal", ProgramWindowState.Normal), ("Run minimized", ProgramWindowState.Minimized), ("Run maximized", ProgramWindowState.Maximized),
    ];

    private readonly Func<ButtonDefinition, int, Control?>? _icon;
    private readonly Func<string, Task<string?>> _pickFile;
    private readonly Border _preview = new() { Width = 34, Height = 34, CornerRadius = new CornerRadius(8) };
    private readonly ToggleSwitch _admin = new() { OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private bool _loading;

    public ButtonFieldsEditor(Func<string, Task<string?>> pickFile, Func<ButtonDefinition, int, Control?>? icon = null)
    {
        _pickFile = pickFile;
        _icon = icon;
        foreach (var box in new[] { Command, Parameters, StartPath, Icon, Label, Tooltip })
        {
            box.MinHeight = 32;
            box.VerticalContentAlignment = VerticalAlignment.Center;
        }
        Command.Classes.Add("noloc");
        Command.FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,monospace");
        Parameters.FontFamily = Command.FontFamily;

        var browseProgram = DialogLook.IconButton("MiFolderOpen", "Choose a program", async () =>
        {
            if (await _pickFile("Choose program") is { } file)
            {
                Command.Text = file;
            }
        });
        var commandRow = Row(Command, browseProgram, CommandPickerDialog.ButtonFor(Command));
        var action = Column(DialogLook.SectionTitle("MiTerminal", "Action"),
            DialogLook.Field("Command", commandRow), DialogLook.Field("Parameters", Parameters), DialogLook.Field("Start folder", StartPath, last: true));

        _preview[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var chooseIcon = DialogLook.IconButton("MiFolderOpen", "Choose an icon file", ChooseIconAsync);
        var iconRow = Row(Icon, chooseIcon);
        iconRow.Children.Insert(0, _preview);
        DockPanel.SetDock(_preview, Dock.Left);
        _preview.Margin = new Thickness(0, 0, 6, 0);
        var appearance = Column(DialogLook.SectionTitle("MiTheme", "Appearance"),
            DialogLook.Field("Icon", iconRow), DialogLook.Field("Label", Label), DialogLook.Field("Tooltip", Tooltip, last: true));

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
        columns.Children.Add(action);
        Grid.SetColumn(appearance, 2);
        columns.Children.Add(appearance);

        Window.ItemsSource = s_windowStates.Select(w => w.Text).ToList();
        Window.SelectedIndex = 0;
        Window.MinWidth = 150;
        Window.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(Window, "The program's window (Windows)");
        var adminText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        adminText.Children.Add(new TextBlock { Text = "Run as administrator", FontSize = 13 });
        adminText.Children.Add(DialogLook.Subtitle("Same as a * in front of the command"));
        var adminControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        adminControls.Children.AddRange([Window, _admin]);
        var options = new DockPanel();
        DockPanel.SetDock(adminControls, Dock.Right);
        options.Children.AddRange([adminControls, adminText]);
        var optionsBox = new Border { Child = options, Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 16, 0, 0) };
        optionsBox[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");

        var root = new StackPanel();
        root.Children.AddRange([columns, optionsBox]);
        Content = root;

        foreach (var box in new[] { Command, Parameters, StartPath, Icon, Label, Tooltip })
        {
            // the property itself (TextChanged comes later): a following add / select must not lose the text
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty && !_loading)
                {
                    if (ReferenceEquals(box, Command))
                    {
                        _loading = true;
                        _admin.IsChecked = Command.Text?.TrimStart().StartsWith('*') == true;
                        _loading = false;
                    }
                    Changed?.Invoke(this, EventArgs.Empty);
                    UpdatePreview();
                }
            };
        }
        Window.SelectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
        _admin.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }
            var command = (Command.Text ?? string.Empty).TrimStart().TrimStart('*');
            Command.Text = _admin.IsChecked == true ? "*" + command : command;
        };
    }

    public TextBox Command { get; } = new() { PlaceholderText = "nc_RereadSource" };

    public TextBox Parameters { get; } = new() { PlaceholderText = "%P %N %T %S %L" };

    public TextBox StartPath { get; } = new() { PlaceholderText = "Empty = active panel's folder" };

    public TextBox Icon { get; } = new() { PlaceholderText = "Built-in name or icon file" };

    public TextBox Label { get; } = new() { PlaceholderText = "Optional, shown next to the icon" };

    public TextBox Tooltip { get; } = new() { PlaceholderText = "Shown when the mouse is over the button" };

    public ComboBox Window { get; } = new();

    /// <summary>"Run as administrator" (the '*' in front of the command).</summary>
    public ToggleSwitch RunAsAdministrator => _admin;

    /// <summary>A field was changed by the user.</summary>
    public event EventHandler? Changed;

    /// <summary>Shows a button's fields (null or a separator: empty).</summary>
    public void Load(ButtonDefinition? button)
    {
        _loading = true;
        var editable = button is { IsSeparator: false };
        Command.Text = editable ? button!.Command : string.Empty;
        Parameters.Text = editable ? button!.Parameters : string.Empty;
        StartPath.Text = editable ? button!.StartPath : string.Empty;
        Icon.Text = editable ? button!.Icon : string.Empty;
        Label.Text = editable ? button!.Label : string.Empty;
        Tooltip.Text = editable ? button!.Tooltip : string.Empty;
        Window.SelectedIndex = Math.Max(0, Array.FindIndex(s_windowStates, w => w.State == (editable ? button!.WindowState : ProgramWindowState.Normal)));
        _admin.IsChecked = editable && button!.Command.StartsWith('*');
        _loading = false;
        UpdatePreview();
    }

    /// <summary>Writes the fields into <paramref name="button"/>.</summary>
    public void Store(ButtonDefinition button)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        button.Command = Command.Text?.Trim() ?? string.Empty;
        button.Parameters = Clean(Parameters.Text);
        button.StartPath = Clean(StartPath.Text);
        button.Icon = Clean(Icon.Text);
        button.Label = Clean(Label.Text);
        button.Tooltip = Clean(Tooltip.Text);
        button.WindowState = s_windowStates[Math.Max(0, Window.SelectedIndex)].State;
    }

    public void SetWindowState(ProgramWindowState state) => Window.SelectedIndex = Array.FindIndex(s_windowStates, w => w.State == state);

    private void UpdatePreview()
    {
        Control? icon = null;
        if (_icon is not null && !string.IsNullOrWhiteSpace(Command.Text))
        {
            var button = new ButtonDefinition();
            Store(button);
            icon = _icon(button, 20);
        }
        if (icon is not null)
        {
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
        }
        _preview.Child = icon;
    }

    private async Task ChooseIconAsync()
    {
        if (await _pickFile("Choose icon file") is not { } file)
        {
            return;
        }
        Icon.Text = file;
        // a file with several icons (exe, dll, icl): choose one of them
        if (OperatingSystem.IsWindows() && WindowsShellIconService.IconCount(file) > 1 && TopLevel.GetTopLevel(this) is Avalonia.Controls.Window owner)
        {
            var picker = new IconPickerDialog(file);
            await picker.ShowDialog(owner);
            if (picker.Result is { } index)
            {
                Icon.Text = $"{file},{index}";
            }
        }
    }

    private static DockPanel Row(TextBox box, params Control[] tools)
    {
        var row = new DockPanel();
        for (var i = tools.Length - 1; i >= 0; i--)
        {
            tools[i].Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(tools[i], Dock.Right);
            row.Children.Add(tools[i]);
        }
        row.Children.Add(box);
        return row;
    }

    private static StackPanel Column(params Control[] children)
    {
        var column = new StackPanel();
        column.Children.AddRange(children);
        return column;
    }
}

/// <summary>The look of the newer dialogs (button bar editor ...): a header with a badge, section titles, labelled fields, icon buttons.</summary>
public static class DialogLook
{
    /// <summary>Title row: an icon on an accent tinted badge, the title and a line below it.</summary>
    public static Control Header(string glyph, string title, string subtitle)
    {
        var tint = new Border { CornerRadius = new CornerRadius(9), Opacity = 0.16 };
        tint[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var badge = new Panel { Width = 34, Height = 34 };
        badge.Children.AddRange([tint, Glyph(glyph, 19, accent: true)]);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        texts.Children.Add(Subtitle(subtitle));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.AddRange([badge, texts]);
        return Bordered(row, new Thickness(20, 14), new Thickness(0, 0, 0, 1));
    }

    /// <summary>The bottom row: a hint with an info icon on the left, the buttons on the right.</summary>
    public static Control Footer(string hint, params Button[] buttons)
    {
        var hintRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        hintRow.Children.AddRange([Glyph("MiInfo", 14), new TextBlock { Text = hint, FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }]);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange(buttons);
        var dock = new DockPanel();
        DockPanel.SetDock(row, Dock.Right);
        dock.Children.AddRange([row, hintRow]);
        return Bordered(dock, new Thickness(20, 14), new Thickness(0, 1, 0, 0));
    }

    public static TextBlock Subtitle(string text) => new() { Text = text, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };

    public static Control SectionTitle(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 12), Opacity = 0.6 };
        row.Children.AddRange([Glyph(glyph, 15), new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    /// <summary>A small label above its control.</summary>
    public static Control Field(string label, Control control, bool last = false)
    {
        var field = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, last ? 0 : 14) };
        field.Children.AddRange([new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 }, control]);
        return field;
    }

    /// <summary>A square button showing only an icon (its text is the tool tip).</summary>
    public static Button IconButton(string glyph, string tip, Func<Task> click, bool danger = false)
    {
        var button = new Button
        {
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = Glyph(glyph, 16, danger: danger),
        };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        button.Click += async (_, _) => await click();
        return button;
    }

    public static Button IconButton(string glyph, string tip, Action click, bool danger = false) =>
        IconButton(glyph, tip, () =>
        {
            click();
            return Task.CompletedTask;
        }, danger);

    /// <summary>A glyph of the icon pack in the text color (or the accent / the danger color).</summary>
    public static Control Glyph(string glyph, double size, bool accent = false, bool danger = false)
    {
        if (IconPacks.Glyph(glyph, IconPacks.Current) is not { } geometry)
        {
            return new TextBlock { Text = "•" };
        }
        var color = danger ? new SolidColorBrush(Color.Parse("#E5484D"))
            : Resolve(accent ? "SystemControlHighlightAccentBrush" : "SystemControlForegroundBaseHighBrush") ?? Brushes.Gray;
        var path = IconPacks.Draw(geometry, color);
        return new Viewbox { Width = size, Height = size, Child = path, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    private static IBrush? Resolve(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as IBrush : null;

    private static Border Bordered(Control child, Thickness padding, Thickness border)
    {
        var box = new Border { Child = child, Padding = padding, BorderThickness = border };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return box;
    }
}
