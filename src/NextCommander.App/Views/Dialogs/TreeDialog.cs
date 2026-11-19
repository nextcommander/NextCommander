using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.ViewModels;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The folder tree (Alt+F10, nc_DirTree; also to choose a target folder): the tree of the drive, a filter of the loaded
/// folders, hidden folders on / off, chips for the user's folders, collapse all and refresh; below, the chosen folder as
/// clickable parts with "copy path", and "Go to folder".
/// </summary>
public sealed class TreeDialog : Window
{
    private readonly DirectoryTreeView _tree = new() { NavigateOnSelect = false };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter loaded folders", MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _hidden = new() { IsChecked = true, Height = 36, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(8), Classes = { "chip" } };
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = new() { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private Func<VfsPath, Task<IReadOnlyList<VfsEntry>>>? _list;

    /// <param name="subtitle">What the folder is chosen for.</param>
    public TreeDialog(string subtitle = "Pick a folder to open in the active panel")
    {
        Title = "Folder tree";
        Width = 640;
        Height = 720;
        MinWidth = 440;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // above: badge, title; collapse all, refresh
        var collapse = Flat(DialogLook.IconButton("MiMinusCircle", "Collapse all", CollapseAll));
        var refresh = Flat(DialogLook.IconButton("MiRefresh", "Refresh", RefreshAsync));
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        tools.Children.AddRange([collapse, refresh]);
        var header = DialogLook.Header("MiStacked", "Folder tree", subtitle);
        var head = new Panel();
        tools.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.AddRange([header, tools]);

        // the filter, hidden folders, the user's folders
        _filter.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiFilter", 15), Margin = new Thickness(11, 0, 2, 0), Opacity = 0.6 };
        _filter.TextChanged += (_, _) => ApplyFilter();
        UpdateHiddenButton();
        _hidden.Click += (_, _) =>
        {
            UpdateHiddenButton();
            ApplyFilter();
        };
        ToolTip.SetTip(_hidden, "Show hidden folders");
        var filterRow = new DockPanel();
        DockPanel.SetDock(_hidden, Dock.Right);
        _hidden.Margin = new Thickness(8, 0, 0, 0);
        filterRow.Children.AddRange([_hidden, _filter]);
        var places = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (name, glyph, folder) in UserFolders())
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.AddRange([DialogLook.Glyph(glyph, 13), new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center }]);
            var chip = new Button { Content = content, Height = 28, FontSize = 12, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 0), VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(chip, folder);
            chip.Click += async (_, _) => await ShowFolderAsync(VfsPath.Local(folder));
            places.Children.Add(chip);
        }
        var controls = new StackPanel { Spacing = 10, Margin = new Thickness(20, 14, 20, 10) };
        controls.Children.AddRange([filterRow, places]);
        var controlsBorder = Bordered(controls, new Thickness(0, 0, 0, 1));

        // below: the chosen folder as parts, copy; the keys; Cancel, Go to folder
        var copy = Flat(DialogLook.IconButton("MiCopy", "Copy path", CopyPathAsync));
        copy.Width = 26;
        copy.Height = 26;
        var crumbBar = new DockPanel { MinHeight = 30 };
        DockPanel.SetDock(copy, Dock.Right);
        crumbBar.Children.AddRange([copy, new ScrollViewer { Content = _crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }]);
        var crumbBox = new Border { Child = crumbBar, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 0, 4, 0) };
        crumbBox[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var hints = new TextBlock { Text = "← → collapse / expand · Enter go", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var go = new Button { Content = "Go to folder", IsDefault = true, Classes = { "dialog" } };
        go.Click += (_, _) => Choose(_tree.SelectedPath);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([cancel, go]);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        left.Children.AddRange([hints, _note]);
        var bar = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.AddRange([buttons, left]);
        var foot = new StackPanel { Spacing = 10, Margin = new Thickness(20, 12) };
        foot.Children.AddRange([crumbBox, bar]);
        var footBorder = Bordered(foot, new Thickness(0, 1, 0, 0));

        _tree.Margin = new Thickness(10, 8);
        _tree.TreeControl.FontSize = 13.5;
        _tree.TreeControl.SelectionChanged += (_, _) => ShowCrumbs();
        _tree.DirectoryActivated += (_, e) =>
        {
            if (e.Confirm)
            {
                Choose(e.Path);
            }
        };

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(controlsBorder, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([head, controlsBorder, footBorder, _tree]);
        Content = root;

        EventHandler loaded = (_, _) => ApplyFilter();
        TreeNodeViewModel.Loaded += loaded; // folders read later are filtered too
        Closed += (_, _) => TreeNodeViewModel.Loaded -= loaded;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_filter.Text))
            {
                _filter.Text = string.Empty;
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                _filter.Focus();
                e.Handled = true;
            }
        };
    }

    /// <summary>The folder chosen; null when cancelled.</summary>
    public VfsPath? Result { get; private set; }

    /// <summary>The folder selected now (tests).</summary>
    public VfsPath? Selected => _tree.SelectedPath;

    /// <summary>The tree's root node (tests).</summary>
    public TreeNodeViewModel? RootNode => _tree.RootNode;

    public string FilterText
    {
        get => _filter.Text ?? string.Empty;
        set => _filter.Text = value;
    }

    public bool ShowHidden
    {
        get => _hidden.IsChecked == true;
        set
        {
            _hidden.IsChecked = value;
            UpdateHiddenButton();
            ApplyFilter();
        }
    }

    public async Task InitializeAsync(VfsPath path, Func<VfsPath, Task<IReadOnlyList<VfsEntry>>> list)
    {
        _list = list;
        await _tree.ShowAsync(path, list);
        ApplyFilter();
        ShowCrumbs();
        _tree.TreeControl.Focus();
    }

    private async Task ShowFolderAsync(VfsPath path)
    {
        if (_list is null)
        {
            return;
        }
        await _tree.ShowAsync(path, _list);
        ApplyFilter();
        ShowCrumbs();
    }

    private void Choose(VfsPath? path)
    {
        if (path is null)
        {
            return;
        }
        Result = path;
        Close();
    }

    /// <summary>The user's folders that exist (the chips).</summary>
    private static IEnumerable<(string Name, string Glyph, string Folder)> UserFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string, string, string)[] folders =
        [
            ("Home", "MiHome", home),
            ("Desktop", "MiDesktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Documents", "MiDoc", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", "MiArrowDown", Path.Combine(home, "Downloads")),
        ];
        return folders.Where(f => !string.IsNullOrEmpty(f.Item3) && Directory.Exists(f.Item3)).DistinctBy(f => f.Item3);
    }

    private void UpdateHiddenButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.AddRange([DialogLook.Glyph("MiEye", 14), new TextBlock { Text = "Hidden", VerticalAlignment = VerticalAlignment.Center }]);
        _hidden.Content = content;
        _hidden.Opacity = _hidden.IsChecked == true ? 1 : 0.6;
    }

    /// <summary>Hides the folders the filter does not match (a folder stays while one of its loaded folders matches) and hidden ones when off.</summary>
    private void ApplyFilter()
    {
        if (_tree.RootNode is not { } root)
        {
            return;
        }
        var query = _filter.Text?.Trim() ?? string.Empty;
        var showHidden = _hidden.IsChecked == true;
        bool Apply(TreeNodeViewModel node, bool isRoot)
        {
            var anyChild = false;
            foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            {
                anyChild |= Apply(child, false);
            }
            var ownMatch = query.Length == 0 || node.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            var shown = isRoot || ((showHidden || !node.IsHiddenFolder) && (ownMatch || anyChild));
            node.IsShown = shown;
            return shown && (query.Length == 0 || ownMatch || anyChild);
        }
        Apply(root, true);
    }

    private void CollapseAll()
    {
        if (_tree.RootNode is not { } root)
        {
            return;
        }
        void Collapse(TreeNodeViewModel node)
        {
            foreach (var child in node.Children)
            {
                Collapse(child);
                child.IsExpanded = false;
            }
        }
        Collapse(root);
        _tree.TreeControl.SelectedItem = root;
    }

    private async Task RefreshAsync()
    {
        if (_tree.RootNode is not { } root || _list is null)
        {
            return;
        }
        var selected = _tree.SelectedPath ?? root.Path;
        root.Invalidate();
        await _tree.ShowAsync(selected, _list);
        ApplyFilter();
        _note.Text = "Tree refreshed.";
    }

    private async Task CopyPathAsync()
    {
        if (_tree.SelectedPath is { } path && Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, path.ToDisplayString());
            _note.Text = "Path copied.";
        }
    }

    /// <summary>The chosen folder as clickable parts (a part selects that folder).</summary>
    private void ShowCrumbs()
    {
        _crumbs.Children.Clear();
        if (_tree.SelectedPath is not { } path)
        {
            return;
        }
        var chain = new List<VfsPath>();
        for (var p = path; p is not null; p = p.Parent)
        {
            chain.Insert(0, p);
        }
        _crumbs.Children.Add(new PathIcon { Width = 14, Height = 14, Data = (Geometry?)Application.Current?.FindResource("FmFolderIcon"), Foreground = new SolidColorBrush(Color.Parse("#E8B44C")), Margin = new Thickness(0, 0, 4, 0) });
        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0)
            {
                _crumbs.Children.Add(new TextBlock { Text = "›", Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
            }
            var part = chain[i];
            var name = i == 0 ? part.ToDisplayString().TrimEnd('\\', '/') : part.Name;
            var button = new Button
            {
                Content = new TextBlock { Text = name.Length == 0 ? "/" : name, FontSize = 12.5, FontWeight = i == chain.Count - 1 ? FontWeight.SemiBold : FontWeight.Normal, Classes = { "noloc" } },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3),
                MinHeight = 0,
            };
            button.Click += async (_, _) => await _tree.RevealAsync(part);
            _crumbs.Children.Add(button);
        }
    }

    private static Button Flat(Button button)
    {
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        return button;
    }

    private static Border Bordered(Control child, Thickness border)
    {
        var box = new Border { Child = child, BorderThickness = border };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return box;
    }
}
