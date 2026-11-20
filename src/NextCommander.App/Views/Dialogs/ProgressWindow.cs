using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Dialogs;

/// <summary>What the progress window can do besides watching its job (set by the main window).</summary>
/// <param name="CloseWhenDone">"Close when done" at the start.</param>
/// <param name="CloseWhenDoneChanged">The check box was changed (saved in the settings).</param>
/// <param name="OpenTarget">Opens the job's target folder in the panel (null: no such button).</param>
public sealed record ProgressOptions(bool CloseWhenDone = true, Action<bool>? CloseWhenDoneChanged = null, Func<Task>? OpenTarget = null);

/// <summary>
/// The progress of one job (copy, move, delete ...): the state with its percent, where from and to; all files with the
/// counts of done / running / waiting / skipped / failed; the current file; speed (with its recent curve), time left,
/// elapsed, files; the list of files with their state and errors; the speed limit and "close when done". Pause / Resume,
/// Skip file, Stop after this file, a small window in the background, Cancel (asked first). When the job is done the
/// window shows the summary, or closes.
/// </summary>
public sealed class ProgressWindow : Window
{
    private enum Look
    {
        Running,
        Paused,
        Done,
        Cancelled,
    }

    private const int SpeedSamples = 30;
    private const int ShownItems = 200;
    private static readonly FontFamily s_mono = new("Cascadia Mono,Consolas,Menlo,monospace");
    private static readonly Color s_ok = Color.Parse("#2FA36B");
    private static readonly Color s_warn = Color.Parse("#C98A00");
    private static readonly Color s_bad = Color.Parse("#E5484D");
    private static readonly Geometry s_pauseIcon = Geometry.Parse("M6,5 L10,5 L10,19 L6,19 Z M14,5 L18,5 L18,19 L14,19 Z");
    private static readonly Geometry s_playIcon = Geometry.Parse("M7,4 L20,12 L7,20 Z");
    private static readonly Geometry s_stopIcon = Geometry.Parse("M6,6 L18,6 L18,18 L6,18 Z");
    private static readonly Geometry s_skipIcon = Geometry.Parse("M5,4 L15,12 L5,20 Z M17,5 L19,5 L19,19 L17,19 Z");

    private static readonly (string Text, long Bytes)[] s_speeds =
    [
        ("Unlimited", 0), ("500 MB/s", 500L << 20), ("100 MB/s", 100L << 20), ("50 MB/s", 50L << 20), ("10 MB/s", 10L << 20),
    ];

