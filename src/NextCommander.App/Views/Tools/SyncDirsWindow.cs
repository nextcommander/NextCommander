using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.Core.Compare;
using NextCommander.Core.Formatting;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>One row of the synchronize dirs list.</summary>
public sealed class SyncRow(SyncItem item) : INotifyPropertyChanged
{
    public SyncItem Item { get; } = item;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path => Item.RelativePath + (Item.IsDirectory ? "/" : string.Empty);

    public string LeftInfo => Info(Item.Left);

    public string RightInfo => Info(Item.Right);

    public string StatusSymbol => Item.Status switch
    {
        SyncStatus.Equal => "=",
        SyncStatus.LeftNewer => "> newer",
        SyncStatus.RightNewer => "newer <",
        SyncStatus.Different => "≠",
        SyncStatus.LeftOnly => "left only",
        SyncStatus.RightOnly => "right only",
        _ => "file/folder!",
    };

    public string ActionSymbol => Item.Action switch
    {
        SyncAction.CopyToRight => "→",
        SyncAction.CopyToLeft => "←",
        SyncAction.DeleteLeft => "✕ left",
        SyncAction.DeleteRight => "right ✕",
        _ => string.Empty,
    };

    public IBrush StatusBrush => Item.Status switch
    {
        SyncStatus.Equal => Brushes.Gray,
        SyncStatus.LeftNewer or SyncStatus.LeftOnly => Brushes.SeaGreen,
        SyncStatus.RightNewer or SyncStatus.RightOnly => Brushes.SteelBlue,
        _ => Brushes.IndianRed,
    };

    public void SetAction(SyncAction action)
    {
        Item.Action = action;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionSymbol)));
    }

    private static string Info(VfsEntry? e) => e is null ? string.Empty
        : (e.IsDirectory ? "<DIR>" : SizeFormatter.Bytes(e.Size)) + "  " + e.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>Synchronize dirs: compare two trees, choose the direction per file, copy / delete.</summary>
