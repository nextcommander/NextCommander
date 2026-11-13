using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.App.Services.Icons;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Controls;

/// <summary>
/// Button bar (horizontal, wrapping into several rows) or vertical bar between the panels.
/// Buttons run internal commands or programs; right-click edits; dropping a file adds a program button.
/// </summary>
public sealed class ButtonBarControl : UserControl
{
    private static readonly Dictionary<string, string> s_builtInIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["refresh"] = "FmBarRefresh",
        ["brief"] = "FmBarBrief",
        ["full"] = "FmBarFull",
        ["thumbs"] = "FmBarThumbs",
        ["branch"] = "FmBarBranch",
        ["tab"] = "FmBarTab",
        ["star"] = "FmBarStar",
        ["history"] = "FmBarHistory",
        ["filter"] = "FmBarFilter",
        ["hidden"] = "FmBarHidden",
        ["terminal"] = "FmBarTerminal",
        ["commands"] = "FmBarCommands",
        ["folder"] = "FmFolderIcon",
        ["file"] = "FmFileIcon",
    };

    private readonly Panel _panel;
    private readonly bool _vertical;
    private MainWindowViewModel? _vm;
    private List<ButtonDefinition> _buttons = [];

    public ButtonBarControl(bool vertical)
    {
        _vertical = vertical;
        // The vertical bar starts at the top and scrolls when the window is too low for it.
        _panel = vertical
            ? new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Top }
            : new WrapPanel { Orientation = Orientation.Horizontal };
        Content = vertical
            ? new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
            : _panel;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var menu = new ContextMenu();
        AddBarItems(menu);
        WatchIcons(menu);
        ContextMenu = menu;
    }

    /// <summary>The items for the whole bar: new button, edit the bar, reset it to the default buttons.</summary>
    private void AddBarItems(ContextMenu menu)
    {
        void Add(string header, string icon, Func<Task> action)
        {
            var item = new MenuItem { Header = header, Tag = icon };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }
        Add("New button...", "nc_AddButton", () => _vm?.AddButtonAsync(null, _vertical) ?? Task.CompletedTask);
        Add("Edit button bar...", _vertical ? "nc_VertButtonConfig" : "nc_ButtonConfig", () => _vm?.EditButtonBarAsync(_vertical) ?? Task.CompletedTask);
        menu.Items.Add(new Separator());
        Add("Reset button bar", _vertical ? "nc_ResetVertButtonBar" : "nc_ResetButtonBar", () => _vm?.ResetButtonBarAsync(_vertical) ?? Task.CompletedTask);
    }

    /// <summary>
    /// Sets the icons of a menu when it opens (the icon pack or the "icons in the menus" setting may have changed);
    /// Opening does not come when the menu is opened in code, Opened does.
    /// </summary>
    private void WatchIcons(ContextMenu menu)
    {
        void SetIcons()
        {
            var show = _vm?.Settings.ShowMenuIcons != false;
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Tag is string key && key != "history")
                {
                    item.Icon = show ? Services.MenuIcons.Create(key) : null;
                }
            }
        }
        menu.Opening += (_, _) => SetIcons();
        menu.Opened += (_, _) => SetIcons();
    }

    /// <summary>The vertical button bar (between the panels) rather than the one at the top.</summary>
    public bool IsVertical => _vertical;

    /// <summary>Icon size of the buttons (16, 20, 24, 32, 48).</summary>
    public int IconSize { get; private set; } = 16;

    public void Bind(MainWindowViewModel vm, List<ButtonDefinition> buttons, int iconSize = 16)
    {
        if (!ReferenceEquals(_vm, vm))
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
        _vm = vm;
        _buttons = buttons;
        IconSize = NormalizeSize(iconSize);
        Rebuild();
        WatchActivePanel();
    }

    // ---------------------------------------------------------------- Back / Forward buttons

    private PanelViewModel? _watchedPanel;

    private static bool IsBack(ButtonDefinition definition) => string.Equals(definition.Command, "nc_GoBack", StringComparison.OrdinalIgnoreCase);

    private static bool IsForward(ButtonDefinition definition) => string.Equals(definition.Command, "nc_GoForward", StringComparison.OrdinalIgnoreCase);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActivePanel))
        {
            WatchActivePanel();
        }
        if (e.PropertyName is nameof(MainWindowViewModel.ActivePanel) or nameof(MainWindowViewModel.VerticalPanels))
        {
            UpdateDirectionalIcons();
        }
    }

    /// <summary>
    /// The icon key of a command whose icon shows the way of the operation: Move (nc_RenMov) points from the active
    /// panel to the other one (right / left, or down / up with the panels above each other).
    /// </summary>
    public static string DirectionalIcon(string command, MainWindowViewModel? vm)
    {
        if (vm is null || !string.Equals(command, "nc_RenMov", StringComparison.OrdinalIgnoreCase))
        {
            return command;
        }
        var fromRight = ReferenceEquals(vm.ActiveSide, vm.RightSide);
        return vm.VerticalPanels
            ? (fromRight ? "nc_RenMov.up" : "nc_RenMov.down")
            : (fromRight ? "nc_RenMov.left" : "nc_RenMov");
    }

    /// <summary>The buttons with a direction get their icon again (another panel became active, or the layout changed).</summary>
    private void UpdateDirectionalIcons()
    {
        foreach (var button in _panel.Children.OfType<Button>())
        {
            if (button.Tag is ButtonDefinition definition && string.Equals(definition.Command.TrimStart('*'), "nc_RenMov", StringComparison.OrdinalIgnoreCase))
            {
                button.Content = CreateContent(definition);
            }
        }
    }

    /// <summary>The Back / Forward buttons follow the active tab (another panel or tab has another history).</summary>
    private void WatchActivePanel()
    {
        var panel = _vm?.ActivePanel;
        if (!ReferenceEquals(panel, _watchedPanel))
        {
            if (_watchedPanel is not null)
            {
                _watchedPanel.PropertyChanged -= OnPanelPropertyChanged;
            }
            _watchedPanel = panel;
            if (panel is not null)
            {
                panel.PropertyChanged += OnPanelPropertyChanged;
            }
        }
        UpdateBackForward();
    }

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelViewModel.CanGoBack) or nameof(PanelViewModel.CanGoForward))
        {
            UpdateBackForward();
        }
    }

    /// <summary>A Back / Forward button with nowhere to go is dimmed (not disabled: its right click menu stays).</summary>
    private void UpdateBackForward()
    {
        foreach (var button in _panel.Children.OfType<Button>())
        {
            if (button.Tag is ButtonDefinition definition && (IsBack(definition) || IsForward(definition)))
            {
                var can = IsBack(definition) ? _watchedPanel?.CanGoBack == true : _watchedPanel?.CanGoForward == true;
                button.Classes.Set("unavailable", !can);
                button.Opacity = can ? 1 : 0.4;
            }
        }
    }

    /// <summary>The folders of Back / Forward at the top of the button's right click menu: a click goes there in one step.</summary>
    private void AddHistoryItems(ContextMenu menu, bool back)
    {
        foreach (var old in menu.Items.OfType<Control>().Where(c => Equals(c.Tag, "history")).ToList())
        {
            menu.Items.Remove(old);
        }
        if (_vm?.ActivePanel is not { } panel)
        {
            return;
        }
        var folders = back ? panel.BackHistory : panel.ForwardHistory;
        var position = 0;
        for (var i = 0; i < folders.Count; i++)
        {
            var index = i;
            // the folder's name first (a TextBlock of its own: paths are not translated and keep their last part)
            var path = folders[i].ToDisplayString();
            var name = folders[i].Name is { Length: > 0 } n ? n : path;
            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            header.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } });
            if (name != path)
            {
                header.Children.Add(new TextBlock { Text = path, Opacity = 0.65, Classes = { "noloc" }, MaxWidth = 360, TextTrimming = TextTrimming.PathSegmentEllipsis });
            }
            var item = new MenuItem { Header = header, Tag = "history" };
            ToolTip.SetTip(item, path);
            item.Click += async (_, _) =>
            {
                await (back ? panel.GoBackAsync(index) : panel.GoForwardAsync(index));
            };
            menu.Items.Insert(position++, item);
        }
        if (folders.Count == 0)
        {
            menu.Items.Insert(position++, new MenuItem { Header = back ? "No folders to go back to" : "No folders to go forward to", IsEnabled = false, Tag = "history" });
        }
        menu.Items.Insert(position, new Separator { Tag = "history" });
    }

    /// <summary>Sizes offered for button bar icons.</summary>
    public static readonly int[] IconSizes = [16, 20, 24, 32, 48];

    public static int NormalizeSize(int size) => IconSizes.Contains(size) ? size : 16;

    public void FocusFirst() => _panel.Children.OfType<Button>().FirstOrDefault()?.Focus();

    public void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var definition in _buttons)
        {
            if (definition.IsSeparator)
            {
                _panel.Children.Add(new Border
                {
                    Classes = { "barseparator" },
                    Width = _vertical ? double.NaN : 1,
                    Height = _vertical ? 1 : double.NaN,
                    HorizontalAlignment = _vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
                });
                continue;
            }
            _panel.Children.Add(CreateButton(definition));
        }
        UpdateBackForward();
    }

    private Button CreateButton(ButtonDefinition definition)
    {
        var button = new Button { Classes = { "barbutton" }, Content = CreateContent(definition), Tag = definition };
        if (_vertical)
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
        ToolTip.SetTip(button, definition.Tooltip ?? definition.Label ?? definition.Command);
        // Icon-only buttons need a name for screen readers.
        Avalonia.Automation.AutomationProperties.SetName(button, definition.Tooltip ?? definition.Label ?? definition.Command);
        if (definition.IsMenu)
        {
            button.Flyout = BuildMenu(definition.Menu!);
        }
        else
        {
            button.Click += async (_, _) =>
            {
                if (_vm is not null)
                {
                    await _vm.ExecuteButtonAsync(definition);
                }
            };
        }

        var menu = new ContextMenu();
        void Add(string header, string icon, Action action)
        {
            var item = new MenuItem { Header = header, Tag = icon };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Edit...", "bar.edit", () => _ = _vm?.EditButtonAsync(_buttons, definition));
        Add("Delete", "bar.delete", () => _vm?.RemoveButton(_buttons, definition));
        Add(_vertical ? "Move up" : "Move left", _vertical ? "bar.up" : "bar.left", () => _vm?.MoveButton(_buttons, definition, -1));
        Add(_vertical ? "Move down" : "Move right", _vertical ? "bar.down" : "bar.right", () => _vm?.MoveButton(_buttons, definition, 1));
        Add("Add separator", "bar.separator", () =>
        {
            _buttons.Insert(_buttons.IndexOf(definition) + 1, ButtonDefinition.Separator());
            Rebuild();
        });
        menu.Items.Add(new Separator());
        AddBarItems(menu);
        WatchIcons(menu);
        if (IsBack(definition) || IsForward(definition))
        {
            menu.Opening += (_, _) => AddHistoryItems(menu, IsBack(definition));
            menu.Opened += (_, _) =>
            {
                if (!menu.Items.OfType<Control>().Any(c => Equals(c.Tag, "history")))
                {
                    AddHistoryItems(menu, IsBack(definition));
                }
            };
        }
        button.ContextMenu = menu;
        return button;
    }

    private MenuFlyout BuildMenu(IEnumerable<ButtonDefinition> entries)
    {
        var flyout = new MenuFlyout();
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                flyout.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = entry.Label ?? entry.Tooltip ?? entry.Command };
            if (entry.IsMenu)
            {
                foreach (var child in BuildMenu(entry.Menu!).Items.Cast<object>().ToList())
                {
                    item.Items.Add(child);
                }
            }
            else
            {
                var e = entry;
                item.Click += async (_, _) =>
                {
                    if (_vm is not null)
                    {
                        await _vm.ExecuteButtonAsync(e);
                    }
                };
            }
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private Control CreateContent(ButtonDefinition definition)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var icon = CreateIcon(definition, IconSize, _vm);
        if (icon is not null)
        {
            stack.Children.Add(icon);
        }
        var text = definition.Label ?? (icon is null ? ShortName(definition.Command) : null);
        if (!string.IsNullOrEmpty(text))
        {
            stack.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        }
        return stack;
    }

    /// <summary>The icon of a button at a size (also the preview of the button bar editor); null for a text-only button.</summary>
    public static Control? CreateIcon(ButtonDefinition definition, int size, MainWindowViewModel? vm)
    {
        if (CreateIcon16(definition, vm) is not { } icon)
        {
            return null;
        }
        return size == 16 ? icon : new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = icon };
    }

    private static Control? CreateIcon16(ButtonDefinition definition, MainWindowViewModel? vm)
    {
        // Colored menu icon of the command unless the button names an icon file.
        if ((definition.Icon is null || s_builtInIcons.ContainsKey(definition.Icon))
            && Services.MenuIcons.Create(DirectionalIcon(definition.Command.TrimStart('*'), vm)) is { } colored)
        {
            return colored;
        }
        if (definition.Icon is { } key && s_builtInIcons.TryGetValue(key, out var resource)
            && Application.Current?.TryGetResource(resource, null, out var geometry) == true && geometry is Geometry g)
        {
            return new PathIcon { Data = g, Width = 16, Height = 16 };
        }
        // "file,index": one icon of an icon file (exe, dll, icl), chosen in the button bar editor
        if (IconReference(definition.Icon) is { } reference && OperatingSystem.IsWindows()
            && Services.Icons.WindowsShellIconService.LoadIndex(reference.File, reference.Index) is { } bitmap)
        {
            return new Image { Source = bitmap, Width = 16, Height = 16 };
        }
        var command = definition.Command.TrimStart('*');
        var programPath = definition.Icon ?? command;
        if (command.StartsWith("nc_", StringComparison.OrdinalIgnoreCase) || command.StartsWith("uc_", StringComparison.OrdinalIgnoreCase))
        {
            return null; // text label for internal commands without an icon
        }
        // Program, folder or file: operating-system icon (Windows), else a type icon.
        var expanded = Environment.ExpandEnvironmentVariables(programPath);
        if (!Path.IsPathRooted(expanded) || (!File.Exists(expanded) && !Directory.Exists(expanded)))
        {
            return new PathIcon { Data = FileKinds.GeometryFor(FileKind.Executable), Foreground = FileKinds.BrushFor(FileKind.Executable), Width = 16, Height = 16 };
        }
        var entry = new VfsEntry { Name = Path.GetFileName(expanded), Path = VfsPath.Local(expanded), IsDirectory = Directory.Exists(expanded) };
        var item = new FileItemViewModel(entry);
        var image = new Image { Width = 16, Height = 16 };
        var fallback = new PathIcon { Data = item.Icon, Foreground = item.IconBrush, Width = 16, Height = 16 };
        var host = new Panel { Width = 16, Height = 16, Children = { fallback, image } };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileItemViewModel.SystemIcon))
            {
                image.Source = item.SystemIcon;
                fallback.IsVisible = item.SystemIcon is null;
            }
        };
        vm?.Left.Icons.Request([item]);
        return host;
    }

    /// <summary>"C:\\Windows\\System32\\shell32.dll,4" → (file, 4); null for a plain path or a built-in name.</summary>
    public static (string File, int Index)? IconReference(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }
        var comma = icon.LastIndexOf(',');
        if (comma <= 0 || !int.TryParse(icon[(comma + 1)..].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }
        return (Environment.ExpandEnvironmentVariables(icon[..comma].Trim().Trim('"')), index);
    }

    private static string ShortName(string command)
    {
        var c = command.TrimStart('*');
        if (c.StartsWith("nc_", StringComparison.OrdinalIgnoreCase) || c.StartsWith("uc_", StringComparison.OrdinalIgnoreCase))
        {
            return c[3..];
        }
        return Path.GetFileNameWithoutExtension(c);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is null || e.DataTransfer.TryGetFiles() is not { } files)
        {
            return;
        }
        e.Handled = true;
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            await _vm.AddButtonAsync(new ButtonDefinition { Command = path, Tooltip = Path.GetFileName(path) }, _vertical);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var buttons = _panel.Children.OfType<Button>().ToList();
        var index = buttons.FindIndex(b => b.IsFocused);
        if (index < 0)
        {
            return;
        }
        var next = e.Key switch
        {
            Key.Right or Key.Down => index + 1,
            Key.Left or Key.Up => index - 1,
            Key.Home => 0,
            Key.End => buttons.Count - 1,
            _ => -2,
        };
        if (next == -2)
        {
            return;
        }
        buttons[Math.Clamp(next, 0, buttons.Count - 1)].Focus();
        e.Handled = true;
    }
}
