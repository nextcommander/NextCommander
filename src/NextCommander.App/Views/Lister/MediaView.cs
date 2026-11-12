using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using NextCommander.App.Services;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace NextCommander.App.Views.Lister;

/// <summary>
/// Lister media player (VLC): video area, seek bar, play / pause, ±10 s / ±60 s, speed, volume, chapters, and resume from
/// the last position. Keys: Space play/pause, Left/Right ±10 s, Ctrl+Left/Right ±60 s, Up/Down volume, [ ] speed.
/// </summary>
public sealed class MediaView : UserControl, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly VideoView _video;
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(8, 0) };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 110 };
    private readonly Button _play = new() { Content = "⏸", MinWidth = 36 };
    private readonly ComboBox _speed = new() { ItemsSource = new[] { "0.5×", "0.75×", "1×", "1.25×", "1.5×", "2×" }, SelectedIndex = 2 };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Value = 80, Width = 90 };
    private readonly MenuFlyout _chapters = new();
    private readonly Button _chapterButton = new() { Content = "Chapters", IsVisible = false };
    private readonly DispatcherTimer _timer;
    private readonly MediaPositions? _positions;
    private Media? _media;
    private Stream? _stream;
    private string? _key;
    private bool _updatingSeek;

    public MediaView(LibVLC libVlc, MediaPositions? positions)
    {
        _positions = positions;
        _libVlc = libVlc;
        _player = new MediaPlayer(libVlc) { Volume = 80 };
        _video = new VideoView { MediaPlayer = _player };
        Focusable = true;

        _play.Click += (_, _) => TogglePause();
        _seek.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingSeek && _player.IsSeekable)
            {
                _player.Position = (float)_seek.Value;
            }
        };
        _speed.SelectionChanged += (_, _) => _player.SetRate(Rate(_speed.SelectedIndex));
        _volume.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                _player.Volume = (int)_volume.Value;
            }
        };
        _chapterButton.Flyout = _chapters;

        Button Jump(string text, int seconds)
        {
            var b = new Button { Content = text };
            b.Click += (_, _) => SeekBy(seconds);
            return b;
        }
        var controls = new DockPanel { Margin = new Thickness(6, 4) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        left.Children.AddRange([Jump("−60", -60), Jump("−10", -10), _play, Jump("+10", 10), Jump("+60", 60), _time]);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        right.Children.AddRange([_chapterButton, _speed, new TextBlock { Text = "🔊", VerticalAlignment = VerticalAlignment.Center }, _volume]);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        controls.Children.AddRange([left, right, _seek]);

        var root = new DockPanel();
        DockPanel.SetDock(controls, Dock.Bottom);
        root.Children.AddRange([controls, _video]);
        Content = root;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateTime());
        _player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _play.Content = "▶";
            Forget();
        });
        _player.LengthChanged += (_, _) => Dispatcher.UIThread.Post(LoadChapters);
    }

    /// <summary>Status line text: position, length, speed.</summary>
    public string StatusText { get; private set; } = string.Empty;

    public event EventHandler? StatusChanged;

    /// <summary>Plays a local file or any stream (archives, FTP). <paramref name="key"/> identifies it for resuming.</summary>
    public void Play(string key, string? localPath, Func<Stream>? openStream)
    {
        Stop();
        _key = key;
        if (localPath is not null)
        {
            _media = new Media(_libVlc, localPath, FromType.FromPath);
        }
        else
        {
            _stream = openStream!();
            _media = new Media(_libVlc, new StreamMediaInput(_stream));
        }
        _player.Play(_media);
        if (_positions?.Get(key) is { } seconds)
        {
            // continue where it stopped (VLC needs the media to start before seeking)
            DispatcherTimer.RunOnce(() => _player.Time = (long)(seconds * 1000), TimeSpan.FromMilliseconds(300));
        }
        _play.Content = "⏸";
        _timer.Start();
    }

    public void TogglePause()
    {
        if (_player.IsPlaying)
        {
            _player.SetPause(true);
            _play.Content = "▶";
            Remember();
        }
        else
        {
            _player.SetPause(false);
            if (_player.State is VLCState.Ended or VLCState.Stopped && _media is not null)
            {
                _player.Play(_media);
            }
            _play.Content = "⏸";
        }
    }

    public void SeekBy(int seconds)
    {
        if (_player.IsSeekable)
        {
            _player.Time = Math.Clamp(_player.Time + (seconds * 1000L), 0, Math.Max(0, _player.Length - 500));
        }
    }

    /// <summary>Stops playback and remembers the position.</summary>
    public void Stop()
    {
        _timer.Stop();
        if (_media is not null)
        {
            Remember();
            _player.Stop();
            _media.Dispose();
            _media = null;
        }
        _stream?.Dispose();
        _stream = null;
    }

    private void Remember()
    {
        if (_key is not null && _player.Length > 0)
        {
            _positions?.Set(_key, _player.Time / 1000.0, _player.Length / 1000.0);
        }
    }

    private void Forget()
    {
        if (_key is not null && _player.Length > 0)
        {
            _positions?.Set(_key, 0, _player.Length / 1000.0);
        }
    }

    private static float Rate(int index) => index switch
    {
        0 => 0.5f,
        1 => 0.75f,
        3 => 1.25f,
        4 => 1.5f,
        5 => 2f,
        _ => 1f,
    };

    private void LoadChapters()
    {
        _chapters.Items.Clear();
        var chapters = _player.FullChapterDescriptions(-1);
        foreach (var chapter in chapters)
        {
            var offset = chapter.TimeOffset;
            var item = new MenuItem { Header = $"{Format(offset)}  {chapter.Name}" };
            item.Click += (_, _) => _player.Time = offset;
            _chapters.Items.Add(item);
        }
        _chapterButton.IsVisible = chapters.Length > 1;
    }

    private void UpdateTime()
    {
        _updatingSeek = true;
        _seek.Value = Math.Clamp(_player.Position, 0, 1);
        _updatingSeek = false;
        _time.Text = $"{Format(_player.Time)} / {Format(_player.Length)}";
        StatusText = string.Format(CultureInfo.CurrentCulture, "{0} / {1}{2}", Format(_player.Time), Format(_player.Length),
            Math.Abs(_player.Rate - 1) > 0.01 ? $"  {_player.Rate:0.##}×" : string.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Player keys (the Lister forwards keys it does not use itself).</summary>
    public bool HandleKey(Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        switch (key)
        {
            case Key.Space:
                TogglePause();
                return true;
            case Key.Left:
                SeekBy(ctrl ? -60 : -10);
                return true;
            case Key.Right:
                SeekBy(ctrl ? 60 : 10);
                return true;
            case Key.Up:
                _volume.Value = Math.Min(100, _volume.Value + 5);
                return true;
            case Key.Down:
                _volume.Value = Math.Max(0, _volume.Value - 5);
                return true;
            case Key.OemOpenBrackets:
                _speed.SelectedIndex = Math.Max(0, _speed.SelectedIndex - 1);
                return true;
            case Key.OemCloseBrackets:
                _speed.SelectedIndex = Math.Min(5, _speed.SelectedIndex + 1);
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
    }
}
