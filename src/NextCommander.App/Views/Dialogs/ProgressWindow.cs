using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Dialogs;

/// <summary>Non-modal progress for one job: current file, total, speed, ETA, pause / background / cancel.</summary>
public sealed class ProgressWindow : DialogWindow
{
    private readonly FileJob _job;
    private readonly TextBlock _item = Label(string.Empty);
    private readonly ProgressBar _fileBar = new() { Minimum = 0, Maximum = 1, Height = 6 };
    private readonly ProgressBar _totalBar = new() { Minimum = 0, Maximum = 1, Height = 10 };
    private readonly TextBlock _stats = Label(string.Empty);
    private readonly Button _pause;
    private readonly DispatcherTimer _timer;

    public ProgressWindow(FileJob job)
        : base(job.Title, 520)
    {
        _job = job;
        ShowInTaskbar = true;
        _item.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        _item.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        Body.Children.Add(_item);
        Body.Children.Add(_fileBar);
        Body.Children.Add(_totalBar);
        Body.Children.Add(_stats);
        Body.Children.Add(SpeedLimitPicker(job));

        _pause = AddButton("Pause", TogglePause);
        AddButton("Background", Close);
        AddButton("Stop after current file", () => _job.StopAfterCurrentFile = true);
        AddButton("Cancel", _job.Cancel); // not IsCancel: Esc only hides the window, the job keeps running

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => UpdateView());
        Opened += (_, _) =>
        {
            UpdateView();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private static readonly (string Text, long Bytes)[] s_speeds =
    [
        ("Unlimited", 0), ("1 MB/s", 1L << 20), ("5 MB/s", 5L << 20), ("10 MB/s", 10L << 20),
        ("25 MB/s", 25L << 20), ("50 MB/s", 50L << 20), ("100 MB/s", 100L << 20),
    ];

    /// <summary>"Hız sınırı" row: presets or a typed value ("3M"), applied to the running job right away.</summary>
    public static Control SpeedLimitPicker(FileJob job)
    {
        var current = job.SpeedLimit.BytesPerSecond;
        var texts = s_speeds.Select(s => s.Text).ToList();
        var combo = new AutoCompleteBox
        {
            ItemsSource = texts,
            FilterMode = AutoCompleteFilterMode.None,
            MinimumPrefixLength = 0,
            Width = 160,
            Text = current <= 0 ? texts[0] : s_speeds.FirstOrDefault(s => s.Bytes == current).Text ?? SizeFormatter.Human(current) + "/s",
        };
        void Apply()
        {
            var text = combo.Text?.Trim() ?? string.Empty;
            var preset = s_speeds.FirstOrDefault(s => string.Equals(s.Text, text, StringComparison.OrdinalIgnoreCase));
            if (preset.Text is not null)
            {
                job.SpeedLimit.BytesPerSecond = preset.Bytes;
            }
            else if (CopyDialog.TryParseSpeed(text.Replace("MB", "M", StringComparison.OrdinalIgnoreCase).Replace("KB", "K", StringComparison.OrdinalIgnoreCase), out var bytes))
            {
                job.SpeedLimit.BytesPerSecond = bytes;
            }
        }
        combo.SelectionChanged += (_, _) => Apply();
        combo.LostFocus += (_, _) => Apply();
        combo.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Apply();
                e.Handled = true;
            }
        };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Speed limit:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        row.Children.Add(combo);
        return row;
    }

    private void TogglePause()
    {
        _job.TogglePause();
        _pause.Content = _job.Pause.IsPaused ? "Resume" : "Pause";
    }

    private void UpdateView()
    {
        var p = _job.Progress.Snapshot();
        var culture = CultureInfo.CurrentCulture;
        _item.Text = p.IsCounting ? "Counting..." : p.CurrentItem ?? string.Empty;
        _fileBar.Value = p.CurrentFileFraction;
        _totalBar.Value = p.Fraction;

        var remaining = p.Remaining is { } r ? $" — {r:hh\\:mm\\:ss} left" : string.Empty;
        var speed = p.BytesPerSecond > 0 ? $" — {SizeFormatter.Human((long)p.BytesPerSecond)}/s" : string.Empty;
        _stats.Text = string.Format(culture, "{0} / {1} files — {2} / {3}{4}{5}{6}",
            p.DoneFiles, p.TotalFiles, SizeFormatter.Human(p.DoneBytes), SizeFormatter.Human(p.TotalBytes), speed, remaining,
            _job.Pause.IsPaused ? " — DURAKLATILDI" : string.Empty);
        Title = string.Format(culture, "{0:P0} {1}", p.Fraction, _job.Title);
    }
}
