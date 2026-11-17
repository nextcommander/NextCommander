using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Views;

/// <summary>
/// Rename by clicking a second time on the name of the file under the cursor (as in Explorer and Finder): the click
/// is on the name of the row that was already current in the focused panel, without modifier keys, and the box opens
/// when the system's double-click time has passed without another click. A double click (open), another click, a key,
/// a drag or a cursor move in between give it up. Not with "open with a single click".
/// </summary>
public partial class FilePanelView
{
    private FileItemViewModel? _clickRenameItem;
    private Point _clickRenameAt;
    private int _clickGeneration;

    /// <summary>The time after the second click before the name box opens (tests: shorter).</summary>
    internal static TimeSpan? ClickRenameDelay { get; set; }

    /// <summary>A press in the list (before the cursor moves): a click on the current row's name may become a rename.</summary>
    private void NoteClickForRename(FileItemViewModel? item, PointerPressedEventArgs e)
    {
        _clickGeneration++;
        _clickRenameItem = null;
        var vm = _vm;
        var point = e.GetCurrentPoint(this);
        if (vm is null || item is null || item.IsParent || item.Entry is null || IsRenaming || vm.Settings.SingleClickOpen
            || !point.Properties.IsLeftButtonPressed || e.ClickCount != 1 || e.KeyModifiers != KeyModifiers.None
            || !vm.IsActive || !IsKeyboardFocusWithin || !ReferenceEquals(vm.CursorItem, item))
        {
            return;
        }
        var at = e.GetPosition(ContentHost);
        if (NameArea(item) is { } name && name.Contains(at))
        {
            _clickRenameItem = item;
            _clickRenameAt = at;
        }
    }

    /// <summary>The button went up: the name box opens after the double-click time when nothing else happened.</summary>
    private void ReleaseClickForRename(PointerReleasedEventArgs e)
    {
        var item = _clickRenameItem;
        _clickRenameItem = null;
        if (item is null || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }
        var at = e.GetPosition(ContentHost);
        if (Math.Abs(at.X - _clickRenameAt.X) > 4 || Math.Abs(at.Y - _clickRenameAt.Y) > 4)
        {
            return; // moved: a drag or a rubber band
        }
        var generation = _clickGeneration;
        var delay = ClickRenameDelay
            ?? (Avalonia.VisualTree.VisualExtensions.GetPlatformSettings(this)?.GetDoubleTapTime(e.Pointer.Type) ?? TimeSpan.FromMilliseconds(500)) + TimeSpan.FromMilliseconds(100);
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _clickGeneration && _vm is { IsActive: true } vm && ReferenceEquals(vm.CursorItem, item)
                && IsKeyboardFocusWithin && !IsRenaming)
            {
                CommandRequested?.Invoke(this, "nc_RenameOnly");
            }
        }, delay);
    }

    /// <summary>A double click, a key or anything else that makes a waiting rename by click void.</summary>
    private void CancelClickRename()
    {
        _clickGeneration++;
        _clickRenameItem = null;
    }
}
