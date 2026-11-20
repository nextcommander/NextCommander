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
    private List<TreeNodeViewModel> _roots = [];
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
        // the icons are set when the menu opens (a menu opened in code gets no Opening)
        menu.Opening += (_, _) => SetMenuIcons(menu);
        menu.Opened += (_, _) => SetMenuIcons(menu);
        _menu = menu;
        Tree.ContextMenu = menu;
    }

    private readonly ContextMenu _menu;

    /// <summary>The right click menu (null when <see cref="MenuEnabled"/> is off).</summary>
    public ContextMenu? Menu => Tree.ContextMenu;

    /// <summary>Off: no right click menu (where nobody runs its commands).</summary>
    public bool MenuEnabled
    {
        get => Tree.ContextMenu is not null;
        set => Tree.ContextMenu = value ? _menu : null;
    }

    /// <summary>Configuration > Icons > icons in the menus, as the main window has it (on when there is none).</summary>
    private static bool ShowMenuIcons =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.DataContext
            is not MainWindowViewModel vm || vm.ShowMenuIcons;

    private static void SetMenuIcons(ContextMenu menu)
    {
        var show = ShowMenuIcons;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Icon = show && item.Tag is string command ? Services.MenuIcons.Create(command.Replace('_', '.')) : null;
        }
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

    /// <summary>The root node of the folder shown (null before <see cref="ShowAsync"/>).</summary>
    public TreeNodeViewModel? RootNode => _root;

    /// <summary>Every root shown: the one of the folder and, for a local folder, the other <see cref="LocalRoots"/>.</summary>
    public IReadOnlyList<TreeNodeViewModel> RootNodes => _roots;

    /// <summary>
    /// The roots shown beside a local folder's own (the folder tree window: every fixed drive of the machine, user's
    /// request 20 November 2026); null: only the folder's own root.
    /// </summary>
    public Func<IReadOnlyList<VfsPath>>? LocalRoots { get; set; }

    private static VfsPath TopOf(VfsPath path)
    {
        var root = path;
        while (root.Parent is not null)
        {
            root = root.Parent;
        }
        return root;
    }

    /// <summary>Shows the tree of the drive/root containing <paramref name="path"/> with <paramref name="path"/> selected.</summary>
    public async Task ShowAsync(VfsPath path, Func<VfsPath, Task<IReadOnlyList<VfsEntry>>> listDirectories)
    {
        var root = TopOf(path);
        var wanted = new List<VfsPath> { root };
        if (path.IsLocal && LocalRoots is { } local)
        {
            // the drives, each once (a mount below "/" is in its tree already), in the order of their names
            wanted.AddRange(local().Select(TopOf));
            wanted = [.. wanted.Distinct().OrderBy(p => p.ToDisplayString(), StringComparer.OrdinalIgnoreCase)];
        }
        if (!ReferenceEquals(_listDirectories, listDirectories) || !_roots.Select(r => r.Path).SequenceEqual(wanted))
        {
            var same = ReferenceEquals(_listDirectories, listDirectories);
            _listDirectories = listDirectories;
            _roots = [.. wanted.Select(p => same && _roots.FirstOrDefault(r => r.Path == p) is { } kept ? kept : TreeNodeViewModel.CreateRoot(p, listDirectories))];
            Tree.ItemsSource = _roots;
            _loadAllCts?.Cancel();
            if (LoadAll)
            {
                _loadAllCts = new CancellationTokenSource();
                foreach (var node in _roots)
                {
                    _ = node.LoadAllAsync(cancellationToken: _loadAllCts.Token);
                }
            }
        }
        _root = _roots.First(r => r.Path == root);
        _root.IsExpanded = true;
        await RevealAsync(path);
    }

    public async Task RevealAsync(VfsPath path)
    {
        if (_roots.FirstOrDefault(r => path.IsSameOrUnder(r.Path)) is { } owner)
        {
            _root = owner; // a folder on another drive of the tree
        }
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
        if (_roots.Select(r => Find(r, directory)).FirstOrDefault(n => n is not null) is { } found)
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
