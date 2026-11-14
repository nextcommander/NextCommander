using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.ViewModels;

namespace NextCommander.App.Views;

/// <summary>Breadcrumb path bar; click on empty space (or Ctrl+L) switches to a text box.</summary>
public partial class PathBarView : UserControl
{
    private PanelViewModel? _vm;

    public PathBarView()
    {
        InitializeComponent();
        Crumbs.AddHandler(Button.ClickEvent, OnCrumbClick);
        Root.AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Tunnel);
        PathBox.AddHandler(KeyDownEvent, OnPathBoxKeyDown, RoutingStrategies.Tunnel);
        Completion = Services.PathAutoComplete.Attach(PathBox,
            baseFolder: () => (DataContext as ViewModels.PanelViewModel)?.CurrentPath is { IsLocal: true } here ? here.Path : null,
            settings: () => (DataContext as ViewModels.PanelViewModel)?.Settings);
        PathBox.LostFocus += (_, _) => EndEdit(navigate: false);
        PathBox.GotFocus += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        SubfoldersButton.Click += OnPathButtonClick;
        HotlistButton.Click += OnPathButtonClick;
        HistoryButton.Click += OnPathButtonClick;
        DisconnectButton.Click += OnPathButtonClick;
    }

    private IDisposable? _hoverTimer;

    /// <summary>Layout > auto-open the breadcrumb bar: resting on a separator opens its subfolder list.</summary>
    private void OnSeparatorPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_vm?.Settings.BreadcrumbAutoOpen != true || sender is not Button { Tag: BreadcrumbSegment segment } separator)
        {
            return;
        }
        _hoverTimer?.Dispose();
        _hoverTimer = DispatcherTimer.RunOnce(() => _ = ShowSubdirectoriesAsync(separator, segment), TimeSpan.FromMilliseconds(450));
    }

    /// <summary>Path line buttons: ▾ subfolders of the current folder, * hotlist, ▾ history.</summary>
    private async void OnPathButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button button)
        {
            return;
        }
        Activated?.Invoke(this, EventArgs.Empty);
        if (button == SubfoldersButton)
        {
            if (_vm.Breadcrumbs is { Count: > 0 } crumbs)
            {
                await ShowSubdirectoriesAsync(button, crumbs[^1]);
            }
            return;
        }
        if (TopLevel.GetTopLevel(this)?.DataContext is MainWindowViewModel main)
        {
            if (button == HotlistButton && TopLevel.GetTopLevel(this) is MainWindow window)
            {
                window.HotlistAnchor = button; // the menu opens below the button
            }
            await main.ExecuteAsync(button == HotlistButton ? "nc_DirectoryHotlist" : button == DisconnectButton ? "nc_FtpDisconnect" : "nc_DirectoryHistory");
        }
    }

    public event EventHandler? Activated;

    /// <summary>The user finished with the bar; the panel should take focus back.</summary>
    public event EventHandler? Done;

    public bool IsEditing => PathBox.IsVisible;

    /// <summary>Path completion while the path is typed.</summary>
    public Services.PathAutoComplete Completion { get; }

    public void BeginEdit()
    {
        if (_vm is null)
        {
            return;
        }
        _vm.PathText = _vm.CurrentPath?.ToDisplayString() ?? string.Empty;
        CrumbArea.IsVisible = false;
        PathBox.IsVisible = true;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as PanelViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        RebuildCrumbs();
    }

    /// <summary>
    /// Path segment buttons ("C:\" "Users" "\" ...). Built here instead of an ItemsControl: its panel could keep a zero
    /// width measured before the items existed, hiding the path.
    /// </summary>
    private void RebuildCrumbs()
    {
        Crumbs.Children.Clear();
        if (_vm is null)
        {
            return;
        }
        foreach (var segment in _vm.Breadcrumbs)
        {
            var crumb = new Button { Content = segment.Name, Tag = segment, Focusable = false };
            crumb.Classes.Add("crumb");
            if (segment.IsLast)
            {
                crumb.Classes.Add("last");
            }
            Crumbs.Children.Add(crumb);
            if (segment.Separator.Length > 0)
            {
                var separator = new Button { Content = segment.Separator, Tag = segment, Focusable = false };
                separator.Classes.Add("crumbsep");
                ToolTip.SetTip(separator, "Subfolders");
                separator.PointerEntered += OnSeparatorPointerEntered;
                separator.PointerExited += (_, _) => _hoverTimer?.Dispose();
                Crumbs.Children.Add(separator);
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.Breadcrumbs))
        {
            RebuildCrumbs();
            // Keep the deepest folder visible.
            Dispatcher.UIThread.Post(() => CrumbScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Activated?.Invoke(this, EventArgs.Empty);
        if (!IsEditing && (e.Source as Avalonia.Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginEdit();
            e.Handled = true;
        }
    }

    private async void OnCrumbClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || e.Source is not Button { Tag: BreadcrumbSegment segment } button)
        {
            return;
        }
        Activated?.Invoke(this, EventArgs.Empty);
        if (button.Classes.Contains("crumbsep"))
        {
            await ShowSubdirectoriesAsync(button, segment);
            return;
        }
        if (_vm.CurrentPath != segment.Path)
        {
            // Going up: put the cursor on the folder we came from.
            var child = _vm.CurrentPath is { } current && current.IsSameOrUnder(segment.Path)
                ? current.GetRelativeTo(segment.Path)?.Split('/')[0]
                : null;
            await _vm.NavigateAsync(segment.Path, focusName: child);
        }
        Done?.Invoke(this, EventArgs.Empty);
    }

    private async Task ShowSubdirectoriesAsync(Button anchor, BreadcrumbSegment segment)
    {
        var vm = _vm!;
        var flyout = new MenuFlyout();
        var dirs = await vm.ListSubdirectoriesAsync(segment.Path);
        if (dirs.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(no subfolders)", IsEnabled = false });
        }
        foreach (var dir in dirs)
        {
            var item = new MenuItem { Header = dir.Name };
            var path = dir.Path;
            item.Click += async (_, _) =>
            {
                await vm.NavigateAsync(path);
                Done?.Invoke(this, EventArgs.Empty);
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private async void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await EndEditAndNavigateAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndEdit(navigate: false);
            Done?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EndEditAndNavigateAsync()
    {
        var text = PathBox.Text ?? string.Empty;
        EndEdit(navigate: true);
        Done?.Invoke(this, EventArgs.Empty);
        if (_vm is not null)
        {
            await _vm.NavigateToTextAsync(text);
        }
    }

    private void EndEdit(bool navigate)
    {
        if (!IsEditing)
        {
            return;
        }
        PathBox.IsVisible = false;
        CrumbArea.IsVisible = true;
        if (!navigate && _vm is not null)
        {
            _vm.PathText = _vm.CurrentPath?.ToDisplayString() ?? string.Empty;
        }
    }
}
