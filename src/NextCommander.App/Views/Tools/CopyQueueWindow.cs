using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// The queue of F2 Queue (background transfers): the file being copied with the progress of the
/// file and of the whole item, the waiting items one a line ("copy:" / "move:"), + (queue the selection of the active
/// panel), - (take the selected item out), Start / Pause, and a speed limit in kB/s.
/// </summary>
public sealed class CopyQueueWindow : Window
{
    private readonly JobQueue _jobs;
    private readonly Func<Task> _addSelection;
    private readonly TextBlock _current = new() { TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
    private readonly ProgressBar _fileBar = new() { Minimum = 0, Maximum = 1, Height = 22 };
    private readonly ProgressBar _totalBar = new() { Minimum = 0, Maximum = 1, Height = 22 };
    private readonly TextBlock _filePercent = new() { Width = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Classes = { "noloc" } };
    private readonly TextBlock _totalPercent = new() { Width = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Classes = { "noloc" } };
    private readonly ListBox _list = new() { Classes = { "noloc" } };
    private readonly Button _start = new() { Content = "Pause", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly CheckBox _limit = new() { Content = "Speed limit", VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericUpDown _kb = new() { Minimum = 1, Maximum = 10_000_000, Value = 5000, Increment = 100, FormatString = "0.0", Width = 130 };
    private readonly DispatcherTimer _timer;
    private List<(FileJob Job, string Line)> _lines = [];
    private bool _hadWork;

    public CopyQueueWindow(JobQueue jobs, Func<Task> addSelection)
    {
        _jobs = jobs;
        _addSelection = addSelection;
        Title = "Queue";
        Width = 560;
        Height = 420;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Control Bar(ProgressBar bar, TextBlock percent)
        {
            var line = new DockPanel();
            DockPanel.SetDock(percent, Dock.Right);
            line.Children.AddRange([percent, bar]);
            return line;
        }
        var top = new StackPanel { Spacing = 6, Margin = new Thickness(8, 8, 8, 6) };
        top.Children.AddRange([_current, Bar(_fileBar, _filePercent), Bar(_totalBar, _totalPercent)]);

        var add = new Button { Content = "+", Width = 36, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(add, "Queue the files selected in the active panel (copied to the other panel)");
        add.Click += async (_, _) =>
        {
            await _addSelection();
            Refresh();
        };
        var remove = new Button { Content = "-", Width = 36, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(remove, "Take the selected item out of the queue");
        remove.Click += (_, _) => RemoveSelected();
        _start.Click += (_, _) => ToggleStart();
        _limit.IsCheckedChanged += (_, _) => ApplyLimit();
        _kb.ValueChanged += (_, _) => ApplyLimit();
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 6, 8, 8) };
        bottom.Children.AddRange([add, remove, _start, new Border { Width = 6 }, _limit, _kb,
            new TextBlock { Text = "kB/s", VerticalAlignment = VerticalAlignment.Center }]);
        if (jobs.SpeedLimit > 0)
        {
            _limit.IsChecked = true;
            _kb.Value = jobs.SpeedLimit / 1024m;
        }

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        _list.Margin = new Thickness(8, 0);
        root.Children.AddRange([top, bottom, _list]);
        Content = root;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh());
        Opened += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>The lines of the list (tests).</summary>
    public IReadOnlyList<string> Lines => [.. _lines.Select(l => l.Line)];

    public string CurrentText => _current.Text ?? string.Empty;

    public string StartText => _start.Content as string ?? string.Empty;

    /// <summary>Closes by itself when the queue is done (after it had work).</summary>
    public bool CloseWhenDone { get; set; } = true;

    public void Refresh()
    {
        var queued = _jobs.QueuedJobs;
        _lines = [.. queued.SelectMany(job => Describe(job).Select(line => (job, line)))];
        var selected = _list.SelectedIndex;
        _list.ItemsSource = _lines.Select(l => l.Line).ToList();
        _list.SelectedIndex = _lines.Count == 0 ? -1 : Math.Clamp(selected, 0, _lines.Count - 1);
        _start.Content = _jobs.IsPaused ? "Start" : "Pause";

        var running = queued.FirstOrDefault(j => j.State is JobState.Running or JobState.Paused);
        if (running is null)
        {
            _current.Text = queued.Count == 0 ? "The queue is empty." : _jobs.IsPaused ? "Paused." : "Waiting...";
            _fileBar.Value = _totalBar.Value = 0;
            _filePercent.Text = _totalPercent.Text = string.Empty;
            Title = "Queue";
            if (queued.Count == 0 && _hadWork && CloseWhenDone)
            {
                Close();
            }
            return;
        }
        _hadWork = true;
        var p = running.Progress.Snapshot();
        var culture = CultureInfo.CurrentCulture;
        var target = (running as CopyMoveJob)?.TargetDirectory.ToDisplayString();
        _current.Text = p.IsCounting ? "Counting..." : (p.CurrentItem ?? string.Empty) + (target is null ? string.Empty : "->" + target);
        _fileBar.Value = p.CurrentFileFraction;
        _totalBar.Value = p.Fraction;
        _filePercent.Text = p.CurrentFileFraction.ToString("P0", culture);
        _totalPercent.Text = p.Fraction.ToString("P0", culture);
        Title = p.Fraction.ToString("P0", culture) + " " + (p.CurrentItem ?? running.Title);
    }

    /// <summary>One line for each item of a job: "copy:C:\Movie\a.mp4" (the target is shown above while it runs).</summary>
    private static IEnumerable<string> Describe(FileJob job) => job is CopyMoveJob copy
        ? copy.Sources.Select(s => (copy.IsMove ? "move:" : "copy:") + s.Path.ToDisplayString())
        : [job.Title];

    /// <summary>- : the item selected leaves the queue (the running one is cancelled).</summary>
    public void RemoveSelected()
    {
        if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _lines.Count)
        {
            _jobs.Remove(_lines[_list.SelectedIndex].Job);
            Refresh();
        }
    }

    public void SelectLine(int index) => _list.SelectedIndex = index;

    /// <summary>Start / Pause: the queue waits, or goes on.</summary>
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

    /// <summary>Speed limit in kB/s for the queue (the running item at once).</summary>
    public void SetSpeedLimit(bool on, decimal kilobytesPerSecond)
    {
        _kb.Value = kilobytesPerSecond;
        _limit.IsChecked = on;
        ApplyLimit();
    }

    private void ApplyLimit() =>
        _jobs.SpeedLimit = _limit.IsChecked == true && _kb.Value is { } kb && kb > 0 ? (long)(kb * 1024) : 0;
}
