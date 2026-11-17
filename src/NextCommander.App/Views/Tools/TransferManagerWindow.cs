using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NextCommander.App.Services.Design;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Tools;

/// <summary>A row of the transfer manager: a job, its text and progress, and what its buttons can do now.</summary>
public sealed partial class TransferRow(FileJob job) : ObservableObject
{
    public FileJob Job { get; } = job;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _canPause;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;
}

/// <summary>
/// The background transfers: running and queued jobs with progress. Each row ends with pause / resume, run next (the
/// job gets priority), up, down and remove; the order of the waiting jobs of the queue is the order they run in. Shown
/// in its own window (Commands > Background transfer manager) and under the panels (the button beside the command
/// line); both close by themselves when the last job is over (<see cref="Emptied"/>).
/// </summary>
public sealed class TransferManagerView : UserControl
{
    private static readonly Geometry s_pause = Geometry.Parse("M4,3 L7,3 L7,13 L4,13 Z M9,3 L12,3 L12,13 L9,13 Z");
    private static readonly Geometry s_resume = Geometry.Parse("M5,3 L13,8 L5,13 Z");
    private static readonly Geometry s_top = Geometry.Parse("M3,2 L13,2 L13,3.6 L3,3.6 Z M8,5 L13,10.5 L10,10.5 L10,14 L6,14 L6,10.5 L3,10.5 Z");
    private static readonly Geometry s_up = Geometry.Parse("M8,3 L13,9 L10,9 L10,13 L6,13 L6,9 L3,9 Z");
    private static readonly Geometry s_down = Geometry.Parse("M8,13 L3,7 L6,7 L6,3 L10,3 L10,7 L13,7 Z");
    private static readonly Geometry s_remove = Geometry.Parse("M5,4 L5,2.5 L11,2.5 L11,4 L14,4 L14,5.5 L2,5.5 L2,4 Z M3.5,6.5 L12.5,6.5 L11.8,14 L4.2,14 Z");

    private readonly JobQueue _jobs;
    private readonly ListBox _list = new() { Classes = { "nc-table" } };
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _timer;
    private bool _hadJobs;

    public TransferManagerView(JobQueue jobs, Control? trailing = null)
    {
        _jobs = jobs;
        Button B(string text, Action<FileJob> action)
        {
            var button = Nc.Button(text, "sm");
            button.Click += (_, _) =>
            {
                if (Selected is { } job)
                {
                    action(job);
                    Refresh();
                }
            };
            return button;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([
            B("Pause / resume", j => j.TogglePause()),
            B("Stop after this file", j => j.StopAfterCurrentFile = true),
            B("Cancel", j => j.Cancel()),
        ]);
        var bar = new DockPanel { Margin = new Thickness(12, 8) };
        if (trailing is not null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            bar.Children.Add(trailing);
        }
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(12, 0);
        _status[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcMutedBrush");
        DockPanel.SetDock(buttons, Dock.Left);
        bar.Children.AddRange([buttons, _status]);
        _list.Padding = new Thickness(12, 0);
        _list.ItemsSource = Items;
        _list.ItemTemplate = new FuncDataTemplate<TransferRow>((row, _) => RowView(row));
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.AddRange([bar, _list]);
        Content = root;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcSurfaceBrush");

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            if (IsEffectivelyVisible)
            {
                Refresh();
            }
        });
        EventHandler changed = (_, _) => Dispatcher.UIThread.Post(Refresh);
        EventHandler<FileJob> finished = (_, _) => Dispatcher.UIThread.Post(Refresh);
        AttachedToVisualTree += (_, _) =>
        {
            _jobs.QueueChanged += changed;
            _jobs.JobFinished += finished;
            Refresh();
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _jobs.QueueChanged -= changed;
            _jobs.JobFinished -= finished;
            _timer.Stop();
        };
    }

    /// <summary>The last job is over (there were jobs, now there are none): the window closes, the place under the panels hides.</summary>
    public event EventHandler? Emptied;

    /// <summary>The rows: the jobs started at once, then the queue in the order it runs.</summary>
    public ObservableCollection<TransferRow> Items { get; } = [];

    private FileJob? Selected => (_list.SelectedItem as TransferRow)?.Job;

    /// <summary>Row texts (tests).</summary>
    public IReadOnlyList<string> Rows { get; private set; } = [];

    public string StatusText => _status.Text ?? string.Empty;

    public void Refresh()
    {
        var queued = _jobs.QueuedJobs;
        var jobs = _jobs.ActiveJobs.Where(j => !queued.Contains(j)).Concat(queued.Where(j => j.State is not (JobState.Completed or JobState.CompletedWithErrors or JobState.Cancelled or JobState.Failed))).ToList();
        if (!Items.Select(r => r.Job).SequenceEqual(jobs))
        {
            var selected = Selected;
            var old = Items.ToDictionary(r => r.Job);
            Items.Clear();
            foreach (var job in jobs)
            {
                Items.Add(old.GetValueOrDefault(job) ?? new TransferRow(job));
            }
            _list.SelectedItem = Items.FirstOrDefault(r => ReferenceEquals(r.Job, selected)) ?? Items.FirstOrDefault();
        }
        foreach (var row in Items)
        {
            var job = row.Job;
            row.Text = Describe(job);
            row.Percent = job.Progress.Snapshot().Fraction * 100;
            row.IsPaused = job.State == JobState.Paused;
            row.CanPause = job.State is JobState.Running or JobState.Paused;
            row.CanMoveUp = _jobs.CanMove(job, -1);
            row.CanMoveDown = _jobs.CanMove(job, 1);
        }
        Rows = Items.Select(r => r.Text).ToList();
        _status.Text = jobs.Count == 0 ? "No running jobs." : $"{jobs.Count(j => j.State == JobState.Running)} running, {jobs.Count(j => j.State == JobState.Pending)} queued.";
        if (_hadJobs && jobs.Count == 0)
        {
            Emptied?.Invoke(this, EventArgs.Empty);
        }
        _hadJobs = jobs.Count > 0;
    }

