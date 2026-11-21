using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NextCommander.App.Services.Icons;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Controls;

/// <summary>The menu a button of the button bar opens below it (<see cref="ButtonBarControl.DropDownOf"/>).</summary>
public enum DropDown
{
    None,

    /// <summary>The connection manager's button: the manager and the saved connections by type.</summary>
    Connections,

    /// <summary>The plugins' button: their places (rclone remotes ...) and the list of plugins.</summary>
    Plugins,
}

/// <summary>
/// Button bar (horizontal, wrapping into several rows) or vertical bar between the panels.
/// Buttons run internal commands or programs; right-click edits; dropping a file adds a program button.
/// The bar at the top has a look (<see cref="ToolbarStyle"/>: the user's template, 21 November 2026): the new looks keep
/// one row (what does not fit goes into the ⋯ menu, a group at a time), put the view buttons into one segment and show
/// the state of the buttons that switch something (hidden files, the view, branch view, the tree, the terminal ...).
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

    private Panel _panel;
    private readonly bool _vertical;
    private readonly Button _more = new() { Classes = { "barbutton", "barmore" }, Content = new TextBlock { Text = "⋯", FontSize = 16, FontWeight = FontWeight.Bold, Classes = { "noloc" } } };
    private ToolbarStyle _style = ToolbarStyle.Classic;
    private bool _labels;
    private string _layoutKey = string.Empty;
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
        ToolTip.SetTip(_more, "More buttons");
        _more.Click += (_, _) => ShowOverflow();
        SizeChanged += (_, e) =>
        {
            if (_style != ToolbarStyle.Classic && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            {
                Fit();
            }
        };
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
        // the empty part of the bar takes the right click too (without a background it lets the pointer through)
        Background = Brushes.Transparent;
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

    /// <summary>The look of the bar (the bar at the top; the vertical one stays classic).</summary>
    public ToolbarStyle Style => _style;

    /// <summary>The buttons of the bar (also those in the view segment), in their order.</summary>
    public IEnumerable<Button> Buttons => _panel.GetLogicalDescendants().OfType<Button>().Where(b => b.Tag is ButtonDefinition);

    /// <summary>The buttons moved into the ⋯ menu for lack of room (tests).</summary>
    public IReadOnlyList<ButtonDefinition> Overflow => [.. Buttons.Where(b => !b.IsVisible && b.Classes.Contains("overflow")).Select(b => (ButtonDefinition)b.Tag!)];

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
        _style = _vertical ? ToolbarStyle.Classic : vm.Settings.ToolbarStyle;
        _labels = !_vertical && vm.Settings.ToolbarLabels;
        ApplyStyle();
        Rebuild();
        WatchActivePanel();
    }

    /// <summary>The bar's frame for its look: one row (with ⋯) for the new looks, rows for the classic one; Aurora floats.</summary>
    private void ApplyStyle()
    {
        if (_vertical)
        {
            return;
        }
        foreach (var name in new[] { "tb-classic", "tb-mica", "tb-graphite", "tb-aurora" })
        {
            Classes.Remove(name);
        }
        Classes.Add("tb-" + _style.ToString().ToLowerInvariant());
        var key = _style == ToolbarStyle.Classic ? "rows" : "row";
        if (key == _layoutKey)
        {
            return;
        }
        _layoutKey = key;
        if (_more.Parent is Panel old)
        {
            old.Children.Remove(_more);
        }
        if (_style == ToolbarStyle.Classic)
        {
            _panel = new WrapPanel { Orientation = Orientation.Horizontal };
            Content = _panel;
            return;
        }
        _panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_more, Dock.Right);
        _more.IsVisible = false;
        row.Children.AddRange([_more, _panel]);
        Content = row;
    }

    // ---------------------------------------------------------------- Back / Forward buttons

    private PanelViewModel? _watchedPanel;

    private static bool IsBack(ButtonDefinition definition) => string.Equals(definition.Command, "nc_GoBack", StringComparison.OrdinalIgnoreCase);

    private static bool IsForward(ButtonDefinition definition) => string.Equals(definition.Command, "nc_GoForward", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The buttons that open a menu below them: the connection manager (nc_RemoteConnection: the manager and the saved
    /// connections by type, when there are some) and the plugins (nc_PluginsMenu).
    /// </summary>
    public static DropDown DropDownOf(ButtonDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Parameters))
        {
            return DropDown.None;
        }
        return definition.Command.TrimStart('*') switch
        {
            var c when string.Equals(c, "nc_RemoteConnection", StringComparison.OrdinalIgnoreCase) => DropDown.Connections,
            var c when string.Equals(c, "nc_PluginsMenu", StringComparison.OrdinalIgnoreCase) => DropDown.Plugins,
            _ => DropDown.None,
        };
    }

    /// <summary>The visible button of this bar with that menu (null: none).</summary>
    public Button? DropDownButton(DropDown kind) =>
        Buttons.FirstOrDefault(b => b.IsVisible && b.Tag is ButtonDefinition d && DropDownOf(d) == kind);

    /// <summary>Whether the button opens its menu now: the connection manager only with saved connections.</summary>
    private bool OpensMenu(DropDown kind) => kind switch
    {
        DropDown.Connections => _vm?.HasConnectionMenuEntries == true,
        DropDown.Plugins => true,
        _ => false,
    };

    /// <summary>The plugins button only while plugins are loaded and the setting shows it; the ▾ of the connection manager.</summary>
    private void UpdateDropDowns()
    {
        foreach (var button in Buttons)
        {
            if (button.Tag is ButtonDefinition definition && DropDownOf(definition) is var kind and not DropDown.None)
            {
                button.IsVisible = kind != DropDown.Plugins || _vm?.HasPluginMenu == true;
                button.Content = CreateContent(definition);
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.HasConnectionMenuEntries) or nameof(MainWindowViewModel.HasPluginMenu))
        {
            UpdateDropDowns();
        }
        if (e.PropertyName == nameof(MainWindowViewModel.ActivePanel))
        {
            WatchActivePanel();
        }
        if (e.PropertyName is nameof(MainWindowViewModel.ActivePanel) or nameof(MainWindowViewModel.VerticalPanels))
        {
            UpdateDirectionalIcons();
        }
        UpdateStates();
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
        foreach (var button in Buttons)
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
        if (e.PropertyName is nameof(PanelViewModel.ViewMode) or nameof(PanelViewModel.Filter) or nameof(PanelViewModel.VirtualTitle) or nameof(PanelViewModel.Items))
        {
            UpdateStates();
        }
    }

    /// <summary>
    /// Whether a button's command is on now (null: it switches nothing): the view of the active panel, branch view, the
    /// quick filter, hidden files, the folder tree, the terminal, Quick View, the panels' arrangement.
    /// </summary>
    public bool? IsOn(ButtonDefinition definition)
    {
        if (_vm is not { } vm || !string.IsNullOrWhiteSpace(definition.Parameters))
        {
            return null;
        }
        var panel = vm.ActivePanel;
        return definition.Command.TrimStart('*').ToLowerInvariant() switch
        {
            "nc_srcshort" => panel.ViewMode == PanelViewMode.Brief,
            "nc_srclong" => panel.ViewMode == PanelViewMode.Full,
            "nc_srcthumbs" => panel.ViewMode == PanelViewMode.Thumbnails,
            "nc_dirbranch" => panel.IsVirtual,
            "nc_showquickfilter" => !string.IsNullOrEmpty(panel.Filter),
            "nc_switchhidsys" => vm.Settings.ShowHidden && vm.Settings.ShowSystem,
            "nc_switchhiddenfiles" => vm.Settings.ShowHidden,
            "nc_showtreepane" => vm.ShowTreePane,
            "nc_toggleterminal" => vm.IsTerminalOpen,
            "nc_quickview" => vm.IsQuickViewActive,
            "nc_verticalpanels" => vm.VerticalPanels,
            _ => null,
        };
    }

    /// <summary>The buttons that switch something show whether it is on (class "on"; the tooltip says it too).</summary>
    private void UpdateStates()
    {
        foreach (var button in Buttons)
        {
            if (button.Tag is ButtonDefinition definition && IsOn(definition) is { } on)
            {
                button.Classes.Set("on", on);
                ToolTip.SetTip(button, Tip(definition));
            }
        }
    }

    /// <summary>The tooltip: the name, the shortcut, on / off for a switch, the command.</summary>
    private Control Tip(ButtonDefinition definition)
    {
        var name = definition.Tooltip ?? definition.Label ?? ShortName(definition.Command);
        var gesture = _vm?.Hotkeys.GestureFor(definition.Command.TrimStart('*'));
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var title = new TextBlock { Text = name, FontWeight = FontWeight.SemiBold };
        head.Children.Add(title);
        if (IsOn(definition) is { } on)
        {
            head.Children.Add(new TextBlock { Text = on ? "On" : "Off", Opacity = 0.7 });
        }
        if (gesture is { Length: > 0 } && !name.Contains(gesture, StringComparison.OrdinalIgnoreCase))
        {
            var key = new Border { BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), Child = new TextBlock { Text = gesture, FontSize = 11, Classes = { "noloc" } } };
            key[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            head.Children.Add(key);
        }
        var tip = new StackPanel { Spacing = 2 };
        tip.Children.Add(head);
        if (!definition.Command.Contains(Path.DirectorySeparatorChar) && definition.Command.StartsWith("nc_", StringComparison.OrdinalIgnoreCase) || definition.Command.StartsWith("uc_", StringComparison.OrdinalIgnoreCase))
        {
            tip.Children.Add(new TextBlock { Text = definition.Command, FontSize = 11, Opacity = 0.6, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), Classes = { "noloc" } });
        }
        return tip;
    }

    /// <summary>A Back / Forward button with nowhere to go is dimmed (not disabled: its right click menu stays).</summary>
    private void UpdateBackForward()
    {
        foreach (var button in Buttons)
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

    private Func<string, bool>? _available;

    /// <summary>
    /// Turns off the buttons whose command does not run now (<paramref name="available"/> false; another page than the
    /// files is shown); null turns all of them on.
    /// </summary>
    public void SetAvailable(Func<string, bool>? available)
    {
        _available = available;
        foreach (var button in Buttons)
        {
            ApplyAvailable(button);
        }
    }

    private void ApplyAvailable(Button button)
    {
        if (button.Tag is ButtonDefinition definition)
        {
            button.IsEnabled = _available is null || _available(definition.Command);
        }
    }

    public void FocusFirst() => Buttons.FirstOrDefault(b => b.IsVisible)?.Focus();

    private static bool IsViewCommand(ButtonDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.Parameters) && definition.Command.TrimStart('*').ToLowerInvariant() is "nc_srcshort" or "nc_srclong" or "nc_srcthumbs";

    public void Rebuild()
    {
        _panel.Children.Clear();
        StackPanel? segment = null;
        foreach (var definition in _buttons)
        {
            if (definition.IsSeparator)
            {
                segment = null;
                _panel.Children.Add(new Border
                {
                    Classes = { "barseparator" },
                    Width = _vertical ? double.NaN : 1,
                    Height = _vertical ? 1 : double.NaN,
                    HorizontalAlignment = _vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
                });
                continue;
            }
            var button = CreateButton(definition);
            if (_style != ToolbarStyle.Classic && IsViewCommand(definition))
            {
                // the view buttons next to each other: one segment, the active one raised
                if (segment is null)
                {
                    segment = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
                    _panel.Children.Add(new Border { Classes = { "barsegment" }, Child = segment, VerticalAlignment = VerticalAlignment.Center });
                }
                segment.Children.Add(button);
                continue;
            }
            segment = null;
            _panel.Children.Add(button);
        }
        UpdateBackForward();
        UpdateStates();
        if (_style != ToolbarStyle.Classic)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(Fit, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// One row: the groups (between separators) that do not fit go into the ⋯ menu, from the right. Called when the bar
    /// is built and when its width changes.
    /// </summary>
    public void Fit()
    {
        if (_style == ToolbarStyle.Classic || _vertical)
        {
            return;
        }
        // every group visible, measured
        var groups = new List<List<Control>>();
        var current = new List<Control>();
        foreach (var child in _panel.Children)
        {
            if (child is Border { Classes: var c } && c.Contains("barseparator"))
            {
                groups.Add(current);
                current = [child];
                continue;
            }
            current.Add(child);
        }
        groups.Add(current);
        foreach (var child in _panel.Children)
        {
            child.IsVisible = true;
            child.Classes.Remove("overflow");
        }
        foreach (var button in Buttons)
        {
            button.Classes.Remove("overflow");
        }
        UpdateDropDowns(); // the plugins button stays hidden without plugins
        _panel.Measure(Size.Infinity);
        _more.Measure(Size.Infinity);
        var available = Bounds.Width - Padding.Left - Padding.Right - 4;
        if (available <= 0)
        {
            _more.IsVisible = false;
            return;
        }
        double Width(List<Control> group) => group.Where(c => c.IsVisible).Sum(c => c.DesiredSize.Width);
        var total = groups.Sum(Width);
        var hidden = 0;
        while (total > available && hidden < groups.Count - 1)
        {
            if (hidden == 0)
            {
                available -= _more.DesiredSize.Width;
            }
            var group = groups[groups.Count - 1 - hidden];
            total -= Width(group);
            foreach (var control in group)
            {
                control.IsVisible = false;
                control.Classes.Add("overflow");
                foreach (var button in control is Button own ? [own] : control.GetLogicalDescendants().OfType<Button>())
                {
                    button.Classes.Add("overflow");
                }
            }
            hidden++;
        }
        _more.IsVisible = hidden > 0;
    }

    /// <summary>The ⋯ menu: the buttons that did not fit, a group after another, switches with their check.</summary>
    private void ShowOverflow()
    {
        var flyout = new MenuFlyout();
        var first = true;
        foreach (var child in _panel.Children.Where(c => c.Classes.Contains("overflow")))
        {
            if (child is Border { Classes: var c } && c.Contains("barseparator"))
            {
                if (!first)
                {
                    flyout.Items.Add(new Separator());
                }
                continue;
            }
            foreach (var button in child is Button own ? [own] : child.GetLogicalDescendants().OfType<Button>())
            {
                if (button.Tag is not ButtonDefinition definition)
                {
                    continue;
                }
                first = false;
                var item = new MenuItem
                {
                    Header = definition.Tooltip ?? definition.Label ?? ShortName(definition.Command),
                    Icon = _vm?.Settings.ShowMenuIcons != false ? CreateIcon(definition, 16, _vm) : null,
                    InputGesture = _vm?.Hotkeys.GestureFor(definition.Command.TrimStart('*')) is { Length: > 0 } g && KeyGesture.Parse(g) is var parsed ? parsed : null,
                };
                if (IsOn(definition) is { } on)
                {
                    item.ToggleType = MenuItemToggleType.CheckBox;
                    item.IsChecked = on;
                }
                item.Click += async (_, _) =>
                {
                    if (_vm is not null)
                    {
                        await _vm.ExecuteButtonAsync(definition);
                    }
                };
                flyout.Items.Add(item);
            }
        }
        flyout.ShowAt(_more);
    }

    private Button CreateButton(ButtonDefinition definition)
    {
        var button = new Button { Classes = { "barbutton" }, Content = CreateContent(definition), Tag = definition };
        ApplyAvailable(button);
        if (_vertical)
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
        ToolTip.SetTip(button, Tip(definition));
        // Icon-only buttons need a name for screen readers.
        Avalonia.Automation.AutomationProperties.SetName(button, definition.Tooltip ?? definition.Label ?? definition.Command);
        if (definition.IsMenu)
        {
            button.Flyout = BuildMenu(definition.Menu!);
        }
        else if (DropDownOf(definition) is var kind and not DropDown.None)
        {
            // a menu below the button: the connection manager with the saved connections by type (without saved
            // connections the manager opens at once), the plugins (the button only while there are plugins)
            button.IsVisible = kind != DropDown.Plugins || _vm?.HasPluginMenu == true;
            button.Click += async (_, _) =>
            {
                if (_vm is null)
                {
                    return;
                }
                if (!OpensMenu(kind))
                {
                    await _vm.ExecuteButtonAsync(definition);
                }
                else if (kind == DropDown.Connections)
                {
                    Services.ConnectionMenus.Show(_vm, button, belowButton: !_vertical);
                }
                else
                {
                    Services.PluginMenus.Show(_vm, button, belowButton: !_vertical);
                }
            };
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
        var text = definition.Label ?? (icon is null || _labels ? LabelOf(definition) : null);
        if (!string.IsNullOrEmpty(text))
        {
            stack.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        }
        if (DropDownOf(definition) is var kind and not DropDown.None && OpensMenu(kind))
        {
            // a drop-down: the arrow says so
            stack.Spacing = 1;
            stack.Children.Add(new TextBlock { Text = "▾", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Classes = { "noloc" } });
        }
        return stack;
    }

    /// <summary>The name shown next to the icon ("Names next to the icons"): the tooltip without its shortcut.</summary>
    private static string LabelOf(ButtonDefinition definition)
    {
        var text = definition.Tooltip ?? ShortName(definition.Command);
        foreach (var stop in new[] { " (", ";" })
        {
            if (text.IndexOf(stop, StringComparison.Ordinal) is > 0 and var at)
            {
                text = text[..at];
            }
        }
        return text.Trim();
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
        var buttons = Buttons.Where(b => b.IsVisible).ToList();
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
