using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Views;

/// <summary>One side of the window: tab strip + the active tab's file panel.</summary>
public partial class PanelSideView : UserControl
{
    private static readonly (string Header, string Command)[] s_tabMenu =
    [
        ("New tab", "nc_OpenNewTab"),
        ("Also open in other panel", "nc_OpenTabInOther"),
        ("Rename...", "nc_RenameTab"),
        ("Lock / unlock", "nc_LockTab"),
        ("Locked (folder can change, resets on return)", "nc_LockTabResettable"),
        ("Move left", "nc_MoveTabLeft"),
        ("Move right", "nc_MoveTabRight"),
        ("-", ""),
        ("Close", "nc_CloseCurrentTab"),
        ("Close others", "nc_CloseAllTabs"),
        ("Close duplicates", "nc_CloseDuplicateTabs"),
        ("Reopen last closed", "nc_RestoreLastClosedTab"),
        ("Closed tabs...", "nc_ShowClosedTabs"),
        ("-", ""),
        ("Save tabs...", "nc_SaveTabs"),
        ("Load tabs...", "nc_LoadTabs"),
    ];

    public PanelSideView()
    {
        InitializeComponent();
        TabStrip.AddHandler(Button.ClickEvent, OnTabClick);
        TabStrip.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabStrip.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabStrip.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabStrip.AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            if (_dragging)
            {
                EndDrag();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        TabStrip.ContextMenu = BuildTabMenu();
        TabStripHost.PointerWheelChanged += (_, e) =>
        {
            if (Side is { } side && e.Delta.Y != 0)
            {
                side.ActivateRelative(e.Delta.Y > 0 ? -1 : 1);
                e.Handled = true;
            }
        };
        TabStrip.DoubleTapped += (_, e) =>
        {
            if (Side is { Settings.DoubleClickClosesTab: true } side && TabOf(e.Source) is { } tab)
            {
                side.CloseTab(tab);
                e.Handled = true;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_side is not null)
            {
                _side.PropertyChanged -= OnSidePropertyChanged;
            }
            _side = Side;
            if (_side is not null)
            {
                _side.PropertyChanged += OnSidePropertyChanged;
            }
            ApplyTabLayout();
        };
        NewTabButton.Click += (_, _) =>
        {
            Activated?.Invoke(this, EventArgs.Empty);
            CommandRequested?.Invoke(this, "nc_OpenNewTab");
        };
        FilePanel.Activated += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        // The tabs go between the drive line and the path line: move the strip into the panel.
        Root.Children.Remove(TabStripHost);
        TabStripHost[!DataContextProperty] = this[!DataContextProperty];
        FilePanel.TabSlot.Child = TabStripHost;
    }

    private TabSideViewModel? _side;

    public FilePanelView Panel => FilePanel;

    /// <summary>The tool shown in place of the file panel (null: the file panel).</summary>
    public Control? Overlay => OverlayHost.Content as Control;

    /// <summary>Shows a tool (the connection manager) in place of the file panel until <see cref="HideOverlay"/>.</summary>
    public void ShowOverlay(Control content)
    {
        OverlayHost.Content = content;
        OverlayHost.IsVisible = true;
        FilePanel.IsVisible = false;
    }

    /// <summary>The file panel again (focused).</summary>
    public void HideOverlay()
    {
        OverlayHost.Content = null;
        OverlayHost.IsVisible = false;
        FilePanel.IsVisible = true;
        FilePanel.FocusList();
    }

    private void OnSidePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabSideViewModel.TabsMultiline))
        {
            ApplyTabLayout();
        }
    }

    /// <summary>Folder tabs: one scrolling row, or rows wrapping at the panel width.</summary>
    private void ApplyTabLayout()
    {
        var multiline = Side?.TabsMultiline == true;
        TabScroll.HorizontalScrollBarVisibility = multiline ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        TabStrip.Classes.Set("multiline", multiline);
    }

    public TabSideViewModel? Side => DataContext as TabSideViewModel;

    /// <summary>The user clicked this side (tab strip or panel).</summary>
    public event EventHandler? Activated;

    /// <summary>A tab command (nc_*) for the active tab of this side.</summary>
    public event EventHandler<string>? CommandRequested;

    private ContextMenu BuildTabMenu()
    {
        var menu = new ContextMenu();
        foreach (var (header, command) in s_tabMenu)
        {
            if (header == "-")
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = header, Tag = command };
            item.Click += (_, _) => CommandRequested?.Invoke(this, command);
            menu.Items.Add(item);
        }
        // the icons of the commands, made on every opening (the icon pack may have changed); Opening does not come when
        // the menu is opened in code, Opened does
        menu.Opening += (_, _) => SetIcons(menu);
        menu.Opened += (_, _) => SetIcons(menu);
        return menu;
    }

    private void SetIcons(ContextMenu menu)
    {
        var show = Side?.Settings.ShowMenuIcons != false;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Icon = show && item.Tag is string command ? Services.MenuIcons.Create(command) : null;
        }
    }

    private static PanelViewModel? TabOf(object? source) =>
        (source as Avalonia.Visual)?.FindLogicalAncestorOrSelf<Button>() is { Tag: PanelViewModel tab } ? tab : null;

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Tag: PanelViewModel closing } close && close.Classes.Contains("tabclose") && Side is { } owner)
        {
            owner.CloseTab(closing); // the × of the current tab
            e.Handled = true;
            return;
        }
        if (TabOf(e.Source) is { } tab && Side is { } side)
        {
            var wasCurrent = ReferenceEquals(side.ActiveTab, tab);
            side.ActiveTab = tab;
            // Configuration > Folder tabs: switching a tab of the other side may leave the focus where it is.
            if (side.Settings.TabClickActivatesPanel || wasCurrent)
            {
                Activated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // Dragging a tab with the left button moves it in the strip.
    private PanelViewModel? _dragTab;
    private Avalonia.Point _dragStart;
    private bool _dragging;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = false;
        _dragTab = e.GetCurrentPoint(TabStrip).Properties.IsLeftButtonPressed ? TabOf(e.Source) : null;
        _dragStart = e.GetPosition(TabStrip);
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is not { } tab || Side is not { } side || !e.GetCurrentPoint(TabStrip).Properties.IsLeftButtonPressed)
        {
            if (_dragging || _dragTab is not null)
            {
                EndDrag();
            }
            return;
        }
        var position = e.GetPosition(TabStrip);
        if (!_dragging && Math.Abs(position.X - _dragStart.X) + Math.Abs(position.Y - _dragStart.Y) < 8)
        {
            return;
        }
        _dragging = true;
        TabStrip.Cursor = s_dragCursor;
        // over the other side's panel: the tab goes there when it is let go
        if (OtherSide is { IsEffectivelyVisible: true } other && e.GetPosition(other) is var there
            && new Rect(other.Bounds.Size).Contains(there))
        {
            SetDropTarget(other);
            return;
        }
        SetDropTarget(null);
        if (TabAt(position) is { } target && !ReferenceEquals(target, tab))
        {
            side.MoveTab(tab, side.Tabs.IndexOf(target));
        }
    }

    private static readonly Cursor s_dragCursor = new(StandardCursorType.DragMove);

    /// <summary>The side on the other half of the window (set by the main window): a tab dragged onto it moves there.</summary>
    public PanelSideView? OtherSide { get; set; }

    /// <summary>A tab of this side was let go over <see cref="OtherSide"/>: the tab and where in that side's strip (null: after its current tab).</summary>
    public event EventHandler<(PanelViewModel Tab, int? At)>? TabDroppedOnOtherSide;

    private PanelSideView? _dropTarget;

    /// <summary>Whether a tab of the other side is dragged over this side now (tests, the highlight).</summary>
    public bool IsDropTarget => Classes.Contains("droptarget");

    private void SetDropTarget(PanelSideView? target)
    {
        if (ReferenceEquals(target, _dropTarget))
        {
            return;
        }
        _dropTarget?.Classes.Set("droptarget", false);
        _dropTarget = target;
        target?.Classes.Set("droptarget", true);
    }

    /// <summary>Where a tab let go at a point of this side goes: before the tab under it, or null (after the current tab).</summary>
    private int? DropIndexAt(Point inSide)
    {
        if (Side is not { } side || this.TranslatePoint(inSide, TabStrip) is not { } inStrip)
        {
            return null;
        }
        return TabAt(inStrip) is { } under ? side.Tabs.IndexOf(under) : null;
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragTab = null;
        TabStrip.Cursor = null;
        SetDropTarget(null);
    }

    /// <summary>The tab under a point of the strip.</summary>
    private PanelViewModel? TabAt(Avalonia.Point position)
    {
        foreach (var button in TabStrip.GetRealizedContainers().Select(c => c is Avalonia.Controls.Presenters.ContentPresenter p ? p.Child : c).OfType<Button>())
        {
            if (button.Tag is PanelViewModel tab && button.TranslatePoint(new Avalonia.Point(0, 0), TabStrip) is { } origin
                && new Avalonia.Rect(origin, button.Bounds.Size).Contains(position))
            {
                return tab;
            }
        }
        return null;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _dragTab is { } dragged && Side is { } dragSide)
        {
            var onto = _dropTarget;
            EndDrag();
            e.Handled = true;
            if (onto is not null)
            {
                TabDroppedOnOtherSide?.Invoke(this, (dragged, onto.DropIndexAt(e.GetPosition(onto))));
                return;
            }
            // end of a drag: the moved tab becomes the current one (its Click may have been lost with the move)
            dragSide.ActiveTab = dragged;
            Activated?.Invoke(this, EventArgs.Empty);
            return;
        }
        _dragTab = null;
        if (TabOf(e.Source) is not { } tab || Side is not { } side)
        {
            return;
        }
        if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            side.CloseTab(tab); // middle click closes, like browsers
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Right)
        {
            // Context menu acts on the clicked tab.
            side.ActiveTab = tab;
            Activated?.Invoke(this, EventArgs.Empty);
        }
    }
}

internal static class LogicalExtensions
{
    public static T? FindLogicalAncestorOrSelf<T>(this Avalonia.Visual visual)
        where T : class
    {
        for (Avalonia.StyledElement? e = visual; e is not null; e = e.Parent)
        {
            if (e is T match)
            {
                return match;
            }
        }
        return null;
    }
}
