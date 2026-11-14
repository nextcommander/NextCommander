using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Views;

/// <summary>
/// Rename in place (Shift+F6): a box over the name in the list. Enter renames, Esc gives up, ↑ / ↓ rename and go on
/// to the previous / next file; leaving the box renames too.
/// </summary>
public partial class FilePanelView
{
    private TextBox? _renameBox;
    private Canvas? _renameLayer;
    private FileItemViewModel? _renaming;
    private Func<FileItemViewModel, string, Task<bool>>? _commitRename;
    private bool _renameFinishing;

    /// <summary>Whether a name is being typed in the list.</summary>
    public bool IsRenaming => _renameBox is { IsVisible: true };

    /// <summary>The box the name is typed in (tests).</summary>
    public TextBox? RenameBox => _renameBox;

    /// <summary>
    /// Starts renaming <paramref name="item"/> in the list; false when its name is not shown in a cell that can be edited
    /// (thumbnails), then the name is asked in a dialog. <paramref name="commit"/> renames (false: not renamed).
    /// </summary>
    public bool BeginRename(FileItemViewModel item, bool selectBaseName, Func<FileItemViewModel, string, Task<bool>> commit)
    {
        if (item.Entry is null || _vm is null)
        {
            return false;
        }
        ScrollToCursor(item);
        ContentHost.UpdateLayout();
        if (NameArea(item) is not { } area)
        {
            return false;
        }
        EnsureRenameBox();
        _renaming = item;
        _commitRename = commit;
        var name = item.Entry.Name;
        _renameBox!.Text = name;
        Canvas.SetLeft(_renameBox, area.X - 3);
        Canvas.SetTop(_renameBox, area.Y - 2);
        _renameBox.Width = Math.Max(120, area.Width + 6);
        _renameBox.Height = Math.Max(22, area.Height + 4);
        _renameBox.IsVisible = true;
        _renameBox.Focus();
        var dot = selectBaseName ? name.LastIndexOf('.') : -1;
        _renameBox.SelectionStart = 0;
        _renameBox.SelectionEnd = dot > 0 ? dot : name.Length;
        return true;
    }

    private void ScrollToCursor(FileItemViewModel item)
    {
        if (Brief.IsEffectivelyVisible)
        {
            Brief.ScrollIntoView(item);
        }
        else if (Grid.IsEffectivelyVisible)
        {
            Grid.ScrollIntoView(item, null);
        }
    }

    /// <summary>The name of an item in the list, in the coordinates of the list area; null when it cannot be edited there.</summary>
    private Rect? NameArea(FileItemViewModel item)
    {
        if (Grid.IsEffectivelyVisible)
        {
            // the name cell of the item's row (the name column runs to the next column)
            var text = Grid.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => ReferenceEquals(t.DataContext, item) && t.Classes.Contains("cell") && t.Text == item.ShownName);
            if (text?.FindAncestorOfType<DataGridCell>() is not { } cell
                || text.TranslatePoint(default, ContentHost) is not { } textAt || cell.TranslatePoint(default, ContentHost) is not { } cellAt)
            {
                return null;
            }
            return new Rect(textAt.X, cellAt.Y, Math.Max(40, cellAt.X + cell.Bounds.Width - textAt.X - 4), cell.Bounds.Height);
        }
        if (Brief.IsEffectivelyVisible && Brief.NameRect(item) is { } rect && Brief.TranslatePoint(rect.Position, ContentHost) is { } at)
        {
            return new Rect(at, rect.Size);
        }
        return null;
    }

    private void EnsureRenameBox()
    {
        if (_renameBox is not null)
        {
            return;
        }
        _renameBox = new TextBox { Classes = { "noloc", "renamebox" }, Padding = new Thickness(3, 1), MinHeight = 0, IsVisible = false, AcceptsReturn = false };
        _renameBox.AddHandler(KeyDownEvent, OnRenameKeyDown, RoutingStrategies.Tunnel);
        _renameBox.LostFocus += (_, _) => _ = FinishRenameAsync(commit: true, move: 0);
        _renameLayer = new Canvas();
        _renameLayer.Children.Add(_renameBox);
        ContentHost.Children.Add(_renameLayer);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = FinishRenameAsync(commit: true, move: 0);
                break;
            case Key.Escape:
                e.Handled = true;
                _ = FinishRenameAsync(commit: false, move: 0);
                break;
            case Key.Up or Key.Down:
                e.Handled = true;
                _ = FinishRenameAsync(commit: true, move: e.Key == Key.Up ? -1 : 1);
                break;
            default:
                e.Handled = false; // typing, Home / End, Ctrl+A ... stay in the box (the window does not run commands)
                break;
        }
    }

    /// <summary>Ends the typing: renames (when changed), then goes on to the file above / below to rename it too.</summary>
    public async Task FinishRenameAsync(bool commit, int move)
    {
        if (_renameFinishing || _renameBox is not { IsVisible: true } box || _renaming is not { } item || _vm is null)
        {
            return;
        }
        _renameFinishing = true;
        try
        {
            var text = box.Text ?? string.Empty;
            box.IsVisible = false;
            var vm = _vm;
            var renamed = commit && text.Trim() != item.Entry!.Name && _commitRename is { } rename && await rename(item, text);
            if (!renamed && commit && text.Trim() != item.Entry!.Name && text.Trim().Length > 0)
            {
                move = 0; // not renamed (the reason was shown): stay on the file
            }
            FocusList();
            if (move == 0)
            {
                return;
            }
            // after the rename the list holds the item under its new name (the cursor is on it)
            var current = renamed ? vm.CursorItem : item;
            var index = current is null ? -1 : vm.Items.ToList().IndexOf(current);
            for (var i = index + move; i >= 0 && i < vm.Items.Count; i += move)
            {
                if (vm.Items[i] is { IsParent: false, Entry: not null } next)
                {
                    vm.CursorItem = next;
                    BeginRename(next, !next.IsDirectory && vm.Settings.RenameSelectsNameOnly, _commitRename!);
                    return;
                }
            }
        }
        finally
        {
            _renameFinishing = false;
        }
    }
}
