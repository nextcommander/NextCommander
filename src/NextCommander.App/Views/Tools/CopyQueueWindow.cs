using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Formatting;
using NextCommander.Core.Localization;
using NextCommander.Core.Operations;
using NextCommander.Vfs.Archive;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// The queue (F2 Queue, "Add to queue"; the look of the user's template, 20 November 2026): its state with Pause all,
/// the progress of all jobs still to do with the speed and the time left; how many jobs run at a time and a speed limit;
/// the jobs as cards (up next, in their order: drag the grip or Alt+↑ / ↓ to change it, each with its own pause, "run
/// next" and cancel; finished: done, failed or cancelled, with the folder to open); a count per state and what happens
/// when the queue is done.
/// </summary>
public sealed class CopyQueueWindow : Window
{
    private static readonly IBrush s_accent = new SolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush s_ok = new SolidColorBrush(Color.Parse("#2E9E44"));
    private static readonly IBrush s_warn = new SolidColorBrush(Color.Parse("#C98A00"));
    private static readonly IBrush s_bad = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush s_dim = new SolidColorBrush(Color.FromArgb(150, 128, 128, 128));

    /// <summary>The speed limits offered (bytes per second; 0: none).</summary>
    private static readonly (string Text, long Bytes)[] s_limits =
        [("Unlimited", 0), ("200 MB/s", 200L << 20), ("50 MB/s", 50L << 20), ("10 MB/s", 10L << 20), ("5 MB/s", 5L << 20), ("1 MB/s", 1L << 20)];