    /// <summary>Pauses or resumes a job (its row's first button).</summary>
    public void TogglePause(FileJob job)
    {
        job.TogglePause();
        Refresh();
    }

    /// <summary>Moves a waiting job up (negative) or down in the queue.</summary>
    public void Move(FileJob job, int delta)
    {
        _jobs.Move(job, delta);
        Refresh();
    }

    /// <summary>Gives a waiting job priority: it runs next.</summary>
    public void RunNext(FileJob job)
    {
        _jobs.MoveToFront(job);
        Refresh();
    }

    /// <summary>Removes a job: a waiting one never runs, a running one is cancelled.</summary>
    public void RemoveJob(FileJob job)
    {
        if (_jobs.QueuedJobs.Contains(job))
        {
            _jobs.Remove(job);
        }
        else
        {
            job.Cancel();
        }
        Refresh();
    }

    /// <summary>A row: the text and a progress line, then the buttons (pause / resume, run next, up, down, remove).</summary>
    private Control RowView(TransferRow? row)
    {
        if (row is null)
        {
            return new Panel();
        }
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(TransferRow.Text)));
        var progress = new ProgressBar { Height = 3, MinHeight = 3, Maximum = 100, Margin = new Thickness(0, 4, 0, 0), CornerRadius = new CornerRadius(2) };
        progress.Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, new Binding(nameof(TransferRow.Percent)));
        progress[!ProgressBar.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcAccentBrush");

        Button Action(Geometry icon, string tip, Action<FileJob> action, string? enabledBy)
        {
            var button = new Button
            {
                Content = new PathIcon { Data = icon, Width = 13, Height = 13 },
                Classes = { "nc-icon" },
                Focusable = false,
            };
            ToolTip.SetTip(button, tip);
            Avalonia.Automation.AutomationProperties.SetName(button, tip);
            if (enabledBy is not null)
            {
                button.Bind(IsEnabledProperty, new Binding(enabledBy));
            }
            button.Click += (_, e) =>
            {
                action(row.Job);
                e.Handled = true;
            };
            return button;
        }
        var pause = Action(s_pause, "Pause / resume", TogglePause, nameof(TransferRow.CanPause));
        var pauseIcon = (PathIcon)pause.Content!;
        void ShowPauseIcon() => pauseIcon.Data = row.IsPaused ? s_resume : s_pause;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransferRow.IsPaused))
            {
                ShowPauseIcon();
            }
        };
        ShowPauseIcon();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Children =
            {
                pause,
                Action(s_top, "Run next", RunNext, nameof(TransferRow.CanMoveUp)),
                Action(s_up, "Move up", j => Move(j, -1), nameof(TransferRow.CanMoveUp)),
                Action(s_down, "Move down", j => Move(j, 1), nameof(TransferRow.CanMoveDown)),
                Action(s_remove, "Remove", RemoveJob, null),
            },
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { text, progress } };
        var dock = new DockPanel { Margin = new Thickness(10, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        dock.Children.AddRange([actions, texts]);
        return dock;
    }

    private static string Describe(FileJob job)
    {
        var p = job.Progress.Snapshot();
        var state = job.State switch
        {
            JobState.Pending => "queued",
            JobState.Paused => "paused",
            JobState.Running => string.Format(CultureInfo.CurrentCulture, "%{0:0}", p.Fraction * 100),
            _ => job.State.ToString(),
        };
        var speed = p.BytesPerSecond > 1 ? $"  {SizeFormatter.Human((long)p.BytesPerSecond)}/sn" : string.Empty;
        var remaining = p.Remaining is { } r ? $"  {r:hh\\:mm\\:ss} left" : string.Empty;
        var item = p.CurrentItem is { } c ? $"  —  {c}" : string.Empty;
        return $"{job.Title}   [{state}]  {p.DoneFiles}/{p.TotalFiles} files{speed}{remaining}{item}";
    }
}

/// <summary>Background transfer manager in a window of its own; it closes when the last job is over.</summary>
public sealed class TransferManagerWindow : Window
{
    private readonly TransferManagerView _view;

    public TransferManagerWindow(JobQueue jobs)
    {
        Title = "Background transfer manager";
        Width = 760;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Nc.Apply(this);
        _view = new TransferManagerView(jobs);
        _view.Emptied += (_, _) => Close();
        Content = _view;
    }

    /// <summary>Row texts (tests).</summary>
    public IReadOnlyList<string> Rows => _view.Rows;

    public TransferManagerView View => _view;

    public void Refresh() => _view.Refresh();

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