    private readonly FileJob _job;
    private readonly ProgressOptions _options;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _speeds = [];
    private readonly Border _badge = new() { Width = 44, Height = 44, CornerRadius = new CornerRadius(10) };
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _pill = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 1), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pillText = new() { FontSize = 11.5, FontWeight = FontWeight.Medium };
    private readonly StackPanel _route = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _percent = new() { FontSize = 30, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly Border _allCard = new();
    private readonly TextBlock _allLabel = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _allSize = new() { FontSize = 12, Opacity = 0.75 };
    private readonly ProgressBar _allBar = new() { Minimum = 0, Maximum = 1, Height = 8, MinHeight = 8, Margin = new Thickness(0, 8, 0, 0) };
    private readonly WrapPanel _chips = new() { ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _fileCard = new();
    private readonly TextBlock _fileLabel = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.6 };
    private readonly TextBlock _filePercent = new() { FontSize = 12, Opacity = 0.75, Classes = { "noloc" } };
    private readonly TextBlock _fileName = new() { FontSize = 13.5, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
    private readonly TextBlock _fileSize = new() { FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _fileBar = new() { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _speed = StatValue();
    private readonly TextBlock _left = StatValue();
    private readonly TextBlock _elapsed = StatValue();
    private readonly TextBlock _files = StatValue();
    private readonly Canvas _spark = new() { Height = 34, ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _listHead = new() { Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _listChevron = new() { FontSize = 12, Opacity = 0.6, Width = 14, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBlock _listSummary = new() { FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _listToggle = new() { FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _listScroll = new() { MaxHeight = 230 };
    private readonly Border _listBorder = new();
    private readonly ComboBox _limit = new() { MinHeight = 30, MinWidth = 140 };
    private readonly NumericUpDown _customLimit = new() { Minimum = 0.01m, Maximum = 100000, Value = 25, Increment = 5, FormatString = "0.##", Width = 110, IsVisible = false };
    private readonly TextBlock _customUnit = new() { Text = "MB/s", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Classes = { "noloc" } };
    private readonly CheckBox _closeWhenDone = new() { Content = "Close when done" };
    private readonly Border _confirm = new() { IsVisible = false, BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly DockPanel _footer = new() { Margin = new Thickness(22, 12) };
    private readonly DockPanel _optionsRow = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly StackPanel _main = new() { Margin = new Thickness(22, 20, 22, 16) };
    private readonly DockPanel _full = new();
    private readonly Border _mini = new() { Padding = new Thickness(12, 10) };
    private readonly Arc _ring = new() { Width = 36, Height = 36, StrokeThickness = 4, StartAngle = -90, StrokeLineCap = PenLineCap.Round };
    private readonly TextBlock _miniName = new() { FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
    private readonly TextBlock _miniInfo = new() { FontSize = 12.5, Opacity = 0.75 };
    private readonly Button _miniPause = IconButton(s_pauseIcon, "Pause or resume");
    private bool _listOpen = true;
    private bool _confirming;
    private bool _finished;
    private string _listSignature = string.Empty;
    private ProgressBar? _runningBar;
    private TextBlock? _runningText;
    private Look? _footerLook;
    private bool _stopAfterShown;
    private TimeSpan _lastSample;

    public ProgressWindow(FileJob job, ProgressOptions? options = null)
    {
        _job = job;
        _options = options ?? new ProgressOptions();
        Title = job.Title;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _closeWhenDone.IsChecked = _options.CloseWhenDone;
        _closeWhenDone.IsCheckedChanged += (_, _) => _options.CloseWhenDoneChanged?.Invoke(_closeWhenDone.IsChecked == true);
        _miniPause.Click += (_, _) => TogglePause();
        Content = BuildLayout();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateView());
        Opened += (_, _) =>
        {
            UpdateView();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>"Close when done" is on.</summary>
    public bool CloseWhenDone => _closeWhenDone.IsChecked == true;

    /// <summary>The small window is shown (Background).</summary>
    public bool IsMini { get; private set; }

    /// <summary>The cancel question is shown.</summary>
    public bool IsConfirmingCancel => _confirming;

    /// <summary>The job ended: the window shows the summary (when it stays open).</summary>
    public void ShowFinished()
    {
        _finished = true;
        _confirming = false;
        if (IsMini)
        {
            SetMini(false);
        }
        UpdateView();
    }

    // ---------------------------------------------------------------- layout

    private static TextBlock StatValue() => new() { FontSize = 16, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };

    private static Button IconButton(Geometry icon, string tip)
    {
        var button = new Button { Content = new PathIcon { Data = icon, Width = 14, Height = 14 }, Classes = { "tb", "ghost", "icon" } };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private static StackPanel WithIcon(Geometry icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([new PathIcon { Data = icon, Width = 13, Height = 13 }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    private static StackPanel WithGlyph(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([DialogLook.Glyph(glyph, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    private static void AsCard(Border card, Control child)
    {
        card.Child = child;
        card.CornerRadius = new CornerRadius(8);
        card.BorderThickness = new Thickness(1);
        card.Padding = new Thickness(14, 12);
        card.Margin = new Thickness(0, 12, 0, 0);
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
    }

    private static Border Stat(string label, Control value, Control? extra = null)
    {
        var stack = new StackPanel();
        stack.Children.AddRange([new TextBlock { Text = label, FontSize = 11.5, Opacity = 0.7 }, value]);
        Control content = stack;
        if (extra is not null)
        {
            var dock = new DockPanel();
            DockPanel.SetDock(stack, Dock.Left);
            extra.Margin = new Thickness(10, 0, 0, 0);
            extra.VerticalAlignment = VerticalAlignment.Bottom;
            dock.Children.AddRange([stack, extra]);
            content = dock;
        }
        var card = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 9) };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return card;
    }

    private Control BuildLayout()
    {
        // the state: icon, title and pill, from → to, percent
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _pill.Child = _pillText;
        titleRow.Children.AddRange([_title, _pill]);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0) };
        titles.Children.AddRange([titleRow, _route]);
        if (_job.Route is { } route)
        {
            TextBlock Place(string text) => new() { Text = text, FontFamily = s_mono, FontSize = 12, Opacity = 0.75, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" }, [ToolTip.TipProperty] = text };
            _route.Children.AddRange([Place(route.Source), new TextBlock { Text = "→", Opacity = 0.6, Classes = { "noloc" } }, Place(route.Target)]);
        }
        else
        {
            _route.IsVisible = false;
        }
        var head = new DockPanel();
        DockPanel.SetDock(_badge, Dock.Left);
        DockPanel.SetDock(_percent, Dock.Right);
        head.Children.AddRange([_badge, _percent, titles]);

        // all files
        var allTop = new DockPanel();
        var allCaption = new TextBlock { Text = "ALL FILES", FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.6 };
        DockPanel.SetDock(_allSize, Dock.Right);
        allTop.Children.AddRange([_allSize, allCaption]);
        var all = new StackPanel();
        all.Children.AddRange([allTop, new Border { Child = _allLabel, Margin = new Thickness(0, 2, 0, 0) }, _allBar, _chips]);
        AsCard(_allCard, all);

        // the current file
        var fileTop = new DockPanel();
        DockPanel.SetDock(_filePercent, Dock.Right);
        fileTop.Children.AddRange([_filePercent, _fileLabel]);
        var fileRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var fileIcon = DialogLook.Glyph("MiDoc", 15);
        fileIcon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(fileIcon, Dock.Left);
        DockPanel.SetDock(_fileSize, Dock.Right);
        _fileSize.Margin = new Thickness(10, 0, 0, 0);
        fileRow.Children.AddRange([fileIcon, _fileSize, _fileName]);
        var file = new StackPanel();
        file.Children.AddRange([fileTop, fileRow, _fileBar]);
        AsCard(_fileCard, file);

        // speed with its curve, time left, elapsed, files
        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("1.4*,*,*,*"), ColumnSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var cards = new[] { Stat("Speed", _speed, _spark), Stat("Time left", _left), Stat("Elapsed", _elapsed), Stat("Files", _files) };
        for (var i = 0; i < cards.Length; i++)
        {
            Grid.SetColumn(cards[i], i);
            stats.Children.Add(cards[i]);
        }

        // the files: a header that folds the list
        var listTitle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        listTitle.Children.AddRange([_listChevron, new TextBlock { Text = "Files", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, _listSummary]);
        var listHeadRow = new DockPanel();
        DockPanel.SetDock(_listToggle, Dock.Right);
        listHeadRow.Children.AddRange([_listToggle, listTitle]);
        _listHead.Child = listHeadRow;
        _listHead.PointerPressed += (_, e) =>
        {
            ToggleList();
            e.Handled = true;
        };
        _listScroll.Content = _list;
        _listBorder.Child = _listScroll;
        _listBorder.CornerRadius = new CornerRadius(8);
        _listBorder.BorderThickness = new Thickness(1);
        _listBorder.Margin = new Thickness(0, 8, 0, 0);
        _listBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        _listBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        // the speed limit (a preset or a number of MB/s) and "close when done"
        var limits = s_speeds.Select(s => s.Text).Append("Custom…").ToList();
        _limit.ItemsSource = limits;
        var current = _job.SpeedLimit.BytesPerSecond;
        var preset = Array.FindIndex(s_speeds, s => s.Bytes == current);
        if (preset >= 0)
        {
            _limit.SelectedIndex = preset;
        }
        else
        {
            _limit.SelectedIndex = limits.Count - 1;
            _customLimit.Value = Math.Max(0.01m, (decimal)current / (1L << 20));
        }
        Avalonia.Automation.AutomationProperties.SetName(_limit, "Speed limit");
        _limit.SelectionChanged += (_, _) => ApplyLimit();
        _customLimit.ValueChanged += (_, _) => ApplyLimit();
        _customLimit.IsVisible = _customUnit.IsVisible = _limit.SelectedIndex == s_speeds.Length; // the job's own limit stays until one is chosen
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        limitRow.Children.AddRange([new TextBlock { Text = "Speed limit", FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }, _limit, _customLimit, _customUnit]);
        var options = _optionsRow;
        DockPanel.SetDock(_closeWhenDone, Dock.Right);
        options.Children.AddRange([_closeWhenDone, limitRow]);

        _main.Children.AddRange([head, _allCard, _fileCard, stats, _listHead, _listBorder, options]);

        // the cancel question
        var question = new TextBlock { Text = _job is CopyMoveJob { IsMove: true } ? "Cancel the move? The partly copied file will be deleted." : _job is CopyMoveJob ? "Cancel the copy? The partly copied file will be deleted." : "Cancel the operation?", FontSize = 13, Foreground = new SolidColorBrush(s_bad), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var keep = new Button { Content = _job is CopyMoveJob { IsMove: true } ? "Keep moving" : _job is CopyMoveJob ? "Keep copying" : "Keep going", Classes = { "tb" } };
        keep.Click += (_, _) =>
        {
            _confirming = false;
            UpdateView();
        };
        var really = new Button
        {
            Content = _job is CopyMoveJob { IsMove: true } ? "Cancel move" : _job is CopyMoveJob ? "Cancel copy" : "Cancel",
            Classes = { "tb" },
            Background = new SolidColorBrush(s_bad),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(s_bad),
        };
        really.Click += (_, _) => CancelJob();
        var confirmButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmButtons.Children.AddRange([keep, really]);
        var confirmRow = new DockPanel { Margin = new Thickness(22, 12) };
        DockPanel.SetDock(confirmButtons, Dock.Right);
        confirmRow.Children.AddRange([confirmButtons, question]);
        _confirm.Child = confirmRow;
        _confirm.Background = new SolidColorBrush(s_bad, 0.12);
        _confirm[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var footBorder = new Border { Child = _footer, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        DockPanel.SetDock(footBorder, Dock.Bottom);
        DockPanel.SetDock(_confirm, Dock.Bottom);
        _full.Children.AddRange([footBorder, _confirm, _main]);

        // the small window: a ring, the file and the numbers, pause, back to the details
        var track = new Ellipse { Width = 36, Height = 36, StrokeThickness = 4 };
        track[!Shape.StrokeProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var ring = new Panel { Width = 36, Height = 36 };
        ring.Children.AddRange([track, _ring]);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) };
        texts.Children.AddRange([_miniName, _miniInfo]);
        var expand = IconButton(Geometry.Parse("M4,14 L4,20 L10,20 M20,10 L20,4 L14,4 M4,20 L11,13 M20,4 L13,11"), "Show details");
        if (expand.Content is PathIcon expandIcon)
        {
            expandIcon.Data = Geometry.Parse("M3,13 L3,21 L11,21 L11,19 L5,19 L5,13 Z M21,11 L21,3 L13,3 L13,5 L19,5 L19,11 Z");
        }
        expand.Click += (_, _) => SetMini(false);
        var miniButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        miniButtons.Children.AddRange([_miniPause, expand]);
        var miniRow = new DockPanel();
        DockPanel.SetDock(ring, Dock.Left);
        DockPanel.SetDock(miniButtons, Dock.Right);
        miniRow.Children.AddRange([ring, miniButtons, texts]);
        _mini.Child = miniRow;

        var root = new Panel();
        root.Children.AddRange([_full, _mini]);
        _mini.IsVisible = false;
        return root;
    }

    private void ToggleList()
    {
        _listOpen = !_listOpen;
        UpdateView();
    }

    private void ApplyLimit()
    {
        var custom = _limit.SelectedIndex == s_speeds.Length;
        _customLimit.IsVisible = _customUnit.IsVisible = custom;
        _job.SpeedLimit.BytesPerSecond = custom ? (long)((_customLimit.Value ?? 1) * (1L << 20)) : _limit.SelectedIndex >= 0 ? s_speeds[_limit.SelectedIndex].Bytes : 0;
    }

    /// <summary>Background: the job keeps going in a small window (and back).</summary>
    public void SetMini(bool mini)
    {
        IsMini = mini;
        _full.IsVisible = !mini;
        _mini.IsVisible = mini;
        Width = mini ? 380 : 640;
        Topmost = mini;
        UpdateView();
    }

    // ---------------------------------------------------------------- actions

    private void TogglePause()
    {
        if (!_finished && _job.State is JobState.Running or JobState.Paused)
        {
            _job.TogglePause();
            _confirming = false;
            UpdateView();
        }
    }

    private void CancelJob()
    {
        _confirming = false;
        if (_job.Pause.IsPaused)
        {
            _job.TogglePause(); // a paused job has to run on to see that it is cancelled
        }
        _job.Cancel();
        UpdateView();
    }

    /// <summary>Esc / Cancel: asks first while the job runs.</summary>
    public void AskCancel()
    {
        if (!_finished)
        {
            _confirming = true;
            UpdateView();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is ComboBox or NumericUpDown or TextBox)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Space when !_finished:
                TogglePause();
                break;
            case Key.Escape when _confirming:
                _confirming = false;
                UpdateView();
                break;
            case Key.Escape when !_finished:
                AskCancel();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Enter when _confirming:
                CancelJob();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- the view

    private Look CurrentLook() =>
        _job.State == JobState.Cancelled ? Look.Cancelled
        : _finished || _job.State is JobState.Completed or JobState.CompletedWithErrors or JobState.Failed ? Look.Done
        : _job.Pause.IsPaused ? Look.Paused
        : Look.Running;

    private static string Time(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private void UpdateView()
    {
        var p = _job.Progress.Snapshot();
        var look = CurrentLook();
        var counts = _job.Progress.ItemCounts;
        var failed = Math.Max(counts.Failed, _job.Errors.Count > 0 && counts.Failed == 0 ? _job.Errors.Count : counts.Failed);
        var multi = p.TotalFiles > 1;
        var copy = _job as CopyMoveJob;
        var percent = look == Look.Done && _job.State != JobState.Failed ? 100 : (int)Math.Min(99, Math.Floor(p.Fraction * 100));
        var color = look switch { Look.Paused => s_warn, Look.Done => failed > 0 || _job.State == JobState.Failed ? s_bad : s_ok, Look.Cancelled => s_bad, _ => (Color?)null };

        // the state
        _badge.Background = color is { } badgeColor ? new SolidColorBrush(badgeColor, 0.15) : null;
        if (color is null)
        {
            var tint = new Border { CornerRadius = new CornerRadius(10), Opacity = 0.14 };
            tint[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            var panel = new Panel();
            panel.Children.AddRange([tint, DialogLook.Glyph("MiCopy", 22, accent: true)]);
            _badge.Child = panel;
        }
        else
        {
            var c = color.Value;
            Control icon = look switch
            {
                Look.Paused => new PathIcon { Data = s_pauseIcon, Width = 20, Height = 20, Foreground = new SolidColorBrush(c) },
                Look.Cancelled => DialogLook.Glyph("MiX", 20, danger: true),
                _ when failed > 0 || _job.State == JobState.Failed => DialogLook.Glyph("MiInfo", 20, danger: true),
                _ => new TextBlock { Text = "✓", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            };
            _badge.Child = icon;
        }
        var verb = copy is null ? null : copy.IsMove ? "Moving" : "Copying";
        _title.Text = (look, copy) switch
        {
            (Look.Done, { IsMove: true }) => string.Format(CultureInfo.CurrentCulture, "Moved {0} of {1} files", counts.Done, p.TotalFiles),
            (Look.Done, not null) => string.Format(CultureInfo.CurrentCulture, "Copied {0} of {1} files", counts.Done, p.TotalFiles),
            (Look.Cancelled, { IsMove: true }) => "Move cancelled",
            (Look.Cancelled, not null) => "Copy cancelled",
            (_, not null) when p.TotalFiles == 1 => verb == "Moving" ? "Moving 1 file" : "Copying 1 file",
            (_, not null) => string.Format(CultureInfo.CurrentCulture, verb == "Moving" ? "Moving {0} files" : "Copying {0} files", p.TotalFiles),
            _ => _job.Title,
        };
        _pillText.Text = look switch
        {
            Look.Paused => "Paused",
            Look.Cancelled => "Cancelled",
            Look.Done when _job.State == JobState.Failed => "Failed",
            Look.Done when failed > 0 => "Done with errors",
            Look.Done => "Done",
            _ when p.IsCounting => "Counting...",
            _ => "In progress",
        };
        if (color is { } pillColor)
        {
            _pill.Background = new SolidColorBrush(pillColor, 0.15);
            _pillText.Foreground = new SolidColorBrush(pillColor);
        }
        else
        {
            _pill[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
            _pillText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        }
        _percent.Text = percent.ToString(CultureInfo.CurrentCulture) + "%";
        Title = (look == Look.Done ? Core.Localization.Localizer.T("Done") : percent.ToString(CultureInfo.CurrentCulture) + "%") + " · " + Core.Localization.Localizer.T(_job.Title);

        // all files
        var running = look is Look.Running or Look.Paused && p.CurrentItem is not null && !p.IsCounting ? 1 : 0;
        var waiting = Math.Max(0, p.TotalFiles - p.DoneFiles - running);
        _allCard.IsVisible = multi;
        _allLabel.Text = string.Format(CultureInfo.CurrentCulture, "{0} of {1} files done", p.DoneFiles, p.TotalFiles);
        _allSize.Text = string.Format(CultureInfo.CurrentCulture, "{0} of {1}", SizeFormatter.Human(p.DoneBytes), SizeFormatter.Human(p.TotalBytes));
        _allBar.IsIndeterminate = p.IsCounting && look == Look.Running;
        _allBar.Value = p.Fraction;
        SetBarColor(_allBar, color);
        _chips.Children.Clear();
        void Chip(string text, int count, Color? dot, bool bad = false)
        {
            if (count <= 0)
            {
                return;
            }
            var mark = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            if (dot is { } d)
            {
                mark.Fill = new SolidColorBrush(d);
            }
            else
            {
                mark[!Shape.FillProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.AddRange([mark, new TextBlock { Text = Fill(Core.Localization.Localizer.T(text), count), FontSize = 11.5, Classes = { "noloc" } }]);
            var chip = new Border { Child = row, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1) };
            if (bad)
            {
                chip.Background = new SolidColorBrush(s_bad, 0.15);
                ((TextBlock)row.Children[1]).Foreground = new SolidColorBrush(s_bad);
            }
            else
            {
                chip[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundListLowBrush");
            }
            _chips.Children.Add(chip);
        }
        Chip("Done {0}", counts.Done, s_ok);
        Chip("Working {0}", running, null);
        Chip("Waiting {0}", waiting, Colors.Gray);
        Chip("Skipped {0}", counts.Skipped, s_warn);
        Chip("Failed {0}", failed, s_bad, bad: true);

        // the current file
        _fileCard.IsVisible = look is Look.Running or Look.Paused || !multi;
        _fileLabel.Text = multi ? string.Format(CultureInfo.CurrentCulture, "CURRENT FILE · {0} OF {1}", Math.Min(p.DoneFiles + 1, p.TotalFiles), p.TotalFiles) : "FILE";
        _fileName.Text = p.IsCounting ? Core.Localization.Localizer.T("Counting...") : p.CurrentItem ?? string.Empty;
        _fileSize.Text = string.Format(CultureInfo.CurrentCulture, "{0} of {1}", SizeFormatter.Human(p.CurrentFileDone), SizeFormatter.Human(p.CurrentFileSize));
        _filePercent.Text = ((int)(p.CurrentFileFraction * 100)).ToString(CultureInfo.CurrentCulture) + "%";
        _fileBar.Value = look == Look.Done && !multi ? 1 : p.CurrentFileFraction;
        _fileBar.IsIndeterminate = p.IsCounting && look == Look.Running;
        SetBarColor(_fileBar, color);

        // the numbers and the speed curve
        if (look == Look.Running && p.Elapsed - _lastSample >= TimeSpan.FromMilliseconds(500))
        {
            _lastSample = p.Elapsed;
            _speeds.Add(p.BytesPerSecond);
            if (_speeds.Count > SpeedSamples)
            {
                _speeds.RemoveAt(0);
            }
        }
        _speed.Text = look switch
        {
            Look.Running when p.BytesPerSecond > 0 => SizeFormatter.Human((long)p.BytesPerSecond) + "/s",
            Look.Paused => Core.Localization.Localizer.T("Paused"),
            _ => "—",
        };
        _left.Text = look == Look.Running && p.Remaining is { } r ? Time(r) : look == Look.Done ? "0:00" : "—";
        _elapsed.Text = Time(p.Elapsed);
        _files.Text = string.Format(CultureInfo.CurrentCulture, "{0} / {1}", p.DoneFiles, p.TotalFiles);
        DrawSpark(color);

        // the files
        var items = _job.Progress.Items;
        _listHead.IsVisible = multi && items.Count > 0;
        _listBorder.IsVisible = _listHead.IsVisible && _listOpen;
        _listChevron.Text = _listOpen ? "▾" : "▸";
        _listToggle.Text = _listOpen ? Core.Localization.Localizer.T("Hide") : Core.Localization.Localizer.T("Show");
        _listSummary.Text = "· " + Fill(Core.Localization.Localizer.T("{0} items, {1}"), p.TotalFiles, SizeFormatter.Human(p.TotalBytes));
        _listSummary.Classes.Set("noloc", true);
        if (_listBorder.IsVisible)
        {
            UpdateList(items, waiting, p, color);
        }

        // the cancel question and the buttons
        _confirm.IsVisible = _confirming && look is Look.Running or Look.Paused;
        _optionsRow.IsVisible = look is Look.Running or Look.Paused; // the speed limit and "close when done" are of no use any more
        UpdateFooter(look, p, counts.Skipped, failed);

        // the small window
        _ring.SweepAngle = 360 * (look == Look.Done ? 1 : p.Fraction);
        if (color is { } ringColor)
        {
            _ring.Stroke = new SolidColorBrush(ringColor);
        }
        else
        {
            _ring[!Shape.StrokeProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        }
        _miniName.Text = multi ? Fill(Core.Localization.Localizer.T("{0} of {1} files"), p.DoneFiles, p.TotalFiles) + " · " + p.CurrentItem : p.CurrentItem ?? _job.Title;
        _miniInfo.Text = look switch
        {
            Look.Running => Fill(Core.Localization.Localizer.T("{0}% · {1}/s · {2} left"), percent, SizeFormatter.Human((long)p.BytesPerSecond), p.Remaining is { } left ? Time(left) : "—"),
            Look.Paused => Fill(Core.Localization.Localizer.T("{0}% · Paused"), percent),
            Look.Done => Core.Localization.Localizer.T("Done"),
            _ => Core.Localization.Localizer.T("Cancelled"),
        };
        _miniInfo.Classes.Set("noloc", true);
        if (_miniPause.Content is PathIcon pauseIcon)
        {
            pauseIcon.Data = look == Look.Paused ? s_playIcon : s_pauseIcon;
        }
        _miniPause.IsVisible = look is Look.Running or Look.Paused;
    }

    /// <summary>A translated text with its {0}, {1} ... holes filled in.</summary>
    private static string Fill(string format, params object[] values)
    {
        var text = format;
        for (var i = 0; i < values.Length; i++)
        {
            text = text.Replace("{" + i + "}", Convert.ToString(values[i], CultureInfo.CurrentCulture), StringComparison.Ordinal);
        }
        return text;
    }

    private static void SetBarColor(ProgressBar bar, Color? color)
    {
        if (color is { } c)
        {
            bar.Foreground = new SolidColorBrush(c);
        }
        else
        {
            bar.ClearValue(ForegroundProperty);
        }
    }

    private void DrawSpark(Color? color)
    {
        _spark.Children.Clear();
        var width = _spark.Bounds.Width;
        if (_speeds.Count < 2 || width <= 0)
        {
            return;
        }
        var max = Math.Max(1, _speeds.Max() * 1.1);
        var points = _speeds.Select((v, i) => new Point(i * width / (_speeds.Count - 1), 34 - (v / max * 32))).ToList();
        var line = new Polyline { Points = points, StrokeThickness = 1.5 };
        var fill = new Polygon { Points = [new Point(0, 34), .. points, new Point(points[^1].X, 34)], Opacity = 0.14 };
        if (color is { } c)
        {
            line.Stroke = new SolidColorBrush(c);
            fill.Fill = new SolidColorBrush(c);
        }
        else
        {
            line[!Shape.StrokeProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            fill[!Shape.FillProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        }
        _spark.Children.AddRange([fill, line]);
    }

    /// <summary>The newest files with their state; the running one with its bar; then how many are waiting.</summary>
    private void UpdateList(IReadOnlyList<ProgressItem> items, int waiting, ProgressSnapshot p, Color? color)
    {
        var shown = items.Count > ShownItems ? items.Skip(items.Count - ShownItems).ToList() : items.ToList();
        var signature = items.Count + "|" + string.Join(",", shown.TakeLast(3).Select(i => i.State)) + "|" + waiting;
        if (signature == _listSignature && _runningBar is not null)
        {
            _runningBar.Value = p.CurrentFileFraction;
            SetBarColor(_runningBar, color);
            if (_runningText is not null && items.Count > 0)
            {
                _runningText.Text = RunningText(p, items[^1].Size);
            }
            return;
        }
        var follow = _listScroll.Offset.Y >= _listScroll.Extent.Height - _listScroll.Viewport.Height - 4;
        _listSignature = signature;
        _runningBar = null;
        _runningText = null;
        _list.Children.Clear();
        for (var i = 0; i < shown.Count; i++)
        {
            _list.Children.Add(ItemRow(shown[i], i > 0, p, color));
        }
        if (waiting > 0)
        {
            var more = new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, "{0} more files waiting", waiting), FontSize = 12.5, Opacity = 0.6, Margin = new Thickness(44, 8, 12, 8) };
            _list.Children.Add(new Border { Child = more, BorderThickness = new Thickness(0, 1, 0, 0), [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush") });
        }
        if (follow)
        {
            Dispatcher.UIThread.Post(() => _listScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private static string RunningText(ProgressSnapshot p, long size) =>
        ((int)(p.CurrentFileFraction * 100)).ToString(CultureInfo.CurrentCulture) + "% · " + SizeFormatter.Human(size);

    private Border ItemRow(ProgressItem item, bool line, ProgressSnapshot p, Color? color)
    {
        var (glyph, tint) = item.State switch
        {
            ProgressItemState.Done => ("✓", s_ok),
            ProgressItemState.Skipped => ("⦸", s_warn),
            ProgressItemState.Failed => ("!", s_bad),
            _ => ("●", (Color?)null),
        };
        var icon = new TextBlock { Text = glyph, Width = 22, FontSize = 13, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Classes = { "noloc" } };
        if (tint is { } t)
        {
            icon.Foreground = new SolidColorBrush(t);
        }
        else
        {
            icon[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        }
        var name = new TextBlock { Text = item.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        ToolTip.SetTip(name, item.Name);
        Control middle = item.State switch
        {
            ProgressItemState.Running => new ProgressBar { Minimum = 0, Maximum = 1, Value = p.CurrentFileFraction, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center },
            ProgressItemState.Failed => new TextBlock { Text = item.Message ?? string.Empty, FontSize = 11.5, Foreground = new SolidColorBrush(s_bad), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" }, [ToolTip.TipProperty] = item.Message },
            ProgressItemState.Skipped => new TextBlock { Text = "Skipped", FontSize = 11.5, Foreground = new SolidColorBrush(s_warn), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
            _ => new TextBlock { Text = "Done", FontSize = 11.5, Foreground = new SolidColorBrush(s_ok), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
        };
        if (middle is ProgressBar bar)
        {
            _runningBar = bar;
            SetBarColor(bar, color);
        }
        var size = new TextBlock
        {
            Text = item.State == ProgressItemState.Running ? RunningText(p, item.Size) : SizeFormatter.Human(item.Size),
            FontSize = 12,
            Opacity = 0.7,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "noloc" },
        };
        if (item.State == ProgressItemState.Running)
        {
            _runningText = size;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*,140,92"), ColumnSpacing = 10, Margin = new Thickness(12, 7) };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(middle, 2);
        Grid.SetColumn(size, 3);
        grid.Children.AddRange([icon, name, middle, size]);
        var row = new Border { Child = grid, BorderThickness = new Thickness(0, line ? 1 : 0, 0, 0) };
        row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        if (item.State == ProgressItemState.Running)
        {
            row[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        }
        return row;
    }

    private void UpdateFooter(Look look, ProgressSnapshot p, int skipped, int failed)
    {
        // rebuilt only when the state (or "stop after") changes, so a click is never lost to a rebuild
        if (_footerLook == look && _stopAfterShown == _job.StopAfterCurrentFile)
        {
            return;
        }
        _footerLook = look;
        _stopAfterShown = _job.StopAfterCurrentFile;
        _footer.Children.Clear();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        DockPanel.SetDock(right, Dock.Right);
        switch (look)
        {
            case Look.Done:
                var summary = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                summary.Children.Add(new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, "{0} in {1}", SizeFormatter.Human(p.DoneBytes), Time(p.Elapsed)), FontSize = 13, Opacity = 0.75 });
                if (skipped > 0)
                {
                    summary.Children.Add(new TextBlock { Text = " · " + Fill(Core.Localization.Localizer.T("{0} skipped"), skipped), FontSize = 13, Opacity = 0.75, Classes = { "noloc" } });
                }
                if (failed > 0)
                {
                    summary.Children.Add(new TextBlock { Text = " · " + Fill(Core.Localization.Localizer.T("{0} failed"), failed), FontSize = 13, Foreground = new SolidColorBrush(s_bad), Classes = { "noloc" } });
                }
                if (_options.OpenTarget is { } open)
                {
                    var openButton = new Button { Content = WithGlyph("MiFolderOpen", "Open destination"), Classes = { "tb" } };
                    openButton.Click += async (_, _) =>
                    {
                        await open();
                        Close();
                    };
                    right.Children.Add(openButton);
                }
                var close = new Button { Content = "Close", Classes = { "tb", "primary" }, MinWidth = 80 };
                close.Click += (_, _) => Close();
                right.Children.Add(close);
                _footer.Children.AddRange([right, summary]);
                break;
            case Look.Cancelled:
                var closeCancelled = new Button { Content = "Close", Classes = { "tb", "primary" }, MinWidth = 80 };
                closeCancelled.Click += (_, _) => Close();
                right.Children.Add(closeCancelled);
                _footer.Children.AddRange([right, new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, "{0} of {1} files done before cancelling.", p.DoneFiles, p.TotalFiles), FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }]);
                break;
            default:
                var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var paused = look == Look.Paused;
                var pause = new Button { Content = WithIcon(paused ? s_playIcon : s_pauseIcon, paused ? "Resume" : "Pause"), Classes = { "tb", "primary" } };
                pause.Click += (_, _) => TogglePause();
                left.Children.Add(pause);
                if (_job.CanSkipFile && p.TotalFiles > 1)
                {
                    var skip = new Button { Content = WithIcon(s_skipIcon, "Skip file"), Classes = { "tb" } };
                    ToolTip.SetTip(skip, "Skip the current file and go on");
                    skip.Click += (_, _) => _job.SkipCurrentFile();
                    left.Children.Add(skip);
                }
                var stopAfter = new Button { Content = WithIcon(s_stopIcon, "Stop after this file"), Classes = { "tb" } };
                if (_job.StopAfterCurrentFile)
                {
                    stopAfter[!Button.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
                    stopAfter[!Button.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
                }
                stopAfter.Click += (_, _) =>
                {
                    _job.StopAfterCurrentFile = !_job.StopAfterCurrentFile;
                    UpdateView();
                };
                left.Children.Add(stopAfter);
                var background = new Button { Content = WithGlyph("MiArrowDown", "Background"), Classes = { "tb", "ghost" } };
                ToolTip.SetTip(background, "Keep going in a small window");
                background.Click += (_, _) => SetMini(true);
                var cancel = new Button { Content = "Cancel", Classes = { "tb", "ghost", "danger" } };
                cancel.Click += (_, _) => AskCancel();
                right.Children.AddRange([background, cancel]);
                _footer.Children.AddRange([right, left]);
                break;
        }
    }
}
