using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Connections;

/// <summary>
/// Renaming in the tree (the user's request, 23 November 2026): F2 or Rename of the row's menu makes the row's name a
/// box in place. Enter (or leaving the box) keeps the name, Esc goes back. A folder moves with everything in it, a
/// connection gets its new name; both are saved at once (a connection with other changes not saved keeps them for its
/// Save). While the box is open the tree's keys (Delete, Enter, the arrows) stay in the box.
/// </summary>
public sealed partial class ConnectionManagerView
{
    private (Row Row, TextBox Box, TextBlock Name)? _treeRename;

    /// <summary>Whether a row's name is being changed in the tree (tests).</summary>
    public bool IsRenamingInTree => _treeRename is not null;

    /// <summary>The box of the row being renamed (tests).</summary>
    public TextBox? TreeRenameBox => _treeRename?.Box;

    /// <summary>F2 / Rename on the tree: the selected row's name as a box in place.</summary>
    public void RenameInTree()
    {
        EndTreeRename(commit: true);
        if (Selected is not { } row || _tree.ContainerFromItem(row) is not Control container)
        {
            FocusName(); // no row in view: the name in the editor
            return;
        }
        var name = container.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, RowNameTag));
        if (name?.Parent is not Panel holder)
        {
            FocusName();
            return;
        }
        var current = row.Folder is { } folder ? ConnectionFolders.NameOf(folder) : row.Connection!.Name;
        var box = new TextBox
        {
            Text = current,
            FontSize = name.FontSize,
            FontWeight = name.FontWeight,
            MinHeight = 0,
            Padding = new Thickness(4, 1),
            Margin = new Thickness(-5, 0, 6, 0),
            Classes = { "noloc" },
        };
        Avalonia.Automation.AutomationProperties.SetName(box, row.Folder is not null ? "Folder name" : "Connection name");
        holder.Children.Insert(holder.Children.IndexOf(name), box);
        name.IsVisible = false;
        _treeRename = (row, box, name);
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                EndTreeRename(commit: e.Key == Key.Enter);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => EndTreeRename(commit: true);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Ends renaming in the tree: the name typed (not empty) is kept and saved, or the old one stays.</summary>
    public void EndTreeRename(bool commit)
    {
        if (_treeRename is not { } rename)
        {
            return;
        }
        _treeRename = null;
        var text = rename.Box.Text?.Trim() ?? string.Empty;
        (rename.Box.Parent as Panel)?.Children.Remove(rename.Box);
        rename.Name.IsVisible = true;
        if (commit && text.Length == 0)
        {
            Toast("A name can't be empty");
        }
        else if (commit && rename.Row.Folder is { } folder && text != ConnectionFolders.NameOf(folder))
        {
            RenameFolder(folder, text);
        }
        else if (commit && rename.Row.Connection is { } c && text != c.Name)
        {
            RenameConnection(c, text);
        }
        FocusSelectedRow();
    }

    private void RenameFolder(string folder, string name)
    {
        if (name.Contains('/', StringComparison.Ordinal))
        {
            Toast("A folder name can't contain /");
            return;
        }
        var target = ConnectionFolders.Join(ConnectionFolders.ParentOf(folder), name);
        if (!ConnectionFolders.Move(_settings, folder, target))
        {
            Toast("There is a folder with this name already");
            return;
        }
        if (_expanded.Remove(folder))
        {
            _expanded.Add(target);
        }
        _request.Save();
        Reveal(null, target);
        Toast(Core.Localization.Localizer.T("Renamed to “{0}”").Replace("{0}", name, StringComparison.Ordinal));
    }

    private void RenameConnection(RemoteConnection c, string name)
    {
        // other changes of the connection not saved yet stay for its own Save; otherwise the new name is saved at once
        var otherChanges = ReferenceEquals(Current, c) && _dirty;
        c.Name = name;
        RefreshTree();
        if (ReferenceEquals(Current, c) && _title is not null && _renaming is null)
        {
            _title.Content = TitleText(c);
        }
        if (!otherChanges)
        {
            _request.Save();
            if (ReferenceEquals(Current, c))
            {
                _snapshotOf = c;
                _snapshot = Snapshot(c);
            }
        }
        if (ReferenceEquals(Current, c))
        {
            UpdateDirty(c);
            UpdateNav();
        }
        Toast(Core.Localization.Localizer.T("Renamed to “{0}”").Replace("{0}", name, StringComparison.Ordinal));
    }
}
