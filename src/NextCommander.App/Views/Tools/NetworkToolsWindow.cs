using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;
using NextCommander.Core.Net;

namespace NextCommander.App.Views.Tools;

/// <summary>What the network tools need from the program: the settings (recent computers, saved Wake-on-LAN computers) and how to open an address.</summary>
public sealed record NetworkToolsRequest(
    string? Host,
    AppSettings Settings,
    Action Save,
    Func<string, Task>? OpenAddress = null,
    Action<string>? OpenUrl = null);

/// <summary>
/// Connection > Network tools (user's request, 21 November 2026; the look of the user's template, 22 November 2026): ping a
/// computer (replies, loss, times as a chart and a table), scan its TCP ports (open, closed and filtered ones, with Open /
/// Connect on the open ones) and wake a computer with Wake-on-LAN (saved computers; when its address is known, the window
/// waits for it to answer). Enter starts, Esc stops (and closes when nothing runs), Ctrl+1 / 2 / 3 switch the pages.
/// </summary>
public sealed class NetworkToolsWindow : Window
{
    private const int MaxRecent = 8;

    // template icons (24 × 24, stroked)
    private const string IcPing = "M3 12h4l3-8 4 16 3-8h4";
    private const string IcScan = "M5 4h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M7 9h.01M11 9h6M7 13h.01M11 13h6M7 17h.01M11 17h6";
    private const string IcWake = "M12 3v9 M6.3 6.3a8 8 0 1 0 11.4 0";
    private const string IcCheck = "M5 12l5 5 9-10";
    private const string IcX = "M6 6l12 12M18 6L6 18";
    private const string IcAlert = "M12 4L2 20h20z M12 10v4M12 17h.01";
    private const string IcCopy = "M10 8h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-8a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2z M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2";
    private const string IcPlay = "M7 4l13 8-13 8z";
    private const string IcStop = "M8 6h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2z";
    private const string IcPc = "M5 4h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M8 20h8M12 16v4";
    private const string IcPlus = "M12 5v14M5 12h14";
    private const string IcLink = "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5";
    private const string IcPlug = "M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0z M12 17v4";

    /// <summary>The port sets of the scan (the last two: a range and the user's own list).</summary>
    private static readonly (string Key, string Name, int[] Ports)[] s_presets =
    [
        ("common", "Common", NetworkTools.CommonPorts),
        ("web", "Web", [80, 443, 8080, 8443]),
        ("mail", "Mail", [25, 110, 143, 465, 587, 993, 995]),
        ("windows", "Windows", [135, 139, 445, 3389, 5985]),
        ("db", "Databases", [1433, 1521, 3306, 5432, 6379, 27017]),
    ];

