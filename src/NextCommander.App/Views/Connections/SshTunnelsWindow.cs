using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;

namespace NextCommander.App.Views.Connections;

/// <summary>
/// SSH tunnels (Net > SSH tunnels...; user's request, 21 November 2026): the saved tunnels
/// as cards with their state (running, starting, stopped, failed with the reason), Start / Stop, Edit, Delete and
/// "Start with NextCommander"; New tunnel opens the editor (<see cref="SshTunnelDialog"/>).
/// </summary>
public sealed class SshTunnelsWindow : Window
{
    private static readonly IBrush s_ok = new SolidColorBrush(Color.Parse("#2E9E44"));
    private static readonly IBrush s_warn = new SolidColorBrush(Color.Parse("#C98A00"));
    private static readonly IBrush s_bad = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush s_dim = new SolidColorBrush(Color.FromArgb(150, 128, 128, 128));

    private readonly AppSettings _settings;
    private readonly TunnelService _tunnels;
    private readonly Action _save;
    private readonly StackPanel _list = new() { Margin = new Thickness(20, 12, 20, 16) };

    public SshTunnelsWindow(AppSettings settings, TunnelService tunnels, Action save)
    {
        _settings = settings;
        _tunnels = tunnels;
        _save = save;
        Title = "SSH tunnels";
        Width = 760;
        Height = 560;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromArgb(28, 76, 141, 255)),
            Child = new Viewbox { Width = 22, Height = 22, Child = MenuIcons.Create("nc_SshTunnels") ?? new Panel() },
        };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.AddRange([
            new TextBlock { Text = "SSH tunnels", FontSize = 17, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "Ports forwarded through a saved SSH connection: reach a service behind a server, let a server reach this computer, or go out through the server (SOCKS).", FontSize = 12.5, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
        ]);
        var add = new Button { Content = "＋ New tunnel", Classes = { "tb", "primary" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        add.Click += async (_, _) => await EditAsync(null);
        var head = new DockPanel { Margin = new Thickness(20, 18, 20, 14) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(add, Dock.Right);
        head.Children.AddRange([icon, add, titles]);
        var headLine = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headLine[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        DockPanel.SetDock(headLine, Dock.Top);
        root.Children.AddRange([headLine, new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }]);
        var background = new Border { Child = root };
        background[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        Content = background;

        void OnChanged(object? sender, EventArgs e) => Refresh();
        tunnels.Changed += OnChanged;
        Closed += (_, _) => tunnels.Changed -= OnChanged;
        Refresh();
    }

    /// <summary>Opens the editor (null: a new tunnel); OK saves it.</summary>
    public async Task EditAsync(SshTunnel? tunnel)
    {
        var dialog = new SshTunnelDialog(_settings, tunnel);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } result)
        {
            return;
        }
        Save(tunnel, result);
    }

    /// <summary>The editor's result into the list (a running tunnel starts again with it); saved.</summary>
    public void Save(SshTunnel? old, SshTunnel edited)
    {
        if (old is null)
        {
            _settings.SshTunnels.Add(edited);
        }
        else
        {
            var running = _tunnels.StateOf(old) is TunnelState.Running or TunnelState.Starting;
            _tunnels.Stop(old);
            _settings.SshTunnels[_settings.SshTunnels.IndexOf(old)] = edited;
            if (running)
            {
                _ = _tunnels.StartAsync(edited);
            }
        }
        _save();
        Refresh();
    }

    public void Delete(SshTunnel tunnel)
    {
        _tunnels.Stop(tunnel);
        _settings.SshTunnels.Remove(tunnel);
        _save();
        Refresh();
    }

    /// <summary>The cards again (a tunnel changed its state, the list changed).</summary>
    public void Refresh()
    {
        _list.Children.Clear();
        if (_settings.SshTunnels.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 50, 0, 0), MaxWidth = 460 };
            empty.Children.AddRange([
                new TextBlock { Text = "No tunnels yet", FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) },
                new TextBlock { Text = "For example: port 5432 of this computer to the database server behind your SSH server, or a SOCKS proxy on port 1080 that goes out through the server.", FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            ]);
            _list.Children.Add(empty);
            return;
        }
        foreach (var tunnel in _settings.SshTunnels)
        {
            _list.Children.Add(Card(tunnel));
        }
    }

    private static Button Small(string text, string? glyph, string tip, Action click, bool primary = false)
    {
        object content = text;
        if (glyph is not null && IconPacks.Glyph(glyph, IconPacks.Current) is { } geometry)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.AddRange([new Viewbox { Width = 13, Height = 13, Child = IconPacks.Draw(geometry, primary ? Brushes.White : Brushes.Gray) }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
            content = row;
        }
        var button = new Button { Content = content, Tag = text, Classes = { "tb" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12.5 };
        if (primary)
        {
            button.Classes.Add("primary");
        }
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private Border Card(SshTunnel tunnel)
    {
        var state = _tunnels.StateOf(tunnel);
        var (word, brush) = state switch
        {
            TunnelState.Running => ("Running", s_ok),
            TunnelState.Starting => ("Starting...", s_warn),
            TunnelState.Failed => ("Failed", s_bad),
            _ => ("Stopped", s_dim),
        };
        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var kind = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)),
            Child = new TextBlock { Text = tunnel.Kind switch { TunnelKind.Remote => "Remote", TunnelKind.Dynamic => "SOCKS", _ => "Local" }, FontSize = 11 },
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.AddRange([dot, new TextBlock { Text = tunnel.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } }, kind,
            new TextBlock { Text = Localizer.T(word), FontSize = 12, Foreground = brush, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }]);
        var route = new TextBlock
        {
            Text = tunnel.Route + "  ·  " + Localizer.T("through {0}").Replace("{0}", tunnel.Connection, StringComparison.Ordinal),
            FontSize = 12, Opacity = 0.75, Margin = new Thickness(17, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" },
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
        };
        var auto = new CheckBox { Content = "Start with NextCommander", IsChecked = tunnel.StartWithApp, FontSize = 12, Margin = new Thickness(17, 4, 0, 0) };
        auto.IsCheckedChanged += (_, _) =>
        {
            tunnel.StartWithApp = auto.IsChecked == true;
            _save();
        };
        var middle = new StackPanel();
        middle.Children.AddRange([top, route]);
        if (state == TunnelState.Failed && _tunnels.ErrorOf(tunnel) is { } error)
        {
            middle.Children.Add(new TextBlock { Text = error, FontSize = 12, Foreground = s_bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(17, 3, 0, 0), Classes = { "noloc" } });
        }
        middle.Children.Add(auto);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(state is TunnelState.Running or TunnelState.Starting
            ? Small("Stop", "MiX", "Stop the tunnel", () => _tunnels.Stop(tunnel))
            : Small("Start", "MiArrowRight", "Start the tunnel", () => _ = _tunnels.StartAsync(tunnel), primary: true));
        buttons.Children.Add(Small("Edit", "MiPencil", "Change the tunnel", () => _ = EditAsync(tunnel)));
        buttons.Children.Add(Small("Delete", "MiTrash", "Delete the tunnel", () => Delete(tunnel)));
        var row2 = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row2.Children.AddRange([buttons, middle]);
        var card = new Border { Child = row2, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 8) };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundChromeMediumLowBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return card;
    }
}

