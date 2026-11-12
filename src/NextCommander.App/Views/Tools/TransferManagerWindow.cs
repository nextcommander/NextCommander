using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Tools;

/// <summary>Background transfer manager: running and queued jobs with progress, pause / resume, stop after file, cancel.</summary>
public sealed class TransferManagerWindow : Window
{
    private readonly JobQueue _jobs;
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 3), FontSize = 12 };
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<FileJob> _shown = [];

    public TransferManagerWindow(JobQueue jobs)
    {
        _jobs = jobs;
        Title = "Background transfer manager";
        Width = 760;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Button B(string text, Action<FileJob> action)
        {
            var button = new Button { Content = text };
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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 6) };
        buttons.Children.AddRange([
            B("Pause / resume", j => j.TogglePause()),
            B("Stop after this file", j => j.StopAfterCurrentFile = true),
            B("Cancel", j => j.Cancel()),
        ]);
        var statusBar = new Border { Child = _status };
        statusBar[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(statusBar);
        root.Children.Add(_list);
        Content = root;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        Opened += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private FileJob? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;

    /// <summary>Row texts (tests).</summary>
    public IReadOnlyList<string> Rows { get; private set; } = [];

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
