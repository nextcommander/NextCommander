using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using NextCommander.App.Services.Design;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// The background transfers: running and queued jobs with progress, pause / resume, stop after file, cancel. Shown in its
/// own window (Commands > Background transfer manager) and under the panels (the button beside the command line).
/// It reads the jobs again twice a second while it is shown.
/// </summary>
public sealed class TransferManagerView : UserControl
{
    private readonly JobQueue _jobs;
    private readonly ListBox _list = new() { Classes = { "nc-table" } };
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<FileJob> _shown = [];

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
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((row, _) =>
            new TextBlock { Text = row, Padding = new Thickness(10, 6), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Classes = { "noloc" } });
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
        AttachedToVisualTree += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private FileJob? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;

    /// <summary>Row texts (tests).</summary>
    public IReadOnlyList<string> Rows { get; private set; } = [];

    public string StatusText => _status.Text ?? string.Empty;

    public void Refresh()
    {
        var selected = Selected;
        _shown = _jobs.ActiveJobs;
        Rows = _shown.Select(Describe).ToList();
        _list.ItemsSource = Rows;
        _list.SelectedIndex = selected is null ? (_shown.Count > 0 ? 0 : -1) : Math.Max(0, _shown.ToList().IndexOf(selected));
        _status.Text = _shown.Count == 0 ? "No running jobs." : $"{_shown.Count(j => j.State == JobState.Running)} running, {_shown.Count(j => j.State == JobState.Pending)} queued.";
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

/// <summary>Background transfer manager in a window of its own.</summary>
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
        Content = _view;
    }

    /// <summary>Row texts (tests).</summary>
    public IReadOnlyList<string> Rows => _view.Rows;

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