    private readonly JobQueue _jobs;
    private readonly Func<Task> _addSelection;
    private readonly Func<string, Task>? _openFolder;
    private readonly Border _stateIcon = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _pill = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _pillBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _subtitle = new() { FontSize = 12.5, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly Button _pauseAll = new() { Classes = { "tb", "primary" } };
    private readonly StackPanel _totals = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _totalLabel = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
    private readonly TextBlock _totalSize = new() { FontSize = 12.5, Opacity = 0.8, Classes = { "noloc" } };
    private readonly ProgressBar _totalBar = new() { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, CornerRadius = new CornerRadius(3) };
    private readonly TextBlock _speed = new() { FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
    private readonly TextBlock _left = new() { FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
    private readonly TextBlock _parallelText = new() { FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
    private readonly Border _toolbar = new() { Padding = new Thickness(20, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
    private readonly List<ToggleButton> _parallel = [];
    private readonly ComboBox _limit = new() { MinWidth = 130 };
    private readonly Button _clear = new() { Classes = { "tb", "ghost" } };
    private readonly StackPanel _list = new() { Margin = new Thickness(20, 12, 20, 16) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly ContentControl _empty = new();
    private readonly StackPanel _counts = new() { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _whenDone = new() { MinWidth = 160 };
    private readonly Dictionary<FileJob, JobCard> _cards = [];
    private readonly DispatcherTimer _timer;
    private string _layoutKey = string.Empty;
    private FileJob? _selected;
    private FileJob? _dragged;
    private bool _hadWork;
    private bool _updating;

    public CopyQueueWindow(JobQueue jobs, Func<Task> addSelection, Func<string, Task>? openFolder = null)
    {
        _jobs = jobs;
        _addSelection = addSelection;
        _openFolder = openFolder;
        Title = "Queue";
        Width = 820;
        Height = 720;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = BuildLayout();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh());
        Opened += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Closes by itself when the queue is done (after it had work): "When done: Close this window".</summary>
    public bool CloseWhenDone
    {
        get => _whenDone.SelectedIndex == 1;
        set => _whenDone.SelectedIndex = value ? 1 : 0;
    }

    // ---------------------------------------------------------------- layout

    private static Control Glyph(string key, double size, IBrush? brush = null) =>
        (key.StartsWith("nc_", StringComparison.Ordinal) ? MenuIcons.Create(key) : null) is { } command
            ? new Viewbox { Width = size, Height = size, Child = command }
            : IconPacks.Glyph(key, IconPacks.Current) is { } geometry
                ? new Viewbox { Width = size, Height = size, Child = IconPacks.Draw(geometry, brush ?? Brushes.Gray) }
                : new Panel { Width = size, Height = size };

    private static Control WithText(Control icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    private static Border Line(Control child, Thickness border, Thickness padding)
    {
        var line = new Border { Child = child, BorderThickness = border, Padding = padding };
        line[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return line;
    }

    private Control BuildLayout()
    {
        // ---- the head: the state, Pause all, the totals
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        _pillBox.Child = _pill;
        title.Children.AddRange([new TextBlock { Text = "Queue", FontSize = 17, FontWeight = FontWeight.SemiBold }, _pillBox]);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.AddRange([title, _subtitle]);
        _pauseAll.Click += (_, _) => ToggleStart();
        var row = new DockPanel();
        DockPanel.SetDock(_stateIcon, Dock.Left);
        DockPanel.SetDock(_pauseAll, Dock.Right);
        _pauseAll.VerticalAlignment = VerticalAlignment.Center;
        _pauseAll.Margin = new Thickness(12, 0, 0, 0);
        row.Children.AddRange([_stateIcon, _pauseAll, titles]);

        var label = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(_totalSize, Dock.Right);
        label.Children.AddRange([_totalSize, _totalLabel]);
        var stats = new WrapPanel { ItemSpacing = 18, Margin = new Thickness(0, 8, 0, 0) };
        Control Stat(string name, TextBlock value)
        {
            var stat = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            stat.Children.AddRange([new TextBlock { Text = name, FontSize = 12, Opacity = 0.75 }, value]);
            value.FontSize = 12;
            return stat;
        }
        var parallel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        _parallelText.FontSize = 12;
        parallel.Children.AddRange([new TextBlock { Text = "Running", FontSize = 12, Opacity = 0.75 }, _parallelText, new TextBlock { Text = "at a time", FontSize = 12, Opacity = 0.75 }]);
        stats.Children.AddRange([Stat("Speed", _speed), Stat("Time left", _left), parallel]);
        _totals.Children.AddRange([label, _totalBar, stats]);
        var headPanel = new StackPanel();
        headPanel.Children.AddRange([row, _totals]);
        var head = Line(headPanel, new Thickness(0, 0, 0, 1), new Thickness(20, 18, 20, 14));

        // ---- the toolbar: jobs at a time, the speed limit, add the selection, clear finished
        var segment = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (var n = 1; n <= 3; n++)
        {
            var count = n;
            var toggle = new ToggleButton
            {
                Content = n.ToString(CultureInfo.InvariantCulture), MinWidth = 30, Height = 26, MinHeight = 26, Padding = new Thickness(8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 12.5, Classes = { "noloc" },
            };
            toggle.Click += (_, _) => SetParallel(count);
            _parallel.Add(toggle);
            segment.Children.Add(toggle);
        }
        var runGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        runGroup.Children.AddRange([new TextBlock { Text = "Run", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }, segment, new TextBlock { Text = "at a time", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }]);
        ToolTip.SetTip(runGroup, "How many jobs of the queue run at the same time");
        FillLimits();
        _limit.SelectionChanged += (_, _) =>
        {
            if (!_updating && _limit.SelectedItem is ComboBoxItem { Tag: long bytes })
            {
                _jobs.SpeedLimit = bytes;
            }
        };
        var limitGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        limitGroup.Children.AddRange([new TextBlock { Text = "Speed limit", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }, _limit]);
        var divider = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        divider[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var add = new Button { Content = WithText(Glyph("MiPlus", 13), "Add the selection"), Classes = { "tb", "ghost" } };
        ToolTip.SetTip(add, "Queue the files selected in the active panel (copied to the other panel)");
        add.Click += async (_, _) =>
        {
            await _addSelection();
            Refresh();
        };
        _clear.Click += (_, _) => ClearFinished();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        right.Children.Add(add);
        var bar = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        var left = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        left.Children.AddRange([runGroup, divider, limitGroup]);
        bar.Children.AddRange([right, left]);
        _toolbar.Child = bar;
        _toolbar[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        // ---- the list, or what to do when it is empty
        _scroll.Content = _list;
        _empty.Content = EmptyView();
        var middle = new Panel();
        middle.Children.AddRange([_scroll, _empty]);

        // ---- the footer: a count per state, when done
        _whenDone.ItemsSource = new[] { "Do nothing", "Close this window" };
        _whenDone.SelectedIndex = 1;
        var done = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        done.Children.AddRange([new TextBlock { Text = "When done", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }, _whenDone]);
        var foot = new DockPanel();
        DockPanel.SetDock(done, Dock.Right);
        foot.Children.AddRange([done, _counts]);
        var footer = Line(foot, new Thickness(0, 1, 0, 0), new Thickness(20, 10));
        footer[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(_toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([head, _toolbar, footer, middle]);
        var background = new Border { Child = root };
        background[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        return background;
    }

    private void FillLimits()
    {
        var items = s_limits.Select(l => new ComboBoxItem { Content = l.Text, Tag = l.Bytes }).ToList();
        var now = _jobs.SpeedLimit;
        if (now > 0 && s_limits.All(l => l.Bytes != now))
        {
            // a limit set before in kB/s stays choosable
            items.Add(new ComboBoxItem { Content = SizeFormatter.Human(now) + "/s", Tag = now, Classes = { "noloc" } });
        }
        _updating = true;
        _limit.ItemsSource = items;
        _limit.SelectedItem = items.First(i => (long)i.Tag! == now);
        _updating = false;
    }

    private static Control EmptyView()
    {
        var art = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 0, 14), Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)), HorizontalAlignment = HorizontalAlignment.Center };
        art.Child = Glyph("MiStacked", 34);
        var how = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 8 };
        (string Key, string Text)[] ways =
        [
            ("F2", "In the Copy or Move dialog: add to the queue"),
            ("Button", "“Add to queue” in the Pack and Copy dialogs"),
            ("+", "“Add the selection” above: the files selected in the active panel"),
        ];
        for (var i = 0; i < ways.Length; i++)
        {
            var key = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(6, 0), MinWidth = 20, HorizontalAlignment = HorizontalAlignment.Left };
            key[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            key.Child = new TextBlock { Text = ways[i].Key, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            var text = new TextBlock { Text = ways[i].Text, FontSize = 12.5, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(key, i);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            how.Children.AddRange([key, text]);
        }
        var box = new Border { Child = how, Padding = new Thickness(16, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460, Margin = new Thickness(30) };
        panel.Children.AddRange([
            art,
            new TextBlock { Text = "The queue is empty.", FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) },
            new TextBlock { Text = "Copy, move, pack and upload jobs you add here run in the background, in their order, so the panels stay free.", FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 16) },
            box,
        ]);
        return panel;
    }

    // ---------------------------------------------------------------- the jobs

    /// <summary>What a job does, by its kind: the icon, the word while it runs.</summary>
    private static (string Glyph, string Verb) KindOf(FileJob job) => job switch
    {
        PackJob => ("nc_PackFiles", "Packing"),
        DeleteJob => ("MiTrash", "Deleting"),
        CopyMoveJob { IsMove: true } => ("MiArrowRight", "Moving"),
        CopyMoveJob c when c.Sources.Count > 0 && c.Sources[0].Path.Container is not null => ("nc_UnpackFiles", "Unpacking"),
        CopyMoveJob c when !c.TargetDirectory.IsLocal => ("MiArrowUp", "Uploading"),
        CopyMoveJob c when c.Sources.Count > 0 && !c.Sources[0].Path.IsLocal => ("MiArrowDown", "Downloading"),
        CopyMoveJob => ("MiCopy", "Copying"),
        _ => ("MiStacked", "Running"),
    };

    /// <summary>The folder a finished job wrote to (Open destination), or null.</summary>
    private static string? DestinationOf(FileJob job) => job switch
    {
        CopyMoveJob c => c.TargetDirectory.ToDisplayString(),
        PackJob p => p.Target.Parent?.ToDisplayString(),
        _ => null,
    };

    private static int ItemsOf(FileJob job, ProgressSnapshot p) => p.TotalFiles > 0 ? p.TotalFiles : job switch
    {
        CopyMoveJob c => c.Sources.Count,
        PackJob p2 => p2.Sources.Count,
        DeleteJob d => d.Sources.Count,
        _ => 0,
    };

    private static string Time(TimeSpan time) => time.TotalHours >= 1
        ? ((int)time.TotalHours).ToString(CultureInfo.InvariantCulture) + time.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
        : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>The card of one job: icon, title and state, where from and to, the progress, its buttons.</summary>
    private sealed class JobCard
    {
        public required Border Root { get; init; }
        public required Border IconBox { get; init; }
        public required TextBlock Tag { get; init; }
        public required Border TagBox { get; init; }
        public required TextBlock Route { get; init; }
        public required TextBlock Size { get; init; }
        public required StackPanel Progress { get; init; }
        public required ProgressBar Bar { get; init; }
        public required TextBlock Current { get; init; }
        public required TextBlock Amount { get; init; }
        public required TextBlock Error { get; init; }
        public required StackPanel Actions { get; init; }
        public required Control Grip { get; init; }
        public string ActionsKey { get; set; } = string.Empty;
    }

    private JobCard CardOf(FileJob job)
    {
        if (_cards.TryGetValue(job, out var card))
        {
            return card;
        }
        var grip = new TextBlock { Text = "⠿", FontSize = 15, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Cursor = new Cursor(StandardCursorType.SizeAll), Classes = { "noloc" } };
        ToolTip.SetTip(grip, "Drag to change the order (Alt+↑ / ↓)");
        grip.PointerPressed += async (_, e) =>
        {
            if (job.State != JobState.Pending || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                return;
            }
            _dragged = job;
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText("job:" + job.Id.ToString(CultureInfo.InvariantCulture)));
            try
            {
                await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Serilog.Log.Warning(ex, "Drag and drop failed");
            }
            finally
            {
                _dragged = null;
                Refresh();
            }
        };
        var iconBox = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Child = Glyph(KindOf(job).Glyph, 18) };
        var name = new TextBlock { Text = TitleOf(job), FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        ToolTip.SetTip(name, TitleOf(job));
        var tag = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        var tagBox = new Border { Child = tag, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var top = new DockPanel();
        DockPanel.SetDock(tagBox, Dock.Right);
        top.Children.AddRange([tagBox, name]);
        tagBox.HorizontalAlignment = HorizontalAlignment.Left;
        var route = new TextBlock { FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.PrefixCharacterEllipsis, Classes = { "noloc" } };
        var size = new TextBlock { FontSize = 12, Opacity = 0.75, Margin = new Thickness(8, 0, 0, 0), Classes = { "noloc" } };
        var routeRow = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(size, Dock.Right);
        routeRow.Children.AddRange([size, route]);
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6, 0, 0) };
        var current = new TextBlock { FontSize = 11.5, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        var amount = new TextBlock { FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(10, 0, 0, 0), Classes = { "noloc" } };
        var meta = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        DockPanel.SetDock(amount, Dock.Right);
        meta.Children.AddRange([amount, current]);
        var progress = new StackPanel();
        progress.Children.AddRange([bar, meta]);
        var error = new TextBlock { FontSize = 12, Foreground = s_bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Classes = { "noloc" } };
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        middle.Children.AddRange([top, routeRow, progress, error]);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,36,*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(iconBox, 1);
        Grid.SetColumn(middle, 2);
        Grid.SetColumn(actions, 3);
        grid.Children.AddRange([grip, iconBox, middle, actions]);
        var root = new Border { Child = grid, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 6) };
        root[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush");
        root.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is null)
            {
                _selected = ReferenceEquals(_selected, job) ? null : job;
                Focus();
                Refresh();
            }
        };
        DragDrop.SetAllowDrop(root, true);
        root.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _dragged is not null && job.State == JobState.Pending && !ReferenceEquals(job, _dragged) ? DragDropEffects.Move : DragDropEffects.None);
        root.AddHandler(DragDrop.DropEvent, (_, _) =>
        {
            if (_dragged is { } dragged)
            {
                MoveBefore(dragged, job);
            }
        });
        card = new JobCard
        {
            Root = root, IconBox = iconBox, Tag = tag, TagBox = tagBox, Route = route, Size = size, Progress = progress, Bar = bar,
            Current = current, Amount = amount, Error = error, Actions = actions, Grip = grip,
        };
        _cards[job] = card;
        return card;
    }

    /// <summary>A waiting job dropped on another waiting one takes its place.</summary>
    public void MoveBefore(FileJob dragged, FileJob target)
    {
        var from = _jobs.PlaceOf(dragged);
        var to = _jobs.PlaceOf(target);
        if (from > 0 && to > 0 && from != to)
        {
            _jobs.Move(dragged, to - from);
            Refresh();
        }
    }

    private Button Action(string glyph, string tip, Action click)
    {
        var button = new Button { Content = glyph.Length <= 2 ? new TextBlock { Text = glyph, FontSize = 12, Classes = { "noloc" } } : Glyph(glyph, 14), Classes = { "tb", "ghost", "icon" }, Width = 28, Height = 28, MinHeight = 28 };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            click();
            Refresh();
        };
        return button;
    }

    private void UpdateCard(FileJob job, int place, DateTime? finished)
    {
        var card = CardOf(job);
        var p = job.Progress.Snapshot();
        var culture = CultureInfo.CurrentCulture;
        var waiting = job.State == JobState.Pending;
        var (glyph, verb) = KindOf(job);
        _ = glyph;
        card.Grip.IsVisible = waiting;
        (string Text, IBrush? Brush) state = job.State switch
        {
            JobState.Running => (Localizer.T(verb), s_accent),
            JobState.Paused => (Localizer.T("Paused"), s_warn),
            JobState.Pending => (Localizer.T("#{0} in line").Replace("{0}", place.ToString(culture), StringComparison.Ordinal), null),
            JobState.Completed => (Localizer.T("Done") + (finished is { } t ? " · " + t.ToString("t", culture) : string.Empty), s_ok),
            JobState.CompletedWithErrors => (Localizer.T("Done with errors") + (finished is { } t2 ? " · " + t2.ToString("t", culture) : string.Empty), s_warn),
            JobState.Failed => (Localizer.T("Failed"), s_bad),
            _ => (Localizer.T("Cancelled"), null),
        };
        card.Tag.Text = state.Text;
        if (state.Brush is { } brush)
        {
            card.Tag.Foreground = brush;
        }
        else
        {
            card.Tag.ClearValue(TextBlock.ForegroundProperty);
        }
        card.TagBox.Background = new SolidColorBrush(state.Brush is ISolidColorBrush s ? Color.FromArgb(36, s.Color.R, s.Color.G, s.Color.B) : Color.FromArgb(18, 128, 128, 128));
        card.IconBox.Background = card.TagBox.Background;

        var items = ItemsOf(job, p);
        card.Route.Text = job.Route is { } r ? r.Source + "  →  " + Localizer.T(r.Target) : string.Empty;
        ToolTip.SetTip(card.Route, card.Route.Text);
        var parts = new List<string>();
        if (items > 0)
        {
            parts.Add(Localizer.T(items == 1 ? "1 item" : "{0} items").Replace("{0}", items.ToString("N0", culture), StringComparison.Ordinal));
        }
        if (p.TotalBytes > 0)
        {
            parts.Add(SizeFormatter.Human(p.TotalBytes));
        }
        card.Size.Text = parts.Count > 0 ? "·  " + string.Join("  ·  ", parts) : string.Empty;

        var showProgress = job.State is JobState.Running or JobState.Paused;
        card.Progress.IsVisible = showProgress;
        if (showProgress)
        {
            card.Bar.Value = p.Fraction;
            card.Bar.Foreground = job.State == JobState.Paused ? s_warn : s_accent;
            card.Current.Text = p.IsCounting ? Localizer.T("Counting...") : p.CurrentItem ?? string.Empty;
            card.Amount.Text = p.TotalBytes > 0
                ? Localizer.T("{0} of {1}").Replace("{0}", SizeFormatter.Human(p.DoneBytes), StringComparison.Ordinal).Replace("{1}", SizeFormatter.Human(p.TotalBytes), StringComparison.Ordinal)
                    + (job.State == JobState.Running && p.BytesPerSecond > 1 ? " · " + SizeFormatter.Human((long)p.BytesPerSecond) + "/s" : string.Empty)
                    + (job.State == JobState.Running && p.Remaining is { } rest ? " · " + Localizer.T("{0} left").Replace("{0}", Time(rest), StringComparison.Ordinal) : string.Empty)
                : p.Fraction.ToString("P0", culture);
        }
        var error = job.State == JobState.Failed ? job.Failure?.Message
            : job.State == JobState.CompletedWithErrors && job.Errors.Count > 0 ? job.Errors[0].Message : null;
        card.Error.Text = error ?? string.Empty;
        card.Error.IsVisible = error is not null;

        // its buttons: pause / resume, run next, open the destination, cancel or remove
        var destination = DestinationOf(job);
        var key = $"{job.State}|{place > 1}|{destination is not null && _openFolder is not null}";
        if (key != card.ActionsKey)
        {
            card.ActionsKey = key;
            card.Actions.Children.Clear();
            if (job.State == JobState.Running)
            {
                card.Actions.Children.Add(Action("❚❚", "Pause this job", () => _jobs.TogglePause(job)));
            }
            if (job.State == JobState.Paused)
            {
                card.Actions.Children.Add(Action("▶", "Resume", () => _jobs.TogglePause(job)));
            }
            if (waiting && place > 1)
            {
                card.Actions.Children.Add(Action("MiArrowUp", "Run next", () => _jobs.MoveToFront(job)));
            }
            if (finished is not null && destination is not null && _openFolder is { } open && job.State is JobState.Completed or JobState.CompletedWithErrors)
            {
                card.Actions.Children.Add(Action("MiFolderOpen", "Open destination", () => _ = open(destination)));
            }
            card.Actions.Children.Add(Action("MiX", job.State is JobState.Running or JobState.Paused ? "Cancel job" : finished is not null ? "Remove from the list" : "Remove from the queue",
                () => Remove(job)));
        }
        if (ReferenceEquals(_selected, job))
        {
            card.Root.BorderBrush = s_accent;
        }
        else
        {
            card.Root[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        }
    }

    // ---------------------------------------------------------------- refresh

    /// <summary>Everything again from the queue: the state, the totals, the cards, the counts.</summary>
    public void Refresh()
    {
        var active = _jobs.QueuedJobs;
        var finished = _jobs.FinishedJobs;
        var running = active.Where(j => j.State is JobState.Running or JobState.Paused).ToList();
        var culture = CultureInfo.CurrentCulture;
        var state = active.Count == 0 ? "idle" : _jobs.IsPaused ? "paused" : "run";

        // the state
        var color = state switch { "run" => s_accent, "paused" => s_warn, _ => s_dim };
        var c = ((ISolidColorBrush)color).Color;
        _stateIcon.Background = new SolidColorBrush(Color.FromArgb(36, c.R, c.G, c.B));
        _stateIcon.Child = state == "idle" ? Glyph("MiStacked", 20) : new TextBlock { Text = state == "paused" ? "❚❚" : "▶", FontSize = 15, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        _pill.Text = Localizer.T(state switch { "run" => "Running", "paused" => "Paused", _ => finished.Count > 0 ? "Finished" : "Empty" });
        _pill.Foreground = color;
        _pillBox.Background = _stateIcon.Background;
        _subtitle.Text = active.Count > 0
            ? Localizer.T(active.Count == 1 ? "1 job left" : "{0} jobs left").Replace("{0}", active.Count.ToString(culture), StringComparison.Ordinal) + " · "
                + (_jobs.MaxParallel > 1 ? Localizer.T("up to {0} run at the same time").Replace("{0}", _jobs.MaxParallel.ToString(culture), StringComparison.Ordinal)
                    : Localizer.T("they run one after another in the order below"))
            : Localizer.T(finished.Count > 0 ? "All jobs are finished." : "Nothing is waiting.");
        _pauseAll.IsVisible = active.Count > 0;
        _pauseAll.Content = WithText(new TextBlock { Text = _jobs.IsPaused ? "▶" : "❚❚", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }, _jobs.IsPaused ? "Resume all" : "Pause all");

        // the totals of the jobs still to do
        _totals.IsVisible = active.Count > 0;
        var snapshots = active.Select(j => j.Progress.Snapshot()).ToList();
        long total = snapshots.Sum(s => s.TotalBytes), done = snapshots.Sum(s => s.DoneBytes);
        var speed = active.Where(j => j.State == JobState.Running).Select(j => j.Progress.Snapshot().BytesPerSecond).Sum();
        _totalLabel.Text = Localizer.T(active.Count == 1 ? "1 job" : "{0} jobs").Replace("{0}", active.Count.ToString(culture), StringComparison.Ordinal)
            + " · " + (running.FirstOrDefault() is { } first ? first.Title : Localizer.T("waiting"));
        _totalSize.Text = total > 0 ? Localizer.T("{0} of {1}").Replace("{0}", SizeFormatter.Human(done), StringComparison.Ordinal).Replace("{1}", SizeFormatter.Human(total), StringComparison.Ordinal) : string.Empty;
        _totalBar.Value = total > 0 ? (double)done / total : 0;
        _totalBar.Foreground = state == "paused" ? s_warn : s_accent;
        _speed.Text = state == "paused" ? Localizer.T("Paused") : speed > 1 ? SizeFormatter.Human((long)speed) + "/s" : "—";
        _left.Text = state == "run" && speed > 1 && total > done ? Time(TimeSpan.FromSeconds((total - done) / speed)) : "—";
        _parallelText.Text = _jobs.MaxParallel.ToString(culture);
        Title = state == "run" && total > 0 ? ((double)done / total).ToString("P0", culture) + " · " + Localizer.T("Queue") : Localizer.T("Queue");

        // the toolbar
        _toolbar.IsVisible = active.Count + finished.Count > 0;
        for (var i = 0; i < _parallel.Count; i++)
        {
            _parallel[i].IsChecked = i + 1 == _jobs.MaxParallel;
        }
        _clear.Content = WithText(Glyph("MiTrash", 13), Localizer.T("Clear finished") + (finished.Count > 0 ? " (" + finished.Count.ToString(culture) + ")" : string.Empty));
        _clear.IsEnabled = finished.Count > 0;

        // the cards: built again only when the jobs or their order change
        var layout = string.Join(",", active.Select(j => j.Id + ":" + (j.State == JobState.Pending ? "w" : "r"))) + "|" + string.Join(",", finished.Select(f => f.Job.Id));
        if (layout != _layoutKey && _dragged is null)
        {
            _layoutKey = layout;
            _list.Children.Clear();
            if (active.Count > 0)
            {
                _list.Children.Add(Header("Up next", active.Count(j => j.State == JobState.Pending) > 1 ? "Drag to change the order" : null));
                foreach (var job in active)
                {
                    _list.Children.Add(CardOf(job).Root);
                }
            }
            if (finished.Count > 0)
            {
                _list.Children.Add(Header("Finished", null, _clear));
                foreach (var (job, _) in finished)
                {
                    _list.Children.Add(CardOf(job).Root);
                }
            }
            foreach (var gone in _cards.Keys.Where(j => !active.Contains(j) && finished.All(f => !ReferenceEquals(f.Job, j))).ToList())
            {
                _cards.Remove(gone);
            }
        }
        foreach (var job in active)
        {
            UpdateCard(job, _jobs.PlaceOf(job), null);
        }
        foreach (var (job, time) in finished)
        {
            UpdateCard(job, 0, time);
        }
        _empty.IsVisible = active.Count + finished.Count == 0;
        _scroll.IsVisible = !_empty.IsVisible;

        // the counts
        _counts.Children.Clear();
        (string Text, int Count, IBrush Brush)[] counts =
        [
            ("{0} running", active.Count(j => j.State == JobState.Running), s_accent),
            ("{0} waiting", active.Count(j => j.State == JobState.Pending), s_dim),
            ("{0} paused", active.Count(j => j.State == JobState.Paused), s_warn),
            ("{0} done", finished.Count(f => f.Job.State is JobState.Completed or JobState.CompletedWithErrors), s_ok),
            ("{0} failed", finished.Count(f => f.Job.State == JobState.Failed), s_bad),
        ];
        foreach (var (text, count, brush) in counts.Where(x => x.Count > 0))
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            item.Children.AddRange([
                new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = brush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Localizer.T(text).Replace("{0}", count.ToString(culture), StringComparison.Ordinal), FontSize = 12.5, Classes = { "noloc" } },
            ]);
            _counts.Children.Add(item);
        }
        if (_counts.Children.Count == 0)
        {
            _counts.Children.Add(new TextBlock { Text = "No jobs", FontSize = 12.5, Opacity = 0.7 });
        }

        // done: close when asked to
        if (active.Count > 0)
        {
            _hadWork = true;
        }
        else if (_hadWork && CloseWhenDone && finished.All(f => f.Job.State is JobState.Completed or JobState.Cancelled))
        {
            _hadWork = false;
            Close();
        }
    }

    private static Control Header(string text, string? hint, Button? action = null)
    {
        var row = new DockPanel { Margin = new Thickness(2, 10, 2, 6) };
        if (action is not null)
        {
            if (action.Parent is Panel old)
            {
                old.Children.Remove(action);
            }
            action.Height = 26;
            action.MinHeight = 26;
            action.FontSize = 12;
            DockPanel.SetDock(action, Dock.Right);
            row.Children.Add(action);
        }
        if (hint is not null)
        {
            var h = new TextBlock { Text = hint, FontSize = 12, Opacity = 0.55 };
            DockPanel.SetDock(h, Dock.Right);
            row.Children.Add(h);
        }
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.75 });
        return row;
    }

    // ---------------------------------------------------------------- actions

    /// <summary>Pause all / Resume all.</summary>
    public void ToggleStart()
    {
        if (_jobs.IsPaused)
        {
            _jobs.ResumeQueue();
        }
        else
        {
            _jobs.PauseQueue();
        }
        Refresh();
    }

    /// <summary>"Run 1 / 2 / 3 at a time".</summary>
    public void SetParallel(int count)
    {
        _jobs.MaxParallel = count;
        Refresh();
    }

    /// <summary>The speed limit of the queue in bytes per second (0: none).</summary>
    public void SetSpeedLimit(long bytesPerSecond)
    {
        _jobs.SpeedLimit = bytesPerSecond;
        FillLimits();
    }

    /// <summary>A waiting job leaves the queue, a running one is cancelled, a finished one leaves the list.</summary>
    public void Remove(FileJob job)
    {
        if (_jobs.FinishedJobs.Any(f => ReferenceEquals(f.Job, job)))
        {
            _jobs.RemoveFinished(job);
        }
        else
        {
            _jobs.Remove(job);
        }
        if (ReferenceEquals(_selected, job))
        {
            _selected = null;
        }
        Refresh();
    }

    public void ClearFinished()
    {
        _jobs.ClearFinished();
        Refresh();
    }

    /// <summary>Selects the card of a job (keys: Delete, Alt+↑ / ↓).</summary>
    public void Select(FileJob? job)
    {
        _selected = job;
        Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Source is TextBox or ComboBox)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Delete when _selected is { } job:
                Remove(job);
                e.Handled = true;
                break;
            case Key.Space when e.Source is not Button:
                ToggleStart();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _selected is { State: JobState.Pending } moving:
                _jobs.Move(moving, e.Key == Key.Up ? -1 : 1);
                Refresh();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- tests

    /// <summary>The titles of the cards in the list: up next, then finished.</summary>
    public IReadOnlyList<string> Lines => [.. _jobs.QueuedJobs.Select(TitleOf), .. _jobs.FinishedJobs.Select(f => TitleOf(f.Job))];

    /// <summary>What a job does and to how much: "Copy a.txt", "Move 3 items", "Pack: x.zip" ...</summary>
    public static string TitleOf(FileJob job)
    {
        var (_, verb) = KindOf(job);
        IReadOnlyList<Core.Vfs.VfsEntry>? items = job switch
        {
            CopyMoveJob c => c.Sources,
            DeleteJob d => d.Sources,
            _ => null,
        };
        if (items is null || items.Count == 0)
        {
            return job.Title;
        }
        var pattern = verb switch
        {
            "Moving" => "Move {0}",
            "Unpacking" => "Unpack {0}",
            "Uploading" => "Upload {0}",
            "Downloading" => "Download {0}",
            "Deleting" => job is DeleteJob { UsesTrash: true } ? "Move {0} to the trash" : "Delete {0}",
            _ => "Copy {0}",
        };
        var what = items.Count == 1 ? items[0].Name
            : Localizer.T("{0} items").Replace("{0}", items.Count.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal);
        return Localizer.T(pattern).Replace("{0}", what, StringComparison.Ordinal);
    }

    /// <summary>The state word of the head ("Running", "Paused", "Finished", "Empty").</summary>
    public string StateText => _pill.Text ?? string.Empty;

    /// <summary>The state word of a job's card ("#2 in line", "Copying", "Done · 14:02" ...).</summary>
    public string TagOf(FileJob job) => _cards.TryGetValue(job, out var card) ? card.Tag.Text ?? string.Empty : string.Empty;

    /// <summary>The button of the head: "Pause all" or "Resume all".</summary>
    public string StartText => _jobs.IsPaused ? "Resume all" : "Pause all";

    /// <summary>The empty view is shown.</summary>
    public bool ShowsEmpty => _empty.IsVisible;
}
