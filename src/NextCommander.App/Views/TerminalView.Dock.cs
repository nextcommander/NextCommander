using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace NextCommander.App.Views;

/// <summary>
/// The terminal in place of a file panel (the user's request, 23 November 2026): its title bar is dragged onto a side of
/// the panels and the terminal takes that side's place; dragged onto the other side it moves there, dropped outside the
/// panels (or ⤓) it goes back below them. Docked, it fills the side: no height grip, no maximize.
/// </summary>
public sealed partial class TerminalView
{
    private Border? _grip;
    private StackPanel? _actions;
    private Button? _dockBelow;
    private Point? _dragStart;
    private bool _dragging;

    /// <summary>The terminal is shown in place of a file panel (the window sets it).</summary>
    public bool IsDockedInPanel { get; private set; }

    /// <summary>The title bar is being dragged: the point in the window (the window marks the side under it).</summary>
    public event EventHandler<Point>? DockDragMoved;

    /// <summary>The title bar was let go after a drag: the point in the window (the window docks the terminal there).</summary>
    public event EventHandler<Point>? DockDragEnded;

    /// <summary>⤓ of a terminal in place of a panel: back below the panels.</summary>
    public event EventHandler? DockBelowRequested;

    /// <summary>Docked in a panel's place (fills it) or back below the panels (its own height).</summary>
    public void SetDockedInPanel(bool docked, double windowHeight = 0)
    {
        IsDockedInPanel = docked;
        if (_grip is not null)
        {
            _grip.IsVisible = !docked;
        }
        _maximize.IsVisible = !docked;
        if (_actions is not null)
        {
            // a panel is narrow: the actions go to a line of their own below the title and the tabs
            SetDock(_actions, docked ? Dock.Bottom : Dock.Right);
            _actions.HorizontalAlignment = docked ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
        }
        if (_dockBelow is not null)
        {
            _dockBelow.IsVisible = docked;
        }
        if (docked)
        {
            Height = double.NaN; // as high as the side
        }
        else
        {
            FitWindow(windowHeight);
        }
    }

    private Button DockBelowButton()
    {
        _dockBelow = HeaderButton("⤓", "Back below the panels", () => DockBelowRequested?.Invoke(this, EventArgs.Empty));
        _dockBelow.Tag = "dockbelow";
        _dockBelow.IsVisible = false;
        return _dockBelow;
    }

    /// <summary>The title bar (not its buttons) can be dragged onto a side of the panels.</summary>
    private void InitializeDockDrag(Control header)
    {
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || IsMaximized
                || e.Source is Button || (e.Source as Visual)?.FindAncestorOfType<Button>() is not null
                || TopLevel.GetTopLevel(this) is not { } top)
            {
                return;
            }
            _dragStart = e.GetPosition(top);
            _dragging = false;
        };
        header.PointerMoved += (_, e) =>
        {
            if (_dragStart is not { } start || TopLevel.GetTopLevel(this) is not { } top)
            {
                return;
            }
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                _dragStart = null;
                return;
            }
            var at = e.GetPosition(top);
            if (!_dragging && Math.Abs(at.X - start.X) + Math.Abs(at.Y - start.Y) < 10)
            {
                return;
            }
            if (!_dragging)
            {
                _dragging = true;
                e.Pointer.Capture(header);
                header.Cursor = new Cursor(StandardCursorType.DragMove);
            }
            DockDragMoved?.Invoke(this, at);
        };
        header.PointerReleased += (_, e) =>
        {
            var was = _dragging;
            _dragStart = null;
            _dragging = false;
            header.Cursor = null;
            if (was && TopLevel.GetTopLevel(this) is { } top)
            {
                e.Pointer.Capture(null);
                e.Handled = true;
                DockDragEnded?.Invoke(this, e.GetPosition(top));
            }
        };
        header.PointerCaptureLost += (_, _) =>
        {
            if (_dragging)
            {
                _dragging = false;
                _dragStart = null;
                header.Cursor = null;
                DockDragMoved?.Invoke(this, new Point(-1, -1)); // nothing marked any more
            }
        };
    }
}