    private readonly NetworkToolsRequest _request;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 14) };
    private readonly ContentControl _form = new();
    private readonly ContentControl _body = new();
    private readonly ScrollViewer _bodyScroll;
    private readonly TextBlock _footerHint = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private int _page;

    // the fields (kept across pages)
    private string _host = string.Empty;
    private int _pingCount = 4;
    private string _preset = "common";
    private string _customPorts = string.Empty;
    private int _timeoutMs = 800;
    private string _mac = string.Empty;
    private string _broadcast = "255.255.255.255";
    private int _wakePort = 9;
    private int? _device;

    // what runs and what came of it
    private CancellationTokenSource? _run;
    private PingRun? _ping;
    private ScanRun? _scan;
    private WakeRun? _wake;
    private bool _showClosed;
    private readonly Dictionary<string, bool> _online = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PingRun
    {
        public required string Host { get; init; }
        public string Address { get; set; } = string.Empty;
        public int Count { get; init; }
        public List<PingReply> Replies { get; } = [];
        public bool NotFound { get; set; }
    }

    private sealed class ScanRun
    {
        public required string Host { get; init; }
        public int Total { get; init; }
        public List<PortResult> Results { get; } = [];
        public string? Error { get; set; }
    }

    private sealed class WakeRun
    {
        public required string Name { get; init; }
        public required string Mac { get; init; }
        public required string Broadcast { get; init; }
        public int Port { get; init; }
        public string? Address { get; init; }
        public string Phase { get; set; } = "sent"; // sent, waiting, up, timeout, failed
        public int Seconds { get; set; }
        public string? Error { get; set; }
    }

    public NetworkToolsWindow(NetworkToolsRequest request)
    {
        _request = request;
        _host = request.Host ?? request.Settings.NetworkToolHosts.FirstOrDefault() ?? string.Empty;
        Title = "Network tools";
        Width = 860;
        Height = 740;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("ntwin");
        this[!BackgroundProperty] = R("NcSurfaceBrush");

        var top = new Border { Padding = new Thickness(20, 16, 20, 12), BorderThickness = new Thickness(0, 0, 0, 1) };
        top[!Border.BorderBrushProperty] = R("NcMwStrokeBrush");
        top.Child = new StackPanel { Children = { _tabs, _form } };
        _bodyScroll = new ScrollViewer { Content = _body, Padding = new Thickness(20, 14, 20, 18) };
        var footer = new Border { Padding = new Thickness(20, 9), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer[!Border.BorderBrushProperty] = R("NcMwStrokeBrush");
        footer[!Border.BackgroundProperty] = R("FmDriveBarBrush");
        var keys = new TextBlock { Text = "Enter starts · Esc stops", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        Faint(keys, "NcMutedBrush");
        Faint(_footerHint, "NcMutedBrush");
        var footerRow = new DockPanel();
        DockPanel.SetDock(keys, Dock.Right);
        footerRow.Children.AddRange([keys, _footerHint]);
        footer.Child = footerRow;

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.AddRange([top, footer, _bodyScroll]);
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closed += (_, _) => _run?.Cancel();
        Render();
        Opened += (_, _) => FocusFirstField();
    }

    /// <summary>The window with a computer filled in (tests, the program before its settings were passed).</summary>
    public NetworkToolsWindow(string? host = null)
        : this(new NetworkToolsRequest(host, new AppSettings(), () => { }))
    {
    }

    // ---------------------------------------------------------------- helpers

    private static DynamicResourceExtension R(string key) => new(key);

    private static string T(string text) => Localizer.T(text);

    /// <summary>A translated pattern with its {0}, {1} ... filled in.</summary>
    private static string F(string pattern, params object[] values)
    {
        var text = Localizer.T(pattern);
        for (var i = 0; i < values.Length; i++)
        {
            text = text.Replace("{" + i.ToString(CultureInfo.InvariantCulture) + "}", Convert.ToString(values[i], CultureInfo.CurrentCulture), StringComparison.Ordinal);
        }
        return text;
    }

    private static void Faint(TextBlock text, string brush) => text[!TextBlock.ForegroundProperty] = R(brush);

    /// <summary>A stroked template icon, drawn in the text color around it (or <paramref name="brush"/>).</summary>
    private static Control Glyph(string data, double size = 16, string? brush = null)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 2.1,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };
        if (brush is not null)
        {
            path[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = R(brush);
        }
        else
        {
            path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding(nameof(Avalonia.Controls.Presenters.ContentPresenter.Foreground))
            {
                RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Avalonia.Controls.Presenters.ContentPresenter) },
            });
        }
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }

    private static Border Card(Control child, Thickness? padding = null)
    {
        var card = new Border { Child = child, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(12, 8) };
        card[!Border.BackgroundProperty] = R("NcMwCardBrush");
        card[!Border.BorderBrushProperty] = R("NcMwStrokeBrush");
        return card;
    }

    private static Button IconButton(string text, string icon, Action click, bool small = true)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Glyph(icon, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        var button = new Button { Content = content, Padding = new Thickness(10, 0), Height = small ? 28 : 34, FontSize = small ? 12.5 : 13, VerticalContentAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }

    private static Control Field(string label, Control input, string? note = null, bool error = false)
    {
        var stack = new StackPanel { Spacing = 5 };
        var caption = new TextBlock { Text = label, FontSize = 12.5 };
        Faint(caption, "NcMutedBrush");
        stack.Children.AddRange([caption, input]);
        var line = new TextBlock { Text = note ?? string.Empty, FontSize = 11.5, MinHeight = 16, TextWrapping = TextWrapping.Wrap };
        Faint(line, error ? "NcBadBrush" : "NcFaintBrush");
        stack.Children.Add(line);
        return stack;
    }

    private Button Chip(string text, bool on, Action click, string? count = null, string? tip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        if (count is not null)
        {
            var small = new TextBlock { Text = count, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            Faint(small, "NcFaintBrush");
            content.Children.Add(small);
        }
        var chip = new Button { Content = content, Classes = { "ntchip" } };
        chip.Classes.Set("on", on);
        if (tip is not null)
        {
            ToolTip.SetTip(chip, tip);
        }
        chip.Click += (_, _) => click();
        return chip;
    }

    private Button StartButton(string text, string icon, bool enabled)
    {
        if (_run is not null)
        {
            var stop = IconButton(T("Stop"), IcStop, Stop, small: false);
            stop.Classes.Add("ntstop");
            stop.MinWidth = 110;
            return stop;
        }
        var start = IconButton(text, icon, () => _ = StartAsync(), small: false);
        start.Classes.Add("accent");
        start.MinWidth = 110;
        start.IsEnabled = enabled;
        return start;
    }

    private static Grid Columns(string widths, params Control[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), ColumnSpacing = 12 };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            cells[i].VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    // ---------------------------------------------------------------- render

    private void Render()
    {
        RenderTabs();
        RenderForm();
        RenderBody();
        _footerHint.Text = T(_page switch
        {
            0 => "Uses ICMP echo, like the ping command.",
            1 => "TCP connect scan; nothing is sent beyond opening a connection.",
            _ => "Sends a UDP magic packet on the local network.",
        });
    }

    private void RenderTabs()
    {
        _tabs.Children.Clear();
        foreach (var (index, icon, text) in new[] { (0, IcPing, "Ping"), (1, IcScan, "Port scan"), (2, IcWake, "Wake-on-LAN") })
        {
            var tab = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Glyph(icon), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } },
                Classes = { "nttab" },
            };
            tab.Classes.Set("on", index == _page);
            ToolTip.SetTip(tab, $"Ctrl+{index + 1}");
            tab.Click += (_, _) => ShowPage(index);
            _tabs.Children.Add(tab);
        }
    }

    private void RenderForm()
    {
        _form.Content = _page switch
        {
            0 => PingForm(),
            1 => ScanForm(),
            _ => WakeForm(),
        };
    }

    private void RenderBody()
    {
        _body.Content = _page switch
        {
            0 => PingBody(),
            1 => ScanBody(),
            _ => WakeBody(),
        };
    }

    /// <summary>The computer box and the recent computers below it.</summary>
    private Control HostField()
    {
        var bad = _host.Length > 0 && !NetworkTools.IsHostName(_host);
        var box = new TextBox { Text = _host, PlaceholderText = "Host name or IP address, e.g. nas or 192.168.1.20", Classes = { "noloc", "nthost" } };
        box.Classes.Set("error", bad);
        box.TextChanged += (_, _) =>
        {
            _host = (box.Text ?? string.Empty).Trim();
            // the button and the note follow the text without rebuilding the box (the caret stays)
            UpdateHostState(box);
        };
        var recent = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var host in _request.Settings.NetworkToolHosts.Take(MaxRecent))
        {
            var chip = Chip(host, string.Equals(host, _host, StringComparison.OrdinalIgnoreCase), () =>
            {
                _host = host;
                RenderForm();
                FocusFirstField();
            });
            chip.Content = new TextBlock { Text = host, Classes = { "noloc" } };
            recent.Children.Add(chip);
        }
        var stack = new StackPanel { Spacing = 5 };
        var caption = new TextBlock { Text = "Computer", FontSize = 12.5 };
        Faint(caption, "NcMutedBrush");
        var note = new TextBlock { Text = bad ? T("That isn't a valid host name or IP address.") : string.Empty, FontSize = 11.5, Tag = "hostnote" };
        Faint(note, "NcBadBrush");
        note.IsVisible = bad;
        stack.Children.AddRange([caption, box, recent, note]);
        return stack;
    }

    private void UpdateHostState(TextBox box)
    {
        var bad = _host.Length > 0 && !NetworkTools.IsHostName(_host);
        box.Classes.Set("error", bad);
        if (box.Parent is StackPanel stack && stack.Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "hostnote")) is { } note)
        {
            note.Text = bad ? T("That isn't a valid host name or IP address.") : string.Empty;
            note.IsVisible = bad;
        }
        if (_form.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("accent")) is { } start)
        {
            start.IsEnabled = CanStart();
        }
    }

    private bool CanStart() => _page switch
    {
        0 => NetworkTools.IsHostName(_host),
        1 => NetworkTools.IsHostName(_host) && CurrentPorts(out var bad).Count > 0 && bad is null,
        _ => NetworkTools.ParseMac(_mac) is not null,
    };

    // ---------------------------------------------------------------- ping

    private Control PingForm()
    {
        var count = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        (int Value, string Text)[] counts = [(4, "4 times"), (10, "10 times"), (30, "30 times"), (0, "Until stopped")];
        foreach (var (_, text) in counts)
        {
            count.Items.Add(new ComboBoxItem { Content = text });
        }
        count.SelectedIndex = Math.Max(0, Array.FindIndex(counts, c => c.Value == _pingCount));
        count.SelectionChanged += (_, _) => _pingCount = counts[Math.Max(0, count.SelectedIndex)].Value;
        return Columns("*,190,Auto", HostField(), Field("Pings", count), Labelless(StartButton(T("Ping"), IcPlay, CanStart())));
    }

    /// <summary>A button lined up with the fields beside it (below their captions).</summary>
    private static Control Labelless(Control control)
    {
        control.Margin = new Thickness(0, 22, 0, 0);
        return control;
    }

    private Control PingBody()
    {
        if (_ping is not { } run)
        {
            return Empty(IcPing, "Check whether a computer answers", "Pick a recent computer above or type a name, then choose Ping.");
        }
        var got = run.Replies.Where(r => r.Milliseconds is not null).Select(r => r.Milliseconds!.Value).ToList();
        var sent = run.Replies.Count;
        var loss = sent == 0 ? 0 : (int)Math.Round(100.0 * (sent - got.Count) / sent);
        var average = got.Count > 0 ? (long)Math.Round(got.Average()) : (long?)null;
        var panel = new StackPanel { Spacing = 12 };
        Control? copy = _run is null && sent > 0 ? IconButton(T("Copy results"), IcCopy, () => _ = CopyAsync(PingText(run))) : null;
        if (run.NotFound)
        {
            panel.Children.Add(VerdictRow("bad", IcX, F("{0} wasn't found", run.Host), T("Check the spelling, or whether the name works on this network."), null));
            return panel;
        }
        panel.Children.Add(_run is not null && _ping == run
            ? VerdictRow("run", IcPing, F("Pinging {0}...", run.Host), F("{0} · {1} of {2} sent", run.Address, sent, run.Count > 0 ? run.Count.ToString(CultureInfo.CurrentCulture) : "∞"), null)
            : got.Count == 0 ? VerdictRow("bad", IcX, F("{0} doesn't answer", run.Host), T("It may be off, asleep, or a firewall blocks ping."), copy)
            : loss > 0 ? VerdictRow("warn", IcAlert, F("{0} answers, with {1}% loss", run.Host, loss), T("The connection is unstable."), copy)
            : VerdictRow("ok", IcCheck, F("{0} is reachable", run.Host), F("{0} · average {1} ms", run.Address, average!), copy));

        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8 };
        Control Stat(int column, string label, string value, string? unit, bool bad = false)
        {
            var caption = new TextBlock { Text = label, FontSize = 11.5 };
            Faint(caption, "NcMutedBrush");
            var number = new TextBlock { FontSize = 18, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
            number.Inlines!.Add(new Avalonia.Controls.Documents.Run(value));
            if (unit is not null)
            {
                var small = new Avalonia.Controls.Documents.Run(unit) { FontSize = 12, FontWeight = FontWeight.Normal };
                small[!Avalonia.Controls.Documents.TextElement.ForegroundProperty] = R("NcFaintBrush");
                number.Inlines.Add(small);
            }
            if (bad)
            {
                Faint(number, "NcBadBrush");
            }
            var card = Card(new StackPanel { Children = { caption, number } });
            Grid.SetColumn(card, column);
            stats.Children.Add(card);
            return card;
        }
        Stat(0, T("Replies"), got.Count.ToString(CultureInfo.CurrentCulture), " / " + sent.ToString(CultureInfo.CurrentCulture));
        Stat(1, T("Lost"), loss.ToString(CultureInfo.CurrentCulture) + "%", null, loss > 0);
        Stat(2, T("Average"), average?.ToString(CultureInfo.CurrentCulture) ?? "—", " ms");
        Stat(3, T("Fastest / slowest"), got.Count > 0 ? got.Min().ToString(CultureInfo.CurrentCulture) : "—",
            " / " + (got.Count > 0 ? got.Max().ToString(CultureInfo.CurrentCulture) : "—") + " ms");
        panel.Children.Add(stats);

        // the response times as bars (the last 60), a lost one a full pale red bar
        var max = Math.Max(40, got.Count > 0 ? got.Max() : 0);
        var bars = new UniformGrid { Rows = 1, Height = 70, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var reply in run.Replies.TakeLast(60))
        {
            var bar = new Border { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1.5, 0), MaxWidth = 28, CornerRadius = new CornerRadius(3, 3, 0, 0) };
            if (reply.Milliseconds is { } ms)
            {
                bar.Height = Math.Max(3, 70.0 * ms / max);
                bar[!Border.BackgroundProperty] = R("NcAccentBrush");
                ToolTip.SetTip(bar, F("{0} ms", ms));
            }
            else
            {
                bar.Height = 70;
                bar.Opacity = 0.5;
                bar.CornerRadius = new CornerRadius(3);
                bar[!Border.BackgroundProperty] = R("NcBadBrush");
                ToolTip.SetTip(bar, T("Lost"));
            }
            bars.Children.Add(bar);
        }
        bars.Width = Math.Min(31 * Math.Max(1, bars.Children.Count), 760);
        var chartHead = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var top = new TextBlock { Text = F("{0} ms", max), FontSize = 11.5, Classes = { "noloc" } };
        Faint(top, "NcFaintBrush");
        DockPanel.SetDock(top, Dock.Right);
        var title = new TextBlock { Text = "Response time", FontSize = 11.5 };
        Faint(title, "NcFaintBrush");
        chartHead.Children.AddRange([top, title]);
        panel.Children.Add(Card(new StackPanel { Children = { chartHead, bars } }, new Thickness(12, 10, 12, 6)));

        // the replies, newest first (12)
        var table = new StackPanel();
        table.Children.Add(TableRow(true, "#", T("Reply from"), T("Time"), "TTL", null));
        foreach (var reply in run.Replies.AsEnumerable().Reverse().Take(12))
        {
            table.Children.Add(reply.Milliseconds is { } ms
                ? TableRow(false, reply.Sequence.ToString(CultureInfo.CurrentCulture), reply.Address, ms.ToString(CultureInfo.CurrentCulture) + " ms",
                    reply.Ttl?.ToString(CultureInfo.CurrentCulture) ?? string.Empty, "NcOkBrush")
                : TableRow(false, reply.Sequence.ToString(CultureInfo.CurrentCulture), T("No reply (timed out)"), "—", string.Empty, "NcBadBrush"));
        }
        var tableCard = Card(table, new Thickness(0));
        tableCard.ClipToBounds = true;
        panel.Children.Add(tableCard);
        return panel;
    }

    private static Control TableRow(bool header, string number, string from, string time, string ttl, string? state)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,90,70"), ColumnSpacing = 10 };
        var row = new Border { Child = grid, Padding = new Thickness(12, 6), BorderThickness = new Thickness(0, header ? 0 : 1, 0, 0) };
        row[!Border.BorderBrushProperty] = R("NcMwStrokeBrush");
        if (header)
        {
            row[!Border.BackgroundProperty] = R("NcMwHoverBrush");
        }
        var cells = new[] { number, from, time, ttl };
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = new TextBlock { Text = cells[i], FontSize = header ? 11.5 : 12.5, Classes = { "noloc" }, TextTrimming = TextTrimming.CharacterEllipsis };
            if (header)
            {
                Faint(cell, "NcMutedBrush");
            }
            else if (state is not null && (i == 2 || (i == 1 && state == "NcBadBrush")))
            {
                Faint(cell, state);
            }
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return row;
    }

    private static string PingText(PingRun run) => string.Join(Environment.NewLine, run.Replies.Select(r => r.Milliseconds is { } ms
        ? $"{r.Sequence}  {r.Address}  {ms} ms  TTL {r.Ttl}"
        : $"{r.Sequence}  {r.Status}"));

    /// <summary>Pings the computer as many times as chosen (one a second; tests give a shorter interval).</summary>
    public async Task PingAsync(TimeSpan? interval = null)
    {
        var host = _host.Trim();
        if (!NetworkTools.IsHostName(host) || _run is not null)
        {
            return;
        }
        Remember(host);
        var run = _ping = new PingRun { Host = host, Count = _pingCount };
        _run = new CancellationTokenSource();
        var token = _run.Token;
        Render();
        try
        {
            run.Address = (await NetworkTools.ResolveAsync(host, token)).ToString();
            for (var i = 1; run.Count == 0 || i <= run.Count; i++)
            {
                var reply = await NetworkTools.PingAsync(run.Address, i, TimeSpan.FromSeconds(2), token);
                run.Replies.Add(reply);
                RenderBody();
                if (run.Count != 0 && i >= run.Count)
                {
                    break;
                }
                await Task.Delay(interval ?? TimeSpan.FromSeconds(1), token);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop
        }
        catch (IOException)
        {
            run.NotFound = true;
        }
        _run = null;
        Render();
    }

    /// <summary>The ping fields (tests).</summary>
    public void SetPing(string host, int count)
    {
        _host = host;
        _pingCount = count;
        Render();
    }

    public IReadOnlyList<PingReply> PingReplies => _ping?.Replies ?? [];

    /// <summary>The headline of the results (tests).</summary>
    public string Verdict => _body.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "verdict"))?.Text ?? string.Empty;

    // ---------------------------------------------------------------- port scan

    private IReadOnlyList<int> CurrentPorts(out string? bad)
    {
        bad = null;
        if (_preset == "custom")
        {
            return NetworkTools.ParsePorts(_customPorts, out bad);
        }
        if (_preset == "low")
        {
            return [.. Enumerable.Range(1, 1024)];
        }
        return s_presets.FirstOrDefault(p => p.Key == _preset).Ports ?? NetworkTools.CommonPorts;
    }

    private Control ScanForm()
    {
        var wait = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        (int Ms, string Text)[] waits = [(300, "0.3 s (local network)"), (800, "0.8 s"), (2000, "2 s (slow / far)")];
        foreach (var (_, text) in waits)
        {
            wait.Items.Add(new ComboBoxItem { Content = text });
        }
        wait.SelectedIndex = Math.Max(0, Array.FindIndex(waits, w => w.Ms == _timeoutMs));
        wait.SelectionChanged += (_, _) => _timeoutMs = waits[Math.Max(0, wait.SelectedIndex)].Ms;
        var top = Columns("*,190,Auto", HostField(), Field("Wait per port", wait), Labelless(StartButton(T("Scan"), IcScan, CanStart())));

        var chips = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (key, name, list) in s_presets)
        {
            chips.Children.Add(Chip(name, _preset == key, () => SetPreset(key), list.Length.ToString(CultureInfo.CurrentCulture),
                string.Join(", ", list)));
        }
        chips.Children.Add(Chip("1–1024", _preset == "low", () => SetPreset("low"), "1024"));
        chips.Children.Add(Chip("Custom...", _preset == "custom", () => SetPreset("custom")));
        var block = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        var caption = new TextBlock { Text = "Ports", FontSize = 12.5 };
        Faint(caption, "NcMutedBrush");
        block.Children.AddRange([caption, chips]);
        var note = new TextBlock { FontSize = 11.5, MinHeight = 16, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
        void ShowNote()
        {
            var list = CurrentPorts(out var wrong);
            note.Text = wrong is not null ? F("\"{0}\" isn't a port or range (1–65535).", wrong)
                : list.Count == 1 ? T("1 port") : F("{0} ports", list.Count)
                    + (_preset is not ("custom" or "low") ? " · " + string.Join(", ", list.Take(8)) + (list.Count > 8 ? "…" : string.Empty) : string.Empty);
            Faint(note, wrong is not null ? "NcBadBrush" : "NcFaintBrush");
        }
        if (_preset == "custom")
        {
            var custom = new TextBox { Text = _customPorts, PlaceholderText = "22, 80, 443, 8000-8100", Classes = { "noloc", "ntmono" } };
            custom.TextChanged += (_, _) =>
            {
                _customPorts = custom.Text ?? string.Empty;
                ShowNote();
                if (_form.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("accent")) is { } start)
                {
                    start.IsEnabled = CanStart();
                }
            };
            block.Children.Add(custom);
        }
        ShowNote();
        block.Children.Add(note);
        return new StackPanel { Spacing = 4, Children = { top, block } };
    }

    private void SetPreset(string key)
    {
        _preset = key;
        RenderForm();
        if (key == "custom")
        {
            Dispatcher.UIThread.Post(() => _form.GetLogicalDescendants().OfType<TextBox>().LastOrDefault()?.Focus());
        }
    }

    private Control ScanBody()
    {
        if (_scan is not { } run)
        {
            return Empty(IcScan, "See which services a computer offers", "Choose a computer and a set of ports. Open ports show what you can connect to.");
        }
        var panel = new StackPanel { Spacing = 0 };
        if (run.Error is { } error)
        {
            panel.Children.Add(VerdictRow("bad", IcX, F("{0} wasn't found", run.Host), error, null));
            return panel;
        }
        var open = run.Results.Where(r => r.State == PortState.Open).OrderBy(r => r.Port).ToList();
        var filtered = run.Results.Where(r => r.State == PortState.Filtered).OrderBy(r => r.Port).ToList();
        var closed = run.Results.Where(r => r.State == PortState.Closed).OrderBy(r => r.Port).ToList();
        Control? copy = _run is null ? IconButton(T("Copy"), IcCopy, () => _ = CopyAsync(string.Join(Environment.NewLine,
            open.Select(p => $"{p.Port}  {(p.Service.Length > 0 ? p.Service : "?")}  {p.Milliseconds} ms")))) : null;
        panel.Children.Add(_run is not null && _scan == run
            ? VerdictRow("run", IcScan, F("Scanning {0}...", run.Host), F("{0} of {1} ports checked", run.Results.Count, run.Total), null)
            : open.Count > 0 ? VerdictRow("ok", IcCheck, open.Count == 1 ? F("1 open port on {0}", run.Host) : F("{0} open ports on {1}", open.Count, run.Host),
                F("{0} checked · {1} filtered · {2} closed", run.Total, filtered.Count, closed.Count), copy)
            : VerdictRow("warn", IcAlert, F("No open ports on {0}", run.Host), F("{0} checked. A firewall may hide them.", run.Total), copy));
        var progress = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, run.Total), Value = run.Results.Count, Height = 4, MinHeight = 4, Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(progress);
        if (open.Count > 0)
        {
            panel.Children.Add(Section(T("Open"), null));
            panel.Children.Add(Ports(open, run.Host));
        }
        if (filtered.Count + closed.Count > 0)
        {
            var toggle = new Button { Content = _showClosed ? T("Hide") : F("Show {0}", filtered.Count + closed.Count), Classes = { "ntghost" }, Height = 28, Padding = new Thickness(10, 0) };
            toggle.Click += (_, _) =>
            {
                _showClosed = !_showClosed;
                RenderBody();
            };
            panel.Children.Add(Section(T("Not reachable"), toggle));
            if (_showClosed)
            {
                panel.Children.Add(Ports([.. filtered, .. closed], run.Host));
            }
        }
        var legal = new TextBlock { Text = "Scan only computers you manage or have permission to test.", FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0) };
        Faint(legal, "NcFaintBrush");
        panel.Children.Add(legal);
        return panel;
    }

    private static Control Section(string title, Control? right)
    {
        var row = new DockPanel { Margin = new Thickness(2, 14, 2, 8) };
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
        }
        var text = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Faint(text, "NcMutedBrush");
        row.Children.Add(text);
        return row;
    }

    private Control Ports(IEnumerable<PortResult> ports, string host)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, ItemWidth = 254 };
        foreach (var port in ports)
        {
            var number = new TextBlock { Text = port.Port.ToString(CultureInfo.InvariantCulture), FontSize = 14, FontWeight = FontWeight.SemiBold, MinWidth = 46,
                VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc", "ntmono" } };
            Faint(number, port.State switch { PortState.Open => "NcOkBrush", PortState.Filtered => "NcWarnBrush", _ => "NcFaintBrush" });
            var name = new TextBlock { Text = port.Service.Length > 0 ? port.Service : T("Unknown service"), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var state = new TextBlock
            {
                Text = port.State switch
                {
                    PortState.Open => F("Open · {0} ms", port.Milliseconds),
                    PortState.Filtered => T("No answer (filtered)"),
                    _ => T("Closed"),
                },
                FontSize = 11.5,
            };
            Faint(state, "NcFaintBrush");
            var row = new DockPanel();
            if (port.State == PortState.Open && PortAction(port.Port, host) is { } action)
            {
                DockPanel.SetDock(action, Dock.Right);
                action.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(action);
            }
            DockPanel.SetDock(number, Dock.Left);
            row.Children.AddRange([number, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), Children = { name, state } }]);
            var card = Card(row, new Thickness(12, 10));
            if (port.State == PortState.Open)
            {
                card[!Border.BorderBrushProperty] = R("NcOkBrush");
            }
            else if (port.State == PortState.Closed)
            {
                card.Opacity = 0.6;
            }
            wrap.Children.Add(card);
        }
        return wrap;
    }

    /// <summary>Open (a web page in the browser) or Connect (the program's own connection to that service) for an open port.</summary>
    private Button? PortAction(int port, string host)
    {
        var target = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        if (port is 80 or 8080 or 443 or 8443 && _request.OpenUrl is { } openUrl)
        {
            var url = $"{(port is 443 or 8443 ? "https" : "http")}://{target}:{port}";
            return IconButton(T("Open"), IcLink, () => openUrl(url));
        }
        var scheme = port switch { 21 => "ftp", 22 => "sftp", 23 => "telnet", 445 => "smb", 3389 => "rdp", 5900 => "vnc", _ => null };
        if (scheme is not null && _request.OpenAddress is { } openAddress)
        {
            var address = $"{scheme}://{target}/";
            return IconButton(T("Connect"), IcPlug, () => _ = openAddress(address));
        }
        return null;
    }

    /// <summary>Scans the chosen ports; every port shows as it is done.</summary>
    public async Task ScanAsync()
    {
        var host = _host.Trim();
        var ports = CurrentPorts(out var bad);
        if (!NetworkTools.IsHostName(host) || bad is not null || ports.Count == 0 || _run is not null)
        {
            return;
        }
        Remember(host);
        var run = _scan = new ScanRun { Host = host, Total = ports.Count };
        _showClosed = false;
        _run = new CancellationTokenSource();
        Render();
        var pending = 0;
        try
        {
            await NetworkTools.ScanPortsAsync(host, ports, TimeSpan.FromMilliseconds(_timeoutMs), 64, result =>
            {
                lock (run.Results)
                {
                    run.Results.Add(result);
                }
                // a redraw at most every few ports
                if (Interlocked.Exchange(ref pending, 1) == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Interlocked.Exchange(ref pending, 0);
                        if (_page == 1)
                        {
                            RenderBody();
                        }
                    }, DispatcherPriority.Background);
                }
            }, _run.Token);
        }
        catch (OperationCanceledException)
        {
            // Stop
        }
        catch (IOException ex)
        {
            run.Error = ex.Message;
        }
        _run = null;
        Render();
    }

    /// <summary>The scan fields (tests): a host, a custom port list, the wait per port.</summary>
    public void SetScan(string host, string ports, int timeoutMs)
    {
        _host = host;
        _preset = "custom";
        _customPorts = ports;
        _timeoutMs = timeoutMs;
        Render();
    }

    public IReadOnlyList<PortResult> ScanResults => _scan?.Results is { } results ? [.. results.OrderBy(r => r.Port)] : [];

    /// <summary>The note under the ports (tests): their count, or what is wrong.</summary>
    public string PortsNote => _form.GetLogicalDescendants().OfType<TextBlock>().LastOrDefault()?.Text ?? string.Empty;

    // ---------------------------------------------------------------- Wake-on-LAN

    private Control WakeForm()
    {
        var panel = new StackPanel { Spacing = 12 };
        var devices = _request.Settings.WakeDevices;
        if (devices.Count > 0)
        {
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, ItemWidth = 230 };
            for (var i = 0; i < devices.Count; i++)
            {
                wrap.Children.Add(DeviceCard(devices[i], i));
            }
            var caption = new TextBlock { Text = "Saved computers", FontSize = 12.5 };
            Faint(caption, "NcMutedBrush");
            panel.Children.Add(new StackPanel { Spacing = 5, Children = { caption, wrap } });
            CheckOnline();
        }

        var macOk = NetworkTools.ParseMac(_mac) is not null;
        var mac = new TextBox { Text = _mac, PlaceholderText = "A4:BB:6D:12:3F:90", Classes = { "noloc", "ntmono" } };
        mac.Classes.Set("error", _mac.Length > 0 && !macOk);
        var macNote = new TextBlock { FontSize = 11.5, MinHeight = 16 };
        void ShowMacNote()
        {
            var ok = NetworkTools.ParseMac(_mac) is not null;
            macNote.Text = T(_mac.Length > 0 && !ok ? "12 hexadecimal digits, e.g. A4:BB:6D:12:3F:90" : "Any format works: dashes, dots or none.");
            Faint(macNote, _mac.Length > 0 && !ok ? "NcBadBrush" : "NcFaintBrush");
            mac.Classes.Set("error", _mac.Length > 0 && !ok);
        }
        ShowMacNote();
        mac.TextChanged += (_, _) =>
        {
            _mac = mac.Text ?? string.Empty;
            _device = null;
            ShowMacNote();
            if (_form.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("accent")) is { } start)
            {
                start.IsEnabled = CanStart();
            }
        };
        // written the usual way when the box is left (00:11:22:AA:BB:CC)
        mac.LostFocus += (_, _) =>
        {
            if (NetworkTools.ParseMac(_mac) is not null)
            {
                mac.Text = _mac = NetworkTools.FormatMac(_mac);
            }
        };
        var macLabel = new TextBlock { Text = "MAC address of the network card", FontSize = 12.5 };
        Faint(macLabel, "NcMutedBrush");
        var macField = new StackPanel { Spacing = 5, Children = { macLabel, mac, macNote } };

        var send = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var targets = NetworkTools.BroadcastTargets().ToList();
        if (targets.All(t => t.Address != _broadcast))
        {
            targets.Add(new(_broadcast, string.Empty));
        }
        foreach (var target in targets)
        {
            send.Items.Add(new ComboBoxItem
            {
                Content = target.Address == "255.255.255.255" ? T(target.Description)
                    : target.Description.Length > 0 ? $"{target.Address} ({target.Description})" : target.Address,
            });
        }
        send.SelectedIndex = Math.Max(0, targets.FindIndex(t => t.Address == _broadcast));
        send.SelectionChanged += (_, _) => _broadcast = targets[Math.Max(0, send.SelectedIndex)].Address;

        var ports = new StackPanel { Orientation = Orientation.Horizontal, Classes = { "ntseg" } };
        foreach (var value in new[] { 7, 9 })
        {
            var button = new Button { Content = value.ToString(CultureInfo.InvariantCulture), Classes = { "noloc" } };
            button.Classes.Set("on", _wakePort == value);
            button.Click += (_, _) =>
            {
                _wakePort = value;
                RenderForm();
            };
            ports.Children.Add(button);
        }
        var segment = new Border { Child = ports, Classes = { "ntsegframe" }, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Columns("1.3*,*,110,Auto", macField, Field("Send to", send, T("Broadcast address of its network.")),
            Field("Port", segment, T("Usually 9.")), Labelless(StartButton(T("Wake up"), IcWake, macOk))));
        return panel;
    }

    private Control DeviceCard(WakeDevice device, int index)
    {
        var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Child = Glyph(IcPc, 16, "NcMutedBrush") };
        icon[!Border.BackgroundProperty] = R("NcMwHoverBrush");
        var name = new TextBlock { Text = device.Name, FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        var mac = new TextBlock { Text = NetworkTools.FormatMac(device.Mac), FontSize = 11.5, Classes = { "noloc", "ntmono" } };
        Faint(mac, "NcFaintBrush");
        var up = device.Address is { Length: > 0 } address && _online.TryGetValue(address, out var on) && on;
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        dot[!Border.BackgroundProperty] = R(up ? "NcOkBrush" : "NcFaintBrush");
        ToolTip.SetTip(dot, T(up ? "Online" : "Offline or unknown"));
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(dot, Dock.Right);
        row.Children.AddRange([icon, dot, new StackPanel { Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { name, mac } }]);
        var card = new Button { Content = row, Classes = { "ntdevice" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        card.Classes.Set("on", _device == index);
        ToolTip.SetTip(card, string.IsNullOrEmpty(device.Address) ? device.Broadcast : $"{device.Address} · {device.Broadcast}");
        card.Click += (_, _) =>
        {
            _device = index;
            _mac = NetworkTools.FormatMac(device.Mac);
            _broadcast = device.Broadcast;
            _wakePort = device.Port;
            RenderForm();
        };
        // rename and forget from its menu
        var rename = new MenuItem { Header = "Rename...", Icon = Services.MenuIcons.Create("nc_RenameOnly") };
        rename.Click += (_, _) => RenameDevice(card, device);
        var forget = new MenuItem { Header = "Forget", Icon = Services.MenuIcons.Create("nc_Delete") };
        forget.Click += (_, _) =>
        {
            _request.Settings.WakeDevices.Remove(device);
            _request.Save();
            _device = null;
            RenderForm();
        };
        card.ContextMenu = new ContextMenu { Items = { rename, forget } };
        return card;
    }

    private void RenameDevice(Control anchor, WakeDevice device)
    {
        var box = new TextBox { Text = device.Name, Width = 220, Classes = { "noloc" } };
        var flyout = new Flyout { Content = box, Placement = PlacementMode.Bottom };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(box.Text))
            {
                device.Name = box.Text.Trim();
                _request.Save();
                flyout.Hide();
                RenderForm();
                e.Handled = true;
            }
        };
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>The saved computers with an address are pinged once (the dot on their card).</summary>
    private void CheckOnline()
    {
        foreach (var address in _request.Settings.WakeDevices.Select(d => d.Address).OfType<string>().Where(a => a.Length > 0 && !_online.ContainsKey(a)).Distinct())
        {
            _online[address] = false;
            _ = Task.Run(async () =>
            {
                var reply = await NetworkTools.PingAsync(address, 1, TimeSpan.FromSeconds(1));
                return reply.Milliseconds is not null;
            }).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result)
                {
                    _online[address] = true;
                    if (_page == 2 && _run is null)
                    {
                        RenderForm();
                    }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private Control WakeBody()
    {
        if (_wake is not { } run)
        {
            return Empty(IcWake, "Wake a computer over the network",
                "Choose a saved computer or enter its MAC address. Wake-on-LAN must be turned on in the computer's BIOS and network card settings.");
        }
        var panel = new StackPanel { Spacing = 8 };
        if (run.Error is { } error)
        {
            panel.Children.Add(VerdictRow("bad", IcX, F("The magic packet could not be sent to {0}", run.Name), error, null));
            return panel;
        }
        panel.Children.Add(run.Phase switch
        {
            "up" => VerdictRow("ok", IcCheck, F("{0} is awake", run.Name), F("Answered after {0} s", run.Seconds), null),
            "timeout" => VerdictRow("warn", IcAlert, F("{0} didn't answer yet", run.Name), T("It may still be starting, or Wake-on-LAN is off."), null),
            "sent" => VerdictRow("ok", IcCheck, F("Magic packet sent to {0}", run.Name), $"{run.Mac} → {run.Broadcast}:{run.Port}", null),
            _ => VerdictRow("run", IcWake, F("Waking {0}...", run.Name), $"{run.Mac} → {run.Broadcast}:{run.Port}", null),
        });
        panel.Children.Add(Step("done", F("Magic packet sent to {0}", run.Mac)));
        panel.Children.Add(run.Address is { Length: > 0 } address
            ? run.Phase switch
            {
                "up" => Step("done", F("Answering at {0}", address)),
                "timeout" => Step("bad", F("No answer from {0} after 60 s", address)),
                _ => Step("run", F("Waiting for {0} to answer... {1} s", address, run.Seconds)),
            }
            : Step("done", T("No IP address saved, so it can't be checked automatically.")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        if (run.Phase == "timeout")
        {
            buttons.Children.Add(IconButton(T("Send again"), IcWake, () => _ = StartAsync()));
            var why = new Button { Content = "Why might it not wake?", Classes = { "ntghost" }, Height = 28, Padding = new Thickness(10, 0) };
            ToolTip.SetTip(why, T("Check: Wake-on-LAN in the BIOS, \"Wake on magic packet\" in the network card, Fast Startup off, the cable connected."));
            why.Click += (_, _) => ToolTip.SetIsOpen(why, true);
            buttons.Children.Add(why);
        }
        if (!_request.Settings.WakeDevices.Any(d => string.Equals(NetworkTools.FormatMac(d.Mac), run.Mac, StringComparison.OrdinalIgnoreCase)))
        {
            buttons.Children.Add(IconButton(T("Save this computer"), IcPlus, SaveWakeDevice));
        }
        if (buttons.Children.Count > 0)
        {
            panel.Children.Add(buttons);
        }
        return panel;
    }

    private static Control Step(string state, string text)
    {
        var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11) };
        dot[!Border.BackgroundProperty] = R(state switch { "done" => "NcOkSoftBrush", "bad" => "NcBadSoftBrush", "run" => "NcAccentSoftBrush", _ => "NcMwHoverBrush" });
        dot.Child = state switch
        {
            "done" => Glyph(IcCheck, 12, "NcOkBrush"),
            "bad" => Glyph(IcX, 12, "NcBadBrush"),
            "run" => Glyph(IcWake, 12, "NcAccentBrush"),
            _ => null,
        };
        if (dot.Child is { } child)
        {
            child.HorizontalAlignment = HorizontalAlignment.Center;
            child.VerticalAlignment = VerticalAlignment.Center;
        }
        var line = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { dot, line } };
    }

    /// <summary>Sends the magic packet; when the computer's address is known, waits up to a minute for it to answer.</summary>
    public async Task WakeAsync(TimeSpan? interval = null)
    {
        if (NetworkTools.ParseMac(_mac) is not { } bytes || _run is not null)
        {
            return;
        }
        var device = _device is { } index && index < _request.Settings.WakeDevices.Count ? _request.Settings.WakeDevices[index] : null;
        var mac = NetworkTools.FormatMac(_mac);
        var run = _wake = new WakeRun
        {
            Name = device?.Name ?? mac,
            Mac = mac,
            Broadcast = _broadcast,
            Port = _wakePort,
            Address = device?.Address,
        };
        try
        {
            await NetworkTools.WakeAsync(bytes, _broadcast, _wakePort);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            run.Error = ex.Message;
            Render();
            return;
        }
        if (run.Address is not { Length: > 0 } address)
        {
            Render();
            return;
        }
        run.Phase = "waiting";
        _run = new CancellationTokenSource();
        var token = _run.Token;
        Render();
        try
        {
            while (run.Seconds < 60)
            {
                var reply = await NetworkTools.PingAsync(address, run.Seconds + 1, TimeSpan.FromSeconds(1), token);
                if (reply.Milliseconds is not null)
                {
                    run.Phase = "up";
                    _online[address] = true;
                    break;
                }
                await Task.Delay(interval ?? TimeSpan.FromSeconds(1), token);
                run.Seconds++;
                RenderBody();
            }
            if (run.Phase != "up")
            {
                run.Phase = "timeout";
            }
        }
        catch (OperationCanceledException)
        {
            run.Phase = "timeout";
        }
        _run = null;
        Render();
    }

    /// <summary>The computer just woken goes to the saved ones ("Computer 3"; rename it from its menu).</summary>
    public void SaveWakeDevice()
    {
        if (NetworkTools.ParseMac(_mac) is null)
        {
            return;
        }
        var devices = _request.Settings.WakeDevices;
        devices.Add(new WakeDevice
        {
            Name = F("Computer {0}", devices.Count + 1),
            Mac = NetworkTools.FormatMac(_mac),
            Broadcast = _broadcast,
            Port = _wakePort,
            Address = NetworkTools.IsHostName(_host) ? _host : null,
        });
        _device = devices.Count - 1;
        _request.Save();
        Render();
    }

    /// <summary>The Wake-on-LAN fields (tests).</summary>
    public void SetWake(string mac, string broadcast, int port)
    {
        _mac = mac;
        _broadcast = broadcast;
        _wakePort = port;
        _device = null;
        Render();
    }

    // ---------------------------------------------------------------- shared

    private static Control Empty(string icon, string title, string text)
    {
        var art = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Center,
            Child = Glyph(icon, 28, "NcFaintBrush") };
        art[!Border.BackgroundProperty] = R("NcMwHoverBrush");
        if (art.Child is { } child)
        {
            child.HorizontalAlignment = HorizontalAlignment.Center;
            child.VerticalAlignment = VerticalAlignment.Center;
        }
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        var line = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460 };
        Faint(line, "NcMutedBrush");
        return new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 70, 0, 0),
            Children = { art, heading, line } };
    }

    /// <summary>The headline of the results: a tinted icon, a title and a line under it, an action at the right.</summary>
    private static Control VerdictRow(string kind, string icon, string title, string text, Control? right)
    {
        var (back, fore) = kind switch
        {
            "ok" => ("NcOkSoftBrush", "NcOkBrush"),
            "bad" => ("NcBadSoftBrush", "NcBadBrush"),
            "warn" => ("NcWarnSoftBrush", "NcWarnBrush"),
            _ => ("NcAccentSoftBrush", "NcAccentBrush"),
        };
        var mark = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), Child = Glyph(icon, 18, fore), VerticalAlignment = VerticalAlignment.Center };
        mark[!Border.BackgroundProperty] = R(back);
        if (mark.Child is { } child)
        {
            child.HorizontalAlignment = HorizontalAlignment.Center;
            child.VerticalAlignment = VerticalAlignment.Center;
        }
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Tag = "verdict", Classes = { "noloc" }, TextWrapping = TextWrapping.Wrap };
        var line = new TextBlock { Text = text, FontSize = 12.5, Classes = { "noloc" }, TextWrapping = TextWrapping.Wrap };
        Faint(line, "NcMutedBrush");
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            right.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(right);
        }
        row.Children.Add(new StackPanel { Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, Children = { heading, line } });
        return row;
    }

    private async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>A computer pinged or scanned goes to the front of the recent ones.</summary>
    private void Remember(string host)
    {
        var recent = _request.Settings.NetworkToolHosts;
        recent.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, host);
        if (recent.Count > MaxRecent)
        {
            recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        }
        _request.Save();
    }

    private Task StartAsync() => _page switch
    {
        0 => PingAsync(),
        1 => ScanAsync(),
        _ => WakeAsync(),
    };

    private void Stop()
    {
        _run?.Cancel();
    }

    /// <summary>Shows a page: 0 ping, 1 port scan, 2 Wake-on-LAN (a ping or a scan that runs stops).</summary>
    public void ShowPage(int index)
    {
        if (index == _page)
        {
            return;
        }
        _run?.Cancel();
        _page = Math.Clamp(index, 0, 2);
        Render();
        FocusFirstField();
    }

    private void FocusFirstField() =>
        Dispatcher.UIThread.Post(() => _form.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(), DispatcherPriority.Input);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3)
        {
            ShowPage(e.Key is Key.D1 or Key.NumPad1 ? 0 : e.Key is Key.D2 or Key.NumPad2 ? 1 : 2);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                if (_run is not null)
                {
                    Stop();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button && _run is null && CanStart():
                if (e.Source is TextBox box && box.Classes.Contains("ntmono") && _page == 2)
                {
                    _mac = box.Text ?? string.Empty;
                }
                _ = StartAsync();
                e.Handled = true;
                break;
        }
    }
}
