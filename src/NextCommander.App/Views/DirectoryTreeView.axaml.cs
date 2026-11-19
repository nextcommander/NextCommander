using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views;

/// <summary>Directory tree used by the tree pane (Ctrl+F8) and the tree window (Alt+F10).</summary>
public partial class DirectoryTreeView : UserControl
{
    private static readonly (string Header, string Command)[] s_menu =
    [
        ("New folder...", "tree_mkdir"),
        ("Rename...", "tree_rename"),
        ("Delete", "tree_delete"),
        ("Open in other panel", "tree_other"),
        ("Open in new tab", "tree_tab"),
    ];

    /// <summary>Reveals in progress: selection changes made by them are not user choices.</summary>
    private int _revealing;

    /// <summary>Only the newest reveal may select (an older one finishing late must not win).</summary>
    private int _revealGeneration;
    private TreeNodeViewModel? _root;
    private Func<VfsPath, Task<IReadOnlyList<VfsEntry>>>? _listDirectories;

    public DirectoryTreeView()
    {
        InitializeComponent();
        Tree.SelectionChanged += OnSelectionChanged;
        Tree.DoubleTapped += (_, _) => RaiseActivated(fromUser: true, confirm: true);
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        var menu = new ContextMenu();
        foreach (var (header, command) in s_menu)
        {
            var item = new MenuItem { Header = header, Tag = command };
            item.Click += (_, _) =>
            {
                if (SelectedPath is { } path)
                {
                    CommandRequested?.Invoke(this, (command, path));
                }
            };
            menu.Items.Add(item);
        }
        Tree.ContextMenu = menu;
    }

    /// <summary>A directory was chosen. Confirm = Enter / double-click (tree window closes).</summary>
    public event EventHandler<(VfsPath Path, bool Confirm)>? DirectoryActivated;

    /// <summary>Context menu command on a directory (tree_mkdir, tree_rename, tree_delete, tree_other, tree_tab).</summary>
    public event EventHandler<(string Command, VfsPath Path)>? CommandRequested;

    /// <summary>When true, selecting a node with the mouse or keyboard activates it immediately (tree pane).</summary>
    public bool NavigateOnSelect { get; set; } = true;

    public TreeView TreeControl => Tree;

    /// <summary>Read the whole tree in the background when a root is shown (Display > Always load complete directory tree).</summary>
    public bool LoadAll { get; set; }

    private CancellationTokenSource? _loadAllCts;

    public VfsPath? SelectedPath => (Tree.SelectedItem as TreeNodeViewModel)?.Path;

    /// <summary>The root node shown (null before <see cref="ShowAsync"/>).</summary>
    public TreeNodeViewModel? RootNode => _root;

    /// <summary>Shows the tree of the drive/root containing <paramref name="path"/> with <paramref name="path"/> selected.</summary>
    public async Task ShowAsync(VfsPath path, Func<VfsPath, Task<IReadOnlyList<VfsEntry>>> listDirectories)
    {
        var root = path;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }
        if (_root is null || _root.Path != root || !ReferenceEquals(_listDirectories, listDirectories))
        {
            _listDirectories = listDirectories;
            _root = TreeNodeViewModel.CreateRoot(root, listDirectories);
            Tree.ItemsSource = new[] { _root };
            _root.IsExpanded = true;
            _loadAllCts?.Cancel();
            if (LoadAll)
            {
                _loadAllCts = new CancellationTokenSource();
                _ = _root.LoadAllAsync(cancellationToken: _loadAllCts.Token);
            }
        }
        await RevealAsync(path);
    }

    public async Task RevealAsync(VfsPath path)
    {
        if (_root is null)
        {
            return;
        }
        var generation = ++_revealGeneration;
        _revealing++;
        try
        {
            var node = await _root.RevealAsync(path);
            if (generation != _revealGeneration)
            {
                return; // a newer path is being shown
            }
            Tree.SelectedItem = node;
            Dispatcher.UIThread.Post(() =>
            {
                if (Tree.ContainerFromItem(node) is Control c)
                {
                    c.BringIntoView();
                }
            }, DispatcherPriority.Background);
        }
        finally
        {
            _revealing--;
        }
    }

    /// <summary>Reloads the children of <paramref name="directory"/> (after a change in the file system).</summary>
    public void Invalidate(VfsPath directory)
    {
        static TreeNodeViewModel? Find(TreeNodeViewModel node, VfsPath p)
        {
            if (node.Path == p)
            {
                return node;
            }
            return node.Children.Where(c => !c.IsPlaceholder && p.IsSameOrUnder(c.Path)).Select(c => Find(c, p)).FirstOrDefault(n => n is not null);
        }
        if (_root is not null && Find(_root, directory) is { } found)
        {
            found.Invalidate();
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_revealing == 0 && NavigateOnSelect)
        {
            RaiseActivated(fromUser: true, confirm: false);
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RaiseActivated(fromUser: true, confirm: true);
            e.Handled = true;
        }
    }

    private void RaiseActivated(bool fromUser, bool confirm)
    {
        if (fromUser && Tree.SelectedItem is TreeNodeViewModel { IsPlaceholder: false } node)
        {
            DirectoryActivated?.Invoke(this, (node.Path, confirm));
        }
    }

    /// <summary>Node under a drag/drop position.</summary>
    public VfsPath? NodeAt(object? source) =>
        ((source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as TreeNodeViewModel) is { IsPlaceholder: false } n
            ? n.Path
            : null;
}
