using Avalonia.Controls;
using Avalonia.Input;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views;

/// <summary>Directory tree pane (Ctrl+F8) next to the file list; follows the panel and navigates it.</summary>
public partial class FilePanelView
{
    /// <summary>Context menu commands of the tree (tree_mkdir ...), executed by the main window.</summary>
    public event EventHandler<(string Command, VfsPath Path)>? TreeCommandRequested;

    /// <summary>The width of the tree pane, kept while it is hidden.</summary>
    private double _treeWidth = 220;

    public bool IsTreeVisible
    {
        get => TreePane.IsVisible;
        set
        {
            if (TreePane.IsVisible == value)
            {
                return;
            }
            // the tree's column: its width while shown (the splitter changes it), nothing while hidden
            var column = Body.ColumnDefinitions[0];
            if (!value && column.Width.IsAbsolute)
            {
                _treeWidth = column.Width.Value;
            }
            column.Width = value ? new GridLength(_treeWidth) : GridLength.Auto;
            TreePane.IsVisible = value;
            TreeSplitter.IsVisible = value;
            if (value)
            {
                SyncTree();
            }
        }
    }

    private void InitializeTree()
    {
        TreePane.DirectoryActivated += async (_, e) =>
        {
            Activated?.Invoke(this, EventArgs.Empty);
            // Display > Tree: Enter opens the folder in the other panel
            if (e.Confirm && _vm?.Settings.TreeEnterOpensInOtherPanel == true
                && TopLevel.GetTopLevel(this)?.DataContext is ViewModels.MainWindowViewModel main)
            {
                await main.OtherPanel.NavigateAsync(e.Path);
                return;
            }
            if (_vm is not null && e.Path != _vm.CurrentPath)
            {
                await _vm.NavigateAsync(e.Path);
            }
            if (e.Confirm)
            {
                FocusList();
            }
        };
        TreePane.CommandRequested += (_, e) =>
        {
            Activated?.Invoke(this, EventArgs.Empty);
            TreeCommandRequested?.Invoke(this, e);
        };
        TreePane.TreeControl.GotFocus += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        TreePane.TreeControl.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        TreePane.TreeControl.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Directory changed (create/rename/delete in the tree): reload that part of the tree.</summary>
    public void InvalidateTree(VfsPath directory) => TreePane.Invalidate(directory);

    private void OnVmNavigated(object? sender, VfsPath path)
    {
        if (TreePane.IsVisible)
        {
            _ = TreePane.RevealAsync(path);
        }
    }

    private void SyncTree()
    {
        if (TreePane.IsVisible && _vm?.CurrentPath is { } path)
        {
            var vm = _vm;
            TreePane.LoadAll = vm.Settings.TreeLoadAll;
            _ = TreePane.ShowAsync(path, p => vm.ListSubdirectoriesAsync(p, max: 5000));
        }
    }
}
