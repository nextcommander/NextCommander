using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace NextCommander.App.Views;

/// <summary>
/// The internal terminal in place of a file panel (the user's request, 23 November 2026): its title bar dragged onto a
/// side of the panels puts it there (the side under the pointer is marked while dragging) and the other side becomes the
/// active one; dragged onto the other side it moves, dropped outside the panels or ⤓ it goes back below them. Closing it
/// gives the side its file panel back.
/// </summary>
public partial class MainWindow
{
    private PanelSideView? _terminalSide;

    /// <summary>The side whose place the terminal takes (null: below the panels or closed; tests).</summary>
    public PanelSideView? TerminalSide => _terminalSide;

    private void WireTerminalDock(TerminalView terminal)
    {
        terminal.DockDragMoved += (_, at) => MarkTerminalDrop(SideAt(at));
        terminal.DockDragEnded += (_, at) =>
        {
            MarkTerminalDrop(null);
            if (SideAt(at) is { } side)
            {
                DockTerminalIn(side);
            }
            else if (_terminalSide is not null && !InPanels(at))
            {
                DockTerminalBelow();
            }
        };
        terminal.DockBelowRequested += (_, _) => DockTerminalBelow();
    }

    /// <summary>The side of the panels under a point of the window (null: none, or the page is not the files).</summary>
    private PanelSideView? SideAt(Point at)
    {
        if (CurrentPage != WorkspacePage.Files || !PanelsGrid.IsVisible)
        {
            return null;
        }
        foreach (var side in new[] { LeftSideView, RightSideView })
        {
            if (side.IsEffectivelyVisible && side.TranslatePoint(default, this) is { } origin && new Rect(origin, side.Bounds.Size).Contains(at))
            {
                return side;
            }
        }
        return null;
    }

    private bool InPanels(Point at) =>
        PanelsGrid.IsVisible && PanelsGrid.TranslatePoint(default, this) is { } origin && new Rect(origin, PanelsGrid.Bounds.Size).Contains(at);

    private void MarkTerminalDrop(PanelSideView? side)
    {
        foreach (var each in new[] { LeftSideView, RightSideView })
        {
            each.Classes.Set("droptarget", ReferenceEquals(each, side));
        }
    }

    /// <summary>Puts the terminal in place of a side's file panel (from below the panels or from the other side).</summary>
    public void DockTerminalIn(PanelSideView side)
    {
        if (_vm is null)
        {
            return;
        }
        EnsureTerminal();
        if (_terminal is not { } terminal || ReferenceEquals(_terminalSide, side))
        {
            return;
        }
        RestoreTerminalSize();
        if (_terminalSide is { } old)
        {
            old.HideOverlay();
        }
        else
        {
            ApplyTerminalSettings();
            TerminalHost.Child = null;
            TerminalHost.IsVisible = false;
            _vm.IsTerminalOpen = false; // the command line is back below the panels
        }
        side.ShowOverlay(terminal);
        terminal.SetDockedInPanel(true);
        _terminalSide = side;
        // the keys go to the file panel that is still shown
        _vm.ActivateSide(ReferenceEquals(side, LeftSideView) ? _vm.RightSide : _vm.LeftSide);
        terminal.Activate();
    }

    /// <summary>The terminal back below the panels (shown and focused).</summary>
    public void DockTerminalBelow()
    {
        if (ReleaseTerminalFromSide())
        {
            ShowTerminal();
        }
    }

    /// <summary>The side gets its file panel back; the terminal goes into its place below the panels (not shown). False when it was not in a side.</summary>
    private bool ReleaseTerminalFromSide()
    {
        if (_terminalSide is not { } side || _terminal is not { } terminal)
        {
            return false;
        }
        _terminalSide = null;
        side.HideOverlay();
        terminal.SetDockedInPanel(false, Bounds.Height);
        TerminalHost.Child = terminal;
        return true;
    }

    private Point? _pageDragStart;
    private bool _pageDragging;

    /// <summary>
    /// The Terminal page's tab can be dragged onto a side of the panels too (the user's request, 23 November 2026): when
    /// the drag starts the files page is shown, the side under the pointer is marked, and the terminal goes into the side
    /// it is dropped on; dropped elsewhere it stays on its page.
    /// </summary>
    private void WireTerminalPageDrag(Button tab)
    {
        tab.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } source
                && ReferenceEquals(source, tab))
            {
                _pageDragStart = e.GetPosition(this);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        tab.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_pageDragging || _pageDragStart is not { } start)
            {
                return;
            }
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _pageDragStart = null;
                return;
            }
            var at = e.GetPosition(this);
            if (Math.Abs(at.X - start.X) + Math.Abs(at.Y - start.Y) < 10)
            {
                return;
            }
            _pageDragging = true;
            e.Pointer.Capture(this); // the tab goes away with the page change: the window follows the pointer
            Cursor = new Cursor(StandardCursorType.DragMove);
            AddHandler(PointerMovedEvent, OnTerminalPageDragMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, OnTerminalPageDragReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerCaptureLostEvent, OnTerminalPageDragLost, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            SelectPage(WorkspacePage.Files);
            MarkTerminalDrop(SideAt(at));
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        tab.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (!_pageDragging)
            {
                _pageDragStart = null;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnTerminalPageDragMoved(object? sender, PointerEventArgs e) => MarkTerminalDrop(SideAt(e.GetPosition(this)));

    private void OnTerminalPageDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        var side = SideAt(e.GetPosition(this));
        EndTerminalPageDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        if (side is not null)
        {
            DockTerminalIn(side);
        }
        else
        {
            SelectPage(WorkspacePage.Terminal); // not on a panel: back to its page
        }
    }

    private void OnTerminalPageDragLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pageDragging)
        {
            EndTerminalPageDrag();
            SelectPage(WorkspacePage.Terminal);
        }
    }

    private void EndTerminalPageDrag()
    {
        _pageDragging = false;
        _pageDragStart = null;
        Cursor = null;
        MarkTerminalDrop(null);
        RemoveHandler(PointerMovedEvent, OnTerminalPageDragMoved);
        RemoveHandler(PointerReleasedEvent, OnTerminalPageDragReleased);
        RemoveHandler(PointerCaptureLostEvent, OnTerminalPageDragLost);
    }

    /// <summary>The terminal shortcuts while it is in a side: into the terminal, or back to the panel from it.</summary>
    private bool ToggleDockedTerminal()
    {
        if (_terminalSide is null || _terminal is null)
        {
            return false;
        }
        if (_terminal.IsKeyboardFocusWithin)
        {
            ActiveView.FocusList();
        }
        else
        {
            SelectPage(WorkspacePage.Files);
            _terminal.Activate();
        }
        return true;
    }
}
