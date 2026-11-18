using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using NextCommander.Core.Config;
using NextCommander.Platform;
using Serilog;

namespace NextCommander.App.Views;

/// <summary>What a remote desktop tab is doing.</summary>
public enum RemoteDesktopState
{
    Connecting,
    Connected,
    Ended,
}

/// <summary>
/// A remote desktop session in a page tab of the main window (user's request, 18 November 2026). Windows: the system's
/// Remote Desktop control (<see cref="Platform.Windows.RemoteDesktopControl"/>, the engine of mstsc) in a child window;
/// Linux: the X11 FreeRDP drawing into a window of the tab (/parent-window). The session follows the tab's size (a
/// fixed size is scaled into it). When it ends the tab says so and offers to connect again or to close.
/// </summary>
public sealed class RemoteDesktopView : UserControl, IDisposable
{
    private readonly Grid _layers = new();
    private readonly Border _ended;
    private readonly TextBlock _message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = Avalonia.Media.TextAlignment.Center };
    private readonly Border _connecting;
    private SessionHost? _host;
    private RemoteDesktopState _state;

    /// <summary>Whether sessions can open in a tab on this system.</summary>
    public static bool IsSupported => RemoteDesktopLauncher.CanEmbed;

    public RemoteDesktopView(RemoteConnection connection, string? password, string? folder)
    {
        Connection = connection;
        Password = password;
        Folder = folder;
        var again = new Button { Content = "Connect again", Classes = { "dialog" }, IsDefault = true };
        again.Click += (_, _) => Start();
        var close = new Button { Content = "Close", Classes = { "dialog" } };
        close.Click += (_, _) => Close();
        _ended = new Border
        {
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 12,
                MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    _message,
                    new StackPanel { Classes = { "buttonrow" }, Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { again, close } },
                },
            },
        };
        _layers.Children.Add(_ended);
        // above the session (a native window covers what is under it): shown while connecting
        _connecting = new Border
        {
            Padding = new Thickness(8, 4),
            Child = new TextBlock { Text = $"Connecting to {connection.Host} ..." },
        };
        _connecting.Bind(Border.BackgroundProperty, _connecting.GetResourceObservable("SystemControlBackgroundBaseLowBrush"));
        DockPanel.SetDock(_connecting, Dock.Top);
        Content = new DockPanel { Children = { _connecting, _layers } };
        Focusable = true;
        Start();
    }

    public RemoteConnection Connection { get; }

    /// <summary>The password given for the session (connecting again uses it).</summary>
    private string? Password { get; }

    /// <summary>The active panel's folder when it is shared in the session.</summary>
    private string? Folder { get; }

    /// <summary>The tab's title: the connection's name.</summary>
    public string Title => Connection.Name.Length > 0 ? Connection.Name : Connection.Host;

    public RemoteDesktopState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                _connecting.IsVisible = value == RemoteDesktopState.Connecting;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The text shown when the session has ended (tests).</summary>
    public string? EndedText => _ended.IsVisible ? _message.Text : null;

    public event EventHandler? StateChanged;

    /// <summary>The tab is to be closed (its × or the Close button).</summary>
    public event EventHandler? Closed;

    /// <summary>Starts the session (again): a new native window in the tab.</summary>
    public void Start()
    {
        StopHost();
        _ended.IsVisible = false;
        State = RemoteDesktopState.Connecting;
        _host = new SessionHost(this);
        _layers.Children.Insert(0, _host);
    }

    /// <summary>Ends the session and closes the tab.</summary>
    public void Close()
    {
        StopHost();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The session ended (or could not start): the native window goes, the message and the buttons show.</summary>
    internal void Ended(string message)
    {
        StopHost();
        _message.Text = message;
        _ended.IsVisible = true;
        State = RemoteDesktopState.Ended;
    }

    private void Connected() => State = RemoteDesktopState.Connected;

    private void StopHost()
    {
        if (_host is { } host)
        {
            _host = null;
            host.Stop();
            _layers.Children.Remove(host); // its native window is destroyed when it leaves the tree
        }
    }

    public void Dispose() => StopHost();

    /// <summary>
    /// The native window of the session. It is made when the tab is shown; the session starts once the tab has its size
    /// (the remote desktop gets that size), and is told new sizes after.
    /// </summary>
    private sealed class SessionHost(RemoteDesktopView owner) : NativeControlHost
    {
        private Platform.Windows.RemoteDesktopControl? _control;
        private Process? _freeRdp;
        private ulong _xWindow;
        private bool _created;
        private bool _started;
        private bool _stopped;
        private DispatcherTimer? _poll;
        private DispatcherTimer? _resize;
        private bool _wasConnected;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _created = true;
            var (width, height) = Pixels();
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _control = Platform.Windows.RemoteDesktopControl.Create(parent.Handle, width, height);
                    Dispatcher.UIThread.Post(TryStart, DispatcherPriority.Background);
                    return new PlatformHandle(_control.Window, "HWND");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or DllNotFoundException or EntryPointNotFoundException)
                {
                    Log.Warning(ex, "The remote desktop control could not be made");
                    Dispatcher.UIThread.Post(() => owner.Ended($"The remote desktop could not be shown here: {ex.Message}"));
                    return base.CreateNativeControlCore(parent);
                }
            }
            var handle = base.CreateNativeControlCore(parent);
            _xWindow = (ulong)handle.Handle;
            Dispatcher.UIThread.Post(TryStart, DispatcherPriority.Background);
            return handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            Stop();
            if (_control is { } own)
            {
                _control = null;
                own.Dispose(); // its window is ours
                return;
            }
            base.DestroyNativeControlCore(control);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (!_started)
            {
                TryStart();
                return;
            }
            // the window follows at once; the session after the size has settled
            var (width, height) = Pixels();
            _control?.Resize(width, height, session: false);
            _resize?.Stop();
            _resize = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
            {
                _resize?.Stop();
                var (w, h) = Pixels();
                _control?.Resize(w, h, session: Core.Net.RemoteDesktop.FollowsWindow(owner.Connection));
            });
            _resize.Start();
        }

        /// <summary>The tab's size in device pixels.</summary>
        private (int Width, int Height) Pixels()
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return ((int)Math.Round(Bounds.Width * scale), (int)Math.Round(Bounds.Height * scale));
        }

        /// <summary>Connects once the native window is there and the tab has a size.</summary>
        private void TryStart()
        {
            var (width, height) = Pixels();
            if (_started || _stopped || !_created || width < 50 || height < 50)
            {
                return;
            }
            _started = true;
            if (_control is { } control)
            {
                try
                {
                    control.Resize(width, height, session: false);
                    if (control.Connect(owner.Connection, owner.Password, width, height) is { Count: > 0 } refused)
                    {
                        Log.Debug("The remote desktop control did not take {Settings}", string.Join(", ", refused));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
                {
                    Log.Warning(ex, "The remote desktop session could not start");
                    owner.Ended($"The session could not start: {ex.Message}");
                    return;
                }
                _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Poll());
                _poll.Start();
                return;
            }
            if (_xWindow != 0)
            {
                _ = StartFreeRdpAsync(width, height);
            }
        }

        /// <summary>Windows: the control's state, read every second (connected; ended after it was connecting or connected).</summary>
        private void Poll()
        {
            if (_control is not { } control)
            {
                return;
            }
            switch (control.State)
            {
                case 1:
                    _wasConnected = true;
                    owner.Connected();
                    break;
                case 0:
                    var reason = control.ExtendedDisconnectReason;
                    owner.Ended(_wasConnected ? "The remote desktop session ended."
                        : reason != 0 ? $"The connection to {owner.Connection.Host} could not be made (reason {reason})."
                        : $"The connection to {owner.Connection.Host} could not be made.");
                    break;
            }
        }

        private async Task StartFreeRdpAsync(int width, int height)
        {
            try
            {
                var process = await RemoteDesktopLauncher.StartEmbeddedAsync(owner.Connection, owner.Password, owner.Folder, _xWindow, width, height);
                if (_stopped)
                {
                    End(process);
                    return;
                }
                _freeRdp = process;
                process.EnableRaisingEvents = true;
                var started = Stopwatch.StartNew();
                process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (!_stopped)
                    {
                        // FreeRDP ending within seconds: it could not connect (wrong password, server not there ...)
                        owner.Ended(started.Elapsed < TimeSpan.FromSeconds(5) && process.ExitCode != 0
                            ? $"The connection to {owner.Connection.Host} could not be made (FreeRDP exit code {process.ExitCode})."
                            : "The remote desktop session ended.");
                    }
                });
                owner.Connected();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                owner.Ended($"The session could not start: {ex.Message}");
            }
        }

        /// <summary>Ends the session (the tab closes or connects again).</summary>
        public void Stop()
        {
            _stopped = true;
            _poll?.Stop();
            _resize?.Stop();
            _control?.Disconnect();
            if (_freeRdp is { } process)
            {
                _freeRdp = null;
                End(process);
            }
        }

        private static void End(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(); // FreeRDP's session: its window goes with the tab
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
