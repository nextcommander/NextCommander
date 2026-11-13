using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NextCommander.App.Services;

namespace NextCommander.App.Views;

/// <summary>
/// Internal terminal at the bottom of the main window, like the terminal panel of Visual Studio Code: tabs with one
/// shell each (<see cref="EmulatedTerminal"/> on a pseudo terminal, or the line console <see cref="TerminalConsole"/>
/// when terminal emulation is off or not available), "+" opens another tab with the default shell, "▾" offers every shell
/// installed (cmd, Windows PowerShell, PowerShell 7, Git Bash, WSL; bash, zsh ... elsewhere). Opened with the terminal
/// button at the end of the command line or nc_ToggleTerminal; the top edge is dragged to change the height.
/// </summary>
public sealed class TerminalView : DockPanel
{
    private readonly List<TerminalTab> _consoles = [];
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly Panel _host = new();
    private TerminalTab? _active;
    private double? _resizeStartY;
    private double _resizeHeight;
    private readonly Button _maximize;

    /// <summary>Height as a part of the window: kept when the window is resized.</summary>
    private double _ratio = 0.45;

    public TerminalView()
    {
        MinHeight = 90;

        // top edge: drag to change the height (positions relative to the window: the terminal itself moves while resized)
        var grip = new Border { Height = 5, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeNorthSouth) };
        grip.PointerPressed += (_, e) =>
        {
            if (IsMaximized || TopLevel.GetTopLevel(this) is not { } top)
            {
                return;
            }
            _resizeStartY = e.GetPosition(top).Y;
            _resizeHeight = Bounds.Height;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_resizeStartY is { } start && TopLevel.GetTopLevel(this) is { } top)
            {
                ResizeTo(_resizeHeight + (start - e.GetPosition(top).Y));
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            _resizeStartY = null;
            e.Pointer.Capture(null);
        };
        grip.PointerCaptureLost += (_, _) => _resizeStartY = null;
        SetDock(grip, Dock.Top);
        Children.Add(grip);