/// <summary>
/// The editor of an SSH tunnel: its name, the kind (three cards: local, remote, SOCKS), the SSH connection it goes
/// through, where it listens and where it leads (captions that say which side), Start with NextCommander.
/// </summary>
public sealed class SshTunnelDialog : Window
{
    private static readonly IBrush s_problem = new SolidColorBrush(Color.Parse("#E5484D"));

    private readonly AppSettings _settings;
    private readonly SshTunnel? _original;
    private readonly SshTunnel _tunnel;
    private readonly List<Border> _kinds = [];
    private readonly TextBox _name = new();
    private readonly ComboBox _connection = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _listenAddress = new() { Classes = { "noloc" } };
    private readonly TextBox _listenPort = new() { Width = 110, Classes = { "noloc" } };
    private readonly TextBox _targetHost = new() { Classes = { "noloc" } };
    private readonly TextBox _targetPort = new() { Width = 110, Classes = { "noloc" } };
    private readonly TextBlock _listenCaption = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 170, Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBlock _targetCaption = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 170, Margin = new Thickness(0, 7, 0, 0) };
    private readonly Grid _targetRow = new() { ColumnDefinitions = new ColumnDefinitions("180,*"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox _auto = new() { Content = "Start with NextCommander" };
    private readonly TextBlock _problems = new() { Foreground = s_problem, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<RemoteConnection> _carriers;

    public SshTunnelDialog(AppSettings settings, SshTunnel? tunnel)
    {
        _settings = settings;
        _original = tunnel;
        _tunnel = tunnel?.Clone() ?? new SshTunnel();
        _carriers = [.. settings.Connections.Where(SshTunnel.CanCarry)];
        if (tunnel is null && _carriers.Count > 0)
        {
            _tunnel.Connection = _carriers[0].Name;
        }
        Title = tunnel is null ? "New SSH tunnel" : "SSH tunnel";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 8) };
        panel.Children.Add(new TextBlock { Text = tunnel is null ? "New SSH tunnel" : "SSH tunnel", FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 14) });

        // the kind: three cards
        var kinds = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
        (TunnelKind Kind, string Title, string Text)[] options =
        [
            (TunnelKind.Local, "Local", "A port of this computer leads to a service behind the server."),
            (TunnelKind.Remote, "Remote", "A port of the server leads to a service of this computer."),
            (TunnelKind.Dynamic, "SOCKS", "A SOCKS proxy on this computer that goes out through the server."),
        ];
        foreach (var (kind, title, text) in options)
        {
            var stack = new StackPanel();
            stack.Children.AddRange([
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13.5 },
                new TextBlock { Text = text, FontSize = 11.5, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) },
            ]);
            var card = new Border { Child = stack, Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Tag = kind, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Background = Brushes.Transparent };
            card.Tapped += (_, _) => SetKind(kind);
            _kinds.Add(card);
            kinds.Children.Add(card);
        }
        panel.Children.Add(kinds);

        Grid Row(string caption, Control input, TextBlock? captionBlock = null)
        {
            var label = captionBlock ?? new TextBlock { Text = caption, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 170, Margin = new Thickness(0, 7, 0, 0) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetColumn(input, 1);
            row.Children.AddRange([label, input]);
            return row;
        }
        Control HostPort(TextBox host, TextBox port)
        {
            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
            var colon = new TextBlock { Text = ":", Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            DockPanel.SetDock(port, Dock.Right);
            DockPanel.SetDock(colon, Dock.Right);
            row.Children.AddRange([port, colon, host]);
            return row;
        }
        _name.Text = _tunnel.Name;
        _name.PlaceholderText = "e.g. Database of the web server";
        Avalonia.Automation.AutomationProperties.SetName(_name, "Tunnel name");
        panel.Children.Add(Row("Name", _name));
        _connection.ItemsSource = _carriers.Select(c => c.Name).ToList();
        _connection.SelectedItem = _carriers.Any(c => c.Name == _tunnel.Connection) ? _tunnel.Connection : null;
        _connection.Classes.Add("noloc");
        panel.Children.Add(Row("Through the connection", _connection));
        if (_carriers.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "There is no saved SSH, SFTP or SCP connection yet: make one in the connection manager first.", FontSize = 12, Foreground = s_problem, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(180, -4, 0, 10) });
        }
        _listenAddress.Text = _tunnel.ListenAddress;
        _listenPort.Text = _tunnel.ListenPort > 0 ? _tunnel.ListenPort.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _listenPort.PlaceholderText = "port";
        Avalonia.Automation.AutomationProperties.SetName(_listenPort, "Listen port");
        panel.Children.Add(Row(string.Empty, HostPort(_listenAddress, _listenPort), _listenCaption));
        _targetHost.Text = _tunnel.TargetHost;
        _targetHost.PlaceholderText = "host";
        _targetPort.Text = _tunnel.TargetPort > 0 ? _tunnel.TargetPort.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _targetPort.PlaceholderText = "port";
        Avalonia.Automation.AutomationProperties.SetName(_targetHost, "Target host");
        var target = HostPort(_targetHost, _targetPort);
        Grid.SetColumn(target, 1);
        _targetRow.Children.AddRange([_targetCaption, target]);
        panel.Children.Add(_targetRow);
        _auto.IsChecked = _tunnel.StartWithApp;
        panel.Children.Add(Row(string.Empty, _auto, new TextBlock()));
        panel.Children.Add(_problems);

        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "tb", "primary" }, MinWidth = 88 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "tb" }, MinWidth = 88 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 8, 22, 16) };
        buttons.Children.AddRange([cancel, ok]);
        var root = new StackPanel();
        root.Children.AddRange([panel, buttons]);
        Content = root;
        SetKind(_tunnel.Kind);
        Opened += (_, _) => _name.Focus();
    }

    /// <summary>The tunnel made (null: cancelled).</summary>
    public SshTunnel? Result { get; private set; }

    /// <summary>The fields for tests: name, connection, listen address and port, target host and port.</summary>
    public void Fill(string name, string? connection, TunnelKind kind, string listenAddress, string listenPort, string targetHost, string targetPort, bool startWithApp)
    {
        _name.Text = name;
        _connection.SelectedItem = connection;
        SetKind(kind);
        _listenAddress.Text = listenAddress;
        _listenPort.Text = listenPort;
        _targetHost.Text = targetHost;
        _targetPort.Text = targetPort;
        _auto.IsChecked = startWithApp;
    }

    public string ProblemText => _problems.Text ?? string.Empty;

    /// <summary>The kind chosen: its card marked, the captions say on which side the ports are.</summary>
    public void SetKind(TunnelKind kind)
    {
        _tunnel.Kind = kind;
        foreach (var card in _kinds)
        {
            var on = Equals(card.Tag, kind);
            if (on)
            {
                card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
                card.Background = new SolidColorBrush(Color.FromArgb(28, 76, 141, 255));
            }
            else
            {
                card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
                card.Background = Brushes.Transparent;
            }
        }
        _listenCaption.Text = Localizer.T(kind switch
        {
            TunnelKind.Remote => "Listen on the server",
            TunnelKind.Dynamic => "SOCKS proxy on this computer",
            _ => "Listen on this computer",
        });
        _targetCaption.Text = Localizer.T(kind == TunnelKind.Remote ? "Lead to (as this computer sees it)" : "Lead to (as the server sees it)");
        _targetRow.IsVisible = kind != TunnelKind.Dynamic;
    }

    private static int Port(string? text) => int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 0;

    /// <summary>OK: the fields into a tunnel; problems are listed and it stays open.</summary>
    public void Accept()
    {
        _tunnel.Name = (_name.Text ?? string.Empty).Trim();
        _tunnel.Connection = _connection.SelectedItem as string ?? string.Empty;
        _tunnel.ListenAddress = (_listenAddress.Text ?? string.Empty).Trim();
        _tunnel.ListenPort = Port(_listenPort.Text);
        _tunnel.TargetHost = (_targetHost.Text ?? string.Empty).Trim();
        _tunnel.TargetPort = Port(_targetPort.Text);
        _tunnel.StartWithApp = _auto.IsChecked == true;
        var others = _settings.SshTunnels.Where(t => !ReferenceEquals(t, _original));
        if (_tunnel.Problems(others, _settings.Connections) is { Count: > 0 } problems)
        {
            _problems.Text = string.Join(Environment.NewLine, problems.Select(p => Localizer.T(p.Text)).Distinct());
            _problems.IsVisible = true;
            return;
        }
        Result = _tunnel;
        Close();
    }
}