public sealed class SyncDirsWindow : Window
{
    private readonly ToolContext _context;
    private readonly TextBox _leftPath = new();
    private readonly TextBox _rightPath = new();
    private readonly TextBox _mask = new() { Width = 220 };
    private readonly CheckBox _subdirs = new() { Content = "Subfolders" };
    private readonly CheckBox _byContent = new() { Content = "By content" };
    private readonly CheckBox _ignoreDate = new() { Content = "Ignore date" };
    private readonly CheckBox _asymmetric = new() { Content = "One-way (left → right)" };
    private readonly CheckBox _skipHidden = new() { Content = "Skip hidden" };
    private readonly ToggleButton _showEqual = new() { Content = "=", IsChecked = false };
    private readonly ToggleButton _showDifferent = new() { Content = "≠", IsChecked = true };
    private readonly ToggleButton _showLeft = new() { Content = "Left only", IsChecked = true };
    private readonly ToggleButton _showRight = new() { Content = "Right only", IsChecked = true };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 3), FontSize = 12 };
    private List<SyncRow> _all = [];
    private CancellationTokenSource? _cts;

    public SyncDirsWindow(VfsPath left, VfsPath right, ToolContext context)
    {
        _context = context;
        Title = "Synchronize directories";
        Width = 1150;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _leftPath.Text = left.ToDisplayString();
        _rightPath.Text = right.ToDisplayString();
        var o = context.Settings.SyncOptions;
        _subdirs.IsChecked = o.Subdirectories;
        _byContent.IsChecked = o.ByContent;
        _ignoreDate.IsChecked = o.IgnoreDate;
        _asymmetric.IsChecked = o.Asymmetric;
        _skipHidden.IsChecked = o.SkipHidden;
        _mask.Text = o.Mask;

        _list.ItemsSource = Rows;
        _list.ItemTemplate = new FuncDataTemplate<SyncRow>((_, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,170,90,50,170") };
            TextBlock Cell(string path, int column, HorizontalAlignment align = HorizontalAlignment.Left)
            {
                var t = new TextBlock { TextTrimming = TextTrimming.LeadingCharacterEllipsis, HorizontalAlignment = align, FontSize = 12.5 };
                t.Bind(TextBlock.TextProperty, new Binding(path));
                Grid.SetColumn(t, column);
                grid.Children.Add(t);
                return t;
            }
            Cell(nameof(SyncRow.Path), 0);
            Cell(nameof(SyncRow.LeftInfo), 1, HorizontalAlignment.Right);
            var status = Cell(nameof(SyncRow.StatusSymbol), 2, HorizontalAlignment.Center);
            status.Bind(TextBlock.ForegroundProperty, new Binding(nameof(SyncRow.StatusBrush)));
            var action = Cell(nameof(SyncRow.ActionSymbol), 3, HorizontalAlignment.Center);
            action.FontWeight = FontWeight.Bold;
            Cell(nameof(SyncRow.RightInfo), 4);
            return grid;
        });
        _list.KeyDown += OnListKeyDown;
        _list.DoubleTapped += (_, _) => CompareSelected();
        foreach (var toggle in new[] { _showEqual, _showDifferent, _showLeft, _showRight })
        {
            toggle.IsCheckedChanged += (_, _) => ApplyFilter();
        }

        Content = BuildLayout();
        Opened += (_, _) => _leftPath.Focus();
        Closed += (_, _) => _cts?.Cancel();
    }

    public ObservableCollection<SyncRow> Rows { get; } = [];

    public IReadOnlyList<SyncRow> AllRows => _all;

    public string StatusText => _status.Text ?? string.Empty;

    private Control BuildLayout()
    {
        static TextBlock Caption(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
        StackPanel Row(params Control[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.AddRange(children);
            foreach (var c in children.OfType<CheckBox>())
            {
                c.VerticalAlignment = VerticalAlignment.Center;
            }
            return panel;
        }
        Button B(string text, Action action)
        {
            var b = new Button { Content = text };
            b.Click += (_, _) => action();
            return b;
        }

        var paths = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 8 };
        paths.Children.Add(_leftPath);
        var swap = B("⇄", () => (_leftPath.Text, _rightPath.Text) = (_rightPath.Text, _leftPath.Text));
        Grid.SetColumn(swap, 1);
        paths.Children.Add(swap);
        Grid.SetColumn(_rightPath, 2);
        paths.Children.Add(_rightPath);

        var compare = new Button { Content = "Compare", IsDefault = true };
        compare.Click += async (_, _) => await CompareAsync();
        var sync = B("Synchronize...", () => _ = SynchronizeAsync());
        var top = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
        top.Children.Add(paths);
        top.Children.Add(Row(_subdirs, _byContent, _ignoreDate, _asymmetric, _skipHidden, Caption("Mask:"), _mask));
        top.Children.Add(Row(compare, sync, Caption("   Show:"), _showEqual, _showDifferent, _showLeft, _showRight,
            Caption("   Selected:"), B("→", () => SetAction(SyncAction.CopyToRight)), B("←", () => SetAction(SyncAction.CopyToLeft)),
            B("None", () => SetAction(SyncAction.None)), B("Delete left", () => SetAction(SyncAction.DeleteLeft)),
            B("Delete right", () => SetAction(SyncAction.DeleteRight)), B("Compare by content", CompareSelected)));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,170,90,50,170"), Margin = new Thickness(12, 2) };
        foreach (var (text, column, align) in new[] { ("Name", 0, HorizontalAlignment.Left), ("Left", 1, HorizontalAlignment.Right),
                     ("Status", 2, HorizontalAlignment.Center), ("Operation", 3, HorizontalAlignment.Center), ("Right", 4, HorizontalAlignment.Left) })
        {
            var cell = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 12, HorizontalAlignment = align };
            Grid.SetColumn(cell, column);
            header.Children.Add(cell);
        }
        var headerBorder = new Border { Child = header };
        headerBorder[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmPanelHeaderBrush");
        top.Children.Add(headerBorder);
        top.Margin = new Thickness(10, 10, 10, 0);

        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(statusBar);
        root.Children.Add(_list);
        return root;
    }

    private SyncOptions ReadOptions() => new()
    {
        Subdirectories = _subdirs.IsChecked == true,
        ByContent = _byContent.IsChecked == true,
        IgnoreDate = _ignoreDate.IsChecked == true,
        Asymmetric = _asymmetric.IsChecked == true,
        SkipHidden = _skipHidden.IsChecked == true,
        Mask = string.IsNullOrWhiteSpace(_mask.Text) ? "*" : _mask.Text.Trim(),
    };

    private VfsPath? Resolve(string? text) =>
        VfsPath.TryParse(text?.Trim().Trim('"'), out var p) ? _context.Registry.MapIntoContainers(p!) : null;

    public async Task CompareAsync()
    {
        if (Resolve(_leftPath.Text) is not { } left || Resolve(_rightPath.Text) is not { } right)
        {
            _status.Text = "Invalid folder path.";
            return;
        }
        var options = ReadOptions();
        _context.Settings.SyncOptions = options;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new Progress<string>(p => _status.Text = "Comparing: " + p);
        try
        {
            var items = await Task.Run(() => new DirectoryComparer(_context.Registry, options).CompareAsync(left, right, progress, cts.Token), cts.Token);
            _all = items.Select(i => new SyncRow(i)).ToList();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _status.Text = ex.Message;
            return;
        }
        ApplyFilter();
        var files = _all.Where(r => !r.Item.IsDirectory).ToList();
        _status.Text = string.Format(CultureInfo.CurrentCulture,
            "{0} files: {1} identical, {2} different, {3} left only, {4} right only. {5} operations selected.",
            files.Count, files.Count(r => r.Item.Status == SyncStatus.Equal),
            files.Count(r => r.Item.Status is SyncStatus.LeftNewer or SyncStatus.RightNewer or SyncStatus.Different or SyncStatus.TypeMismatch),
            files.Count(r => r.Item.Status == SyncStatus.LeftOnly), files.Count(r => r.Item.Status == SyncStatus.RightOnly),
            _all.Count(r => r.Item.Action != SyncAction.None));
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var row in _all)
        {
            var show = row.Item.Status switch
            {
                SyncStatus.Equal => _showEqual.IsChecked == true,
                SyncStatus.LeftOnly => _showLeft.IsChecked == true,
                SyncStatus.RightOnly => _showRight.IsChecked == true,
                _ => _showDifferent.IsChecked == true,
            };
            if (show)
            {
                Rows.Add(row);
            }
        }
    }

    public void SetAction(SyncAction action)
    {
        foreach (var row in _list.SelectedItems?.OfType<SyncRow>().ToList() ?? [])
        {
            var possible = action switch
            {
                SyncAction.CopyToRight or SyncAction.DeleteLeft => row.Item.Left is not null,
                SyncAction.CopyToLeft or SyncAction.DeleteRight => row.Item.Right is not null,
                _ => true,
            };
            if (possible)
            {
                row.SetAction(action);
            }
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
                SetAction(SyncAction.CopyToRight);
                e.Handled = true;
                break;
            case Key.Left:
                SetAction(SyncAction.CopyToLeft);
                e.Handled = true;
                break;
            case Key.Back or Key.Delete:
                SetAction(SyncAction.None);
                e.Handled = true;
                break;
            case Key.Enter:
                CompareSelected();
                e.Handled = true;
                break;
        }
    }

    private void CompareSelected()
    {
        if (_list.SelectedItem is SyncRow { Item: { Left: { IsDirectory: false } l, Right: { IsDirectory: false } r } })
        {
            new CompareWindow(l, r, _context).Show(this);
        }
    }

    public async Task SynchronizeAsync(bool confirm = true)
    {
        if (Resolve(_leftPath.Text) is not { } left || Resolve(_rightPath.Text) is not { } right)
        {
            return;
        }
        var work = _all.Select(r => r.Item).Where(i => i.Action != SyncAction.None).ToList();
        if (work.Count == 0)
        {
            _status.Text = "No operation selected.";
            return;
        }
        var deletes = work.Count(i => i.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
        var message = $"{work.Count(i => i.Action == SyncAction.CopyToRight)} items will be copied to the right, {work.Count(i => i.Action == SyncAction.CopyToLeft)} items to the left"
            + (deletes > 0 ? $", {deletes} items WILL BE DELETED." : ".");
        if (confirm && !await _context.Dialogs.ConfirmAsync("Synchronize", message, "Synchronize"))
        {
            return;
        }
        var job = new SyncJob(_context.Registry, work, left, right) { Interaction = new UiJobInteraction(_context.Dialogs) };
        var running = job.RunAsync();
        await _context.Dialogs.TrackJobAsync(job, running);
        await _context.Registry.FlushAsync();
        await CompareAsync();
        if (job.Errors.Count > 0)
        {
            _status.Text = $"{job.Errors.Count} errors: {job.Errors[0].Item}: {job.Errors[0].Message}";
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
