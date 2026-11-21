using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.Core.Localization;
using NextCommander.Core.Net;

namespace NextCommander.App.Views.Tools;

/// <summary>
/// Net > Network tools (MobaXterm's network tools, user's request, 21 November 2026): ping a host, scan its TCP ports,
/// wake a computer with Wake-on-LAN. Results are listed as they come; Stop ends a ping or a scan.
/// </summary>
public sealed class NetworkToolsWindow : Window
{
    private static readonly FontFamily s_mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private readonly TabControl _tabs = new();
    private readonly TextBox _pingHost = new() { PlaceholderText = "host name or IP address", Classes = { "noloc" } };
    private readonly NumericUpDown _pingCount = new() { Minimum = 1, Maximum = 1000, Value = 4, Width = 130, FormatString = "0" };
    private readonly Button _pingStart = new() { Content = "Ping", Classes = { "accent" }, MinWidth = 88 };
    private readonly ListBox _pingResults = new() { FontFamily = s_mono, FontSize = 12.5 };
    private readonly TextBlock _pingSummary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Classes = { "noloc" } };
    private readonly TextBox _scanHost = new() { PlaceholderText = "host name or IP address", Classes = { "noloc" } };
    private readonly TextBox _scanPorts = new() { PlaceholderText = "e.g. 22, 80, 443, 8000-8100", Classes = { "noloc" } };
    private readonly NumericUpDown _scanTimeout = new() { Minimum = 100, Maximum = 10000, Increment = 100, Value = 800, Width = 130, FormatString = "0" };
    private readonly Button _scanStart = new() { Content = "Scan", Classes = { "accent" }, MinWidth = 88 };
    private readonly ProgressBar _scanProgress = new() { Height = 6, MinHeight = 6, IsVisible = false };
    private readonly ListBox _scanResults = new() { FontFamily = s_mono, FontSize = 12.5 };
    private readonly TextBlock _scanSummary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Classes = { "noloc" } };
    private readonly TextBox _wakeMac = new() { PlaceholderText = "00:11:22:AA:BB:CC", Classes = { "noloc" } };
    private readonly TextBox _wakeBroadcast = new() { Text = "255.255.255.255", Classes = { "noloc" } };
    private readonly NumericUpDown _wakePort = new() { Minimum = 1, Maximum = 65535, Value = 9, Width = 130, FormatString = "0" };
    private readonly TextBlock _wakeStatus = new() { TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
    private CancellationTokenSource? _ping;
    private CancellationTokenSource? _scan;

    public NetworkToolsWindow(string? host = null)
    {
        Title = "Network tools";
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _pingHost.Text = host;
        _scanHost.Text = host;
        _scanPorts.Text = string.Join(", ", NetworkTools.CommonPorts);

        _tabs.Items.Add(new TabItem { FontSize = 15, Header = "Ping", Content = PingPage() });
        _tabs.Items.Add(new TabItem { FontSize = 15, Header = "Port scan", Content = ScanPage() });
        _tabs.Items.Add(new TabItem { FontSize = 15, Header = "Wake-on-LAN", Content = WakePage() });
        Content = new Border { Child = _tabs, Padding = new Thickness(12) };
        Closed += (_, _) =>
        {
            _ping?.Cancel();
            _scan?.Cancel();
        };
    }

    private static Control Labeled(string caption, Control input)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.AddRange([new TextBlock { Text = caption, FontSize = 12.5, FontWeight = FontWeight.SemiBold }, input]);
        return stack;
    }

    private static Control Page(Control inputs, Control? progress, Control results, Control summary)
    {
        var top = new StackPanel { Spacing = 10, Margin = new Thickness(4, 12, 4, 10) };
        top.Children.Add(inputs);
        if (progress is not null)
        {
            top.Children.Add(progress);
        }
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(summary, Dock.Bottom);
        summary.Margin = new Thickness(4, 8, 4, 0);
        var frame = new Border { Child = results, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        frame[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        root.Children.AddRange([top, summary, frame]);
        return root;
    }

    private static Grid Row(params (Control Control, string Width)[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", cells.Select(c => c.Width))), ColumnSpacing = 10 };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i].Control, i);
            cells[i].Control.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(cells[i].Control);
        }
        return grid;
    }

    // ---------------------------------------------------------------- ping

    private Control PingPage()
    {
        _pingStart.Click += (_, _) =>
        {
            if (_ping is not null)
            {
                _ping.Cancel();
            }
            else
            {
                _ = PingAsync();
            }
        };
        _pingHost.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && _ping is null)
            {
                e.Handled = true;
                _ = PingAsync();
            }
        };
        return Page(Row((Labeled("Host", _pingHost), "*"), (Labeled("Count", _pingCount), "Auto"), (_pingStart, "Auto")), null, _pingResults, _pingSummary);
    }

    /// <summary>Pings the host as many times as asked (one a second); the summary at the end (tests call it).</summary>
    public async Task PingAsync(TimeSpan? interval = null)
    {
        var host = (_pingHost.Text ?? string.Empty).Trim();
        if (host.Length == 0)
        {
            _pingSummary.Text = Localizer.T("Enter a host name or IP address.");
            return;
        }
        _ping = new CancellationTokenSource();
        _pingStart.Content = Localizer.T("Stop");
        var lines = new List<string>();
        _pingResults.ItemsSource = lines;
        var times = new List<long>();
        var sent = 0;
        try
        {
            var count = (int)(_pingCount.Value ?? 4);
            for (var i = 1; i <= count && !_ping.IsCancellationRequested; i++)
            {
                var reply = await NetworkTools.PingAsync(host, i, TimeSpan.FromSeconds(2), _ping.Token);
                sent++;
                if (reply.Milliseconds is { } ms)
                {
                    times.Add(ms);
                    lines.Add(Localizer.T("Reply from {0}: time {1} ms, TTL {2}").Replace("{0}", reply.Address, StringComparison.Ordinal)
                        .Replace("{1}", ms.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                        .Replace("{2}", reply.Ttl?.ToString(CultureInfo.CurrentCulture) ?? "?", StringComparison.Ordinal));
                }
                else
                {
                    lines.Add(Localizer.T("No reply ({0})").Replace("{0}", reply.Status, StringComparison.Ordinal));
                }
                _pingResults.ItemsSource = lines.ToList();
                _pingResults.ScrollIntoView(lines.Count - 1);
                if (i < count)
                {
                    await Task.Delay(interval ?? TimeSpan.FromSeconds(1), _ping.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop
        }
        var lost = sent == 0 ? 0 : 100 * (sent - times.Count) / sent;
        _pingSummary.Text = Localizer.T("Sent {0}, received {1}, lost {2}%").Replace("{0}", sent.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{1}", times.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{2}", lost.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            + (times.Count > 0
                ? "   " + Localizer.T("min / avg / max {0} / {1} / {2} ms").Replace("{0}", times.Min().ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                    .Replace("{1}", ((long)times.Average()).ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                    .Replace("{2}", times.Max().ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                : string.Empty);
        _ping = null;
        _pingStart.Content = Localizer.T("Ping");
    }

    /// <summary>The ping fields (tests).</summary>
    public void SetPing(string host, int count)
    {
        _pingHost.Text = host;
        _pingCount.Value = count;
    }

    public IReadOnlyList<string> PingLines => _pingResults.ItemsSource as IReadOnlyList<string> ?? [];

    public string PingSummary => _pingSummary.Text ?? string.Empty;

    // ---------------------------------------------------------------- port scan

    private Control ScanPage()
    {
        _scanStart.Click += (_, _) =>
        {
            if (_scan is not null)
            {
                _scan.Cancel();
            }
            else
            {
                _ = ScanAsync();
            }
        };
        var common = new Button { Content = "Common ports" };
        ToolTip.SetTip(common, "SSH, HTTP(S), SMB, RDP, databases, VNC ...");
        common.Click += (_, _) => _scanPorts.Text = string.Join(", ", NetworkTools.CommonPorts);
        var all = new Button { Content = "1-1024" };
        all.Click += (_, _) => _scanPorts.Text = "1-1024";
        var inputs = new StackPanel { Spacing = 10 };
        inputs.Children.Add(Row((Labeled("Host", _scanHost), "*"), (Labeled("Timeout (ms)", _scanTimeout), "Auto"), (_scanStart, "Auto")));
        inputs.Children.Add(Row((Labeled("Ports", _scanPorts), "*"), (common, "Auto"), (all, "Auto")));
        return Page(inputs, _scanProgress, _scanResults, _scanSummary);
    }

    /// <summary>Scans the ports; the open ones are listed as they are found (tests call it).</summary>
    public async Task ScanAsync()
    {
        var host = (_scanHost.Text ?? string.Empty).Trim();
        var ports = NetworkTools.ParsePorts(_scanPorts.Text ?? string.Empty, out var bad);
        if (host.Length == 0)
        {
            _scanSummary.Text = Localizer.T("Enter a host name or IP address.");
            return;
        }
        if (bad is not null || ports.Count == 0)
        {
            _scanSummary.Text = Localizer.T("Not a port or range: {0}").Replace("{0}", bad ?? string.Empty, StringComparison.Ordinal);
            return;
        }
        _scan = new CancellationTokenSource();
        _scanStart.Content = Localizer.T("Stop");
        _scanProgress.Maximum = ports.Count;
        _scanProgress.Value = 0;
        _scanProgress.IsVisible = true;
        var lines = new List<string>();
        _scanResults.ItemsSource = lines;
        _scanSummary.Text = Localizer.T("Scanning {0} ports...").Replace("{0}", ports.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        string Line(OpenPort p) => $"{p.Port,5}  {(p.Service.Length > 0 ? p.Service : "?"),-16} {p.Milliseconds} ms";
        try
        {
            var open = await NetworkTools.ScanAsync(host, ports, TimeSpan.FromMilliseconds((double)(_scanTimeout.Value ?? 800)), 64,
                new Progress<int>(n => _scanProgress.Value = n),
                found => Dispatcher.UIThread.Post(() =>
                {
                    lines.Add(Line(found));
                    _scanResults.ItemsSource = lines.ToList();
                }), _scan.Token);
            _scanResults.ItemsSource = open.Select(Line).ToList();
            _scanSummary.Text = Localizer.T("{0} open of {1} ports").Replace("{0}", open.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                .Replace("{1}", ports.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            _scanSummary.Text = Localizer.T("Stopped.");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            _scanSummary.Text = ex.Message;
        }
        _scan = null;
        _scanProgress.IsVisible = false;
        _scanStart.Content = Localizer.T("Scan");
    }

    /// <summary>The scan fields (tests).</summary>
    public void SetScan(string host, string ports, int timeoutMs)
    {
        _scanHost.Text = host;
        _scanPorts.Text = ports;
        _scanTimeout.Value = timeoutMs;
    }

    public IReadOnlyList<string> ScanLines => _scanResults.ItemsSource as IReadOnlyList<string> ?? [];

    public string ScanSummary => _scanSummary.Text ?? string.Empty;

    // ---------------------------------------------------------------- Wake-on-LAN

    private Control WakePage()
    {
        var send = new Button { Content = "Wake up", Classes = { "accent" }, MinWidth = 88 };
        send.Click += async (_, _) => await WakeAsync();
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(4, 12, 4, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = "Sends the magic packet to the network card with this MAC address; the computer wakes when Wake-on-LAN is on in its firmware and network card settings.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
        });
        panel.Children.Add(Labeled("MAC address", _wakeMac));
        panel.Children.Add(Row((Labeled("Broadcast address", _wakeBroadcast), "*"), (Labeled("Port", _wakePort), "Auto"), (send, "Auto")));
        panel.Children.Add(new TextBlock { Text = "255.255.255.255 reaches this network; for another one, its broadcast address (e.g. 192.168.1.255).", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_wakeStatus);
        return panel;
    }

    /// <summary>Sends the magic packet (tests call it).</summary>
    public async Task WakeAsync()
    {
        if (NetworkTools.ParseMac(_wakeMac.Text) is not { } mac)
        {
            _wakeStatus.Foreground = Brushes.IndianRed;
            _wakeStatus.Text = Localizer.T("Not a MAC address (six pairs of hex digits, e.g. 00:11:22:AA:BB:CC).");
            return;
        }
        try
        {
            await NetworkTools.WakeAsync(mac, (_wakeBroadcast.Text ?? string.Empty).Trim(), (int)(_wakePort.Value ?? 9));
            _wakeStatus.ClearValue(TextBlock.ForegroundProperty);
            _wakeStatus.Text = Localizer.T("Magic packet sent to {0}.").Replace("{0}", string.Join(":", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            _wakeStatus.Foreground = Brushes.IndianRed;
            _wakeStatus.Text = ex.Message;
        }
    }

    /// <summary>The Wake-on-LAN fields (tests).</summary>
    public void SetWake(string mac, string broadcast, int port)
    {
        _wakeMac.Text = mac;
        _wakeBroadcast.Text = broadcast;
        _wakePort.Value = port;
    }

    public string WakeStatus => _wakeStatus.Text ?? string.Empty;

    /// <summary>Shows a page: 0 ping, 1 port scan, 2 Wake-on-LAN.</summary>
    public void ShowPage(int index) => _tabs.SelectedIndex = index;
}