        // header: title, tabs, new tab, actions
        var header = new DockPanel { Margin = new Thickness(8, 0, 4, 2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        buttons.Children.Add(HeaderButton("Panel folder", "Go to the folder of the active panel in the terminal", () => _active?.GoToPanelFolder()));
        buttons.Children.Add(HeaderButton("Clear", "Clear the output (Ctrl+L)", () => _active?.Clear()));
        buttons.Children.Add(HeaderButton("Stop", "Stop the running program and restart the shell (Ctrl+C)", () => _active?.Restart()));
        _maximize = HeaderButton("□", "Maximize the terminal (double click on the title)", () => MaximizeToggleRequested?.Invoke(this, EventArgs.Empty));
        _maximize.Tag = "maximize";
        buttons.Children.Add(_maximize);
        buttons.Children.Add(HeaderButton("✕", "Close the terminal (ends every shell)", () => CloseRequested?.Invoke(this, EventArgs.Empty)));
        SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(new TextBlock { Text = "TERMINAL", FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(_tabStrip);
        var add = HeaderButton("+", "New terminal (default shell)", () => NewTab());
        var choose = HeaderButton("▾", "New terminal with a chosen shell", () => { });
        choose.Click += (_, _) => ShowShellMenu(choose);
        left.Children.Add(add);
        left.Children.Add(choose);
        header.Children.Add(left);
        // a double click on the title bar maximizes or restores, like a window
        header.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Button && (e.Source as Visual)?.FindAncestorOfType<Button>() is null)
            {
                MaximizeToggleRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
        header.Background = Brushes.Transparent;
        SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(_host);
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
    }

    /// <summary>The close button was pressed or the last tab was closed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The maximize / restore button was pressed or the title bar double clicked.</summary>
    public event EventHandler? MaximizeToggleRequested;

    /// <summary>The terminal takes the place of the panels (<see cref="SetMaximized"/>).</summary>
    public bool IsMaximized { get; private set; }

    /// <summary>
    /// Maximized: the terminal is <paramref name="height"/> high (from the top of the panels to the bottom), its top edge
    /// cannot be dragged; null restores the height as a part of the window.
    /// </summary>
    public void SetMaximized(double? height, double windowHeight = 0)
    {
        IsMaximized = height is not null;
        if (height is { } h)
        {
            Height = Math.Max(MinHeight, h);
        }
        else
        {
            FitWindow(windowHeight);
        }
        _maximize.Content = IsMaximized ? "❐" : "□";
        ToolTip.SetTip(_maximize, IsMaximized ? "Restore the terminal size (double click on the title)" : "Maximize the terminal (double click on the title)");
    }

    /// <summary>The height was changed by dragging (<see cref="HeightRatio"/>).</summary>
    public event EventHandler? HeightRatioChanged;

    /// <summary>A tab was opened or closed (the shells of <see cref="Consoles"/>).</summary>
    public event EventHandler? TabsChanged;

    /// <summary>Height as a part of the window (setting).</summary>
    public double HeightRatio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.1, 0.85);
    }

    /// <summary>Shells (names) of the tabs to open when the terminal is shown the first time (setting).</summary>
    public IReadOnlyList<string>? RestoreTabs { get; set; }

    /// <summary>The local folder of the active panel (for a new shell and "Panel folder").</summary>
    public Func<string?>? CurrentFolder { get; set; }

    /// <summary>Default shell (setting; empty: the system's, cmd.exe on Windows).</summary>
    public string? Shell { get; set; }

    /// <summary>New tabs run on a pseudo terminal with a real terminal screen (setting; off: the line console).</summary>
    public bool Emulation { get; set; } = true;

    private (string Family, double Size) _font = (NextCommander.Core.Config.AppSettings.DefaultTerminalFont, 14);

    /// <summary>The font of every tab (Configuration > Font > Terminal).</summary>
    public void SetFont(string family, double size)
    {
        _font = (string.IsNullOrWhiteSpace(family) ? NextCommander.Core.Config.AppSettings.DefaultTerminalFont : family, size);
        foreach (var tab in _consoles)
        {
            tab.SetFont(_font.Family, _font.Size);
        }
    }

    public IReadOnlyList<TerminalTab> Consoles => _consoles;

    public TerminalTab? ActiveConsole => _active;

    // the active tab, for the window and the tests
    public string OutputText => _active?.OutputText ?? string.Empty;

    public int InputStart => (_active as TerminalConsole)?.InputStart ?? 0;

    public string CurrentInput => (_active as TerminalConsole)?.CurrentInput ?? string.Empty;

    public TerminalSession? Session => (_active as TerminalConsole)?.Session;

    public TextBox? Console => (_active as TerminalConsole)?.Console;

    public void Run(string line) => _active?.Run(line);

    internal static string Clean(string text) => TerminalConsole.Clean(text);

    private static Button HeaderButton(string text, string tip, Action action)
    {
        var button = new Button { Content = text, FontSize = 11, Padding = new Thickness(8, 1), MinHeight = 0, Background = Brushes.Transparent, Focusable = false };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Sets the height within the window (at least <see cref="Layoutable.MinHeight"/>, at most 85% of the window).</summary>
    public void ResizeTo(double height)
    {
        var window = TopLevel.GetTopLevel(this)?.Bounds.Height ?? 800;
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, window * 0.85));
        _ratio = window > 0 ? Height / window : _ratio;
        HeightRatioChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window changed its size: the terminal keeps its part of the window.</summary>
    public void FitWindow(double windowHeight)
    {
        if (windowHeight > 0 && !IsMaximized)
        {
            Height = Math.Clamp(windowHeight * _ratio, MinHeight, Math.Max(MinHeight, windowHeight * 0.85));
        }
    }

    /// <summary>Opens the first tab if there is none and focuses the active one.</summary>
    public void Activate()
    {
        if (_active is null)
        {
            // the tabs of the last session, else one with the default shell
            var shells = (RestoreTabs ?? []).Select(name => TerminalSession.FindShell(name))
                .OfType<TerminalSession.ShellInfo>().ToList();
            RestoreTabs = null;
            if (shells.Count == 0)
            {
                NewTab();
                return;
            }
            foreach (var shell in shells)
            {
                NewTab(shell);
            }
            Select(_consoles[0]);
            return;
        }
        _active.Activate();
    }

    /// <summary>A new tab with <paramref name="shell"/> (null: the default shell).</summary>
    public TerminalTab NewTab(TerminalSession.ShellInfo? shell = null)
    {
        shell ??= TerminalSession.ShellFor(Shell);
        TerminalTab console = Emulation && Services.Terminal.PseudoTerminal.IsSupported
            ? new EmulatedTerminal(shell, () => CurrentFolder?.Invoke())
            : new TerminalConsole(shell, () => CurrentFolder?.Invoke());
        console.SetFont(_font.Family, _font.Size);
        console.ShellExited += (_, _) => CloseTab(console); // "exit" closes the tab like in VS Code
        _consoles.Add(console);
        _host.Children.Add(console);
        Select(console);
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return console;
    }

    public void Select(TerminalTab console)
    {
        _active = console;
        foreach (var c in _consoles)
        {
            c.IsVisible = ReferenceEquals(c, console);
        }
        RebuildTabs();
        console.Activate();
    }

    public void CloseTab(TerminalTab console)
    {
        var index = _consoles.IndexOf(console);
        if (index < 0)
        {
            return;
        }
        console.Stop();
        _consoles.RemoveAt(index);
        _host.Children.Remove(console);
        TabsChanged?.Invoke(this, EventArgs.Empty);
        if (_consoles.Count == 0)
        {
            _active = null;
            RebuildTabs();
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (ReferenceEquals(_active, console))
        {
            Select(_consoles[Math.Min(index, _consoles.Count - 1)]);
        }
        else
        {
            RebuildTabs();
        }
    }

    private void RebuildTabs()
    {
        _tabStrip.Children.Clear();
        foreach (var console in _consoles)
        {
            var active = ReferenceEquals(console, _active);
            var same = _consoles.Count(c => c.Title == console.Title);
            var title = same > 1 ? $"{console.Title} {_consoles.Where(c => c.Title == console.Title).ToList().IndexOf(console) + 1}" : console.Title;
            var text = new TextBlock { Text = title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            if (active)
            {
                text.FontWeight = FontWeight.SemiBold;
            }
            var close = new Button { Content = "×", FontSize = 11, Padding = new Thickness(4, 0), MinHeight = 0, Background = Brushes.Transparent, Focusable = false };
            ToolTip.SetTip(close, "Close this terminal");
            var target = console;
            close.Click += (_, e) =>
            {
                e.Handled = true;
                CloseTab(target);
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(text);
            content.Children.Add(close);
            var tab = new Button { Content = content, Padding = new Thickness(8, 1, 2, 1), MinHeight = 0, Focusable = false, Classes = { "terminaltab" } };
            tab.Classes.Set("active", active);
            tab.Click += (_, _) => Select(target);
            _tabStrip.Children.Add(tab);
        }
    }

    private void ShowShellMenu(Control anchor)
    {
        var flyout = new MenuFlyout();
        foreach (var shell in TerminalSession.AvailableShells())
        {
            var item = new MenuItem { Header = shell.Name, Classes = { "noloc" } };
            ToolTip.SetTip(item, shell.FileName);
            var s = shell;
            item.Click += (_, _) => NewTab(s);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    /// <summary>Ends every shell (the terminal was closed or the window closes).</summary>
    public void Stop()
    {
        foreach (var console in _consoles)
        {
            console.Stop();
        }
        _consoles.Clear();
        _host.Children.Clear();
        _active = null;
        RebuildTabs();
    }
}
