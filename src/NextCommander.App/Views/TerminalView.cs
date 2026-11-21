using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly Button _files;
    private readonly Button _log;
    private readonly Border _findBar;
    private readonly TextBox _findText = new() { Width = 260, MinHeight = 24, Padding = new Thickness(6, 2), FontSize = 12, PlaceholderText = "Find in the output" };
    private readonly CheckBox _findCase = new() { Content = "Aa", Classes = { "noloc" }, FontSize = 12, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _findStatus = new() { FontSize = 12, MinWidth = 80, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };

    /// <summary>"Files (SFTP)" of an SSH session: its connection and the shell's folder (null: not known).</summary>
    public event EventHandler<(Core.Config.RemoteConnection Connection, string? Folder)>? FilesRequested;

    /// <summary>"Files (SFTP)" is offered (the active tab is an SSH session; tests).</summary>
    public bool OffersFiles => _files.IsVisible;

    /// <summary>"Files (SFTP)" of the active tab (tests).</summary>
    public void OpenFiles()
    {
        if (_active?.Connection is { Protocol: Core.Config.RemoteProtocol.Ssh } ssh)
        {
            FilesRequested?.Invoke(this, (ssh, _active.WorkingDirectory));
        }
    }

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
        // an SSH session: its computer's files over SFTP in a panel (where the shell is, when it says)
        _files = HeaderButton("Files (SFTP)", "The files of this SSH session's computer in a new tab of the active panel (in the shell's folder when the shell reports it)",
            () =>
            {
                if (_active?.Connection is { Protocol: Core.Config.RemoteProtocol.Ssh } ssh)
                {
                    FilesRequested?.Invoke(this, (ssh, _active.WorkingDirectory));
                }
            });
        _files.IsVisible = false;
        buttons.Children.Add(_files);
        // the output of the tab into a text file (MobaXterm's session logs)
        _log = HeaderButton("Log", "Write the output of this tab to a text file", () => { });
        _log.Tag = "log";
        _log.Click += (_, _) => ShowLogMenu(_log);
        buttons.Children.Add(_log);
        buttons.Children.Add(HeaderButton("Find", "Find in the output and the scrollback (Ctrl+Shift+F)", OpenFind));
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

        // find in the output: Enter goes to older matches, Shift+Enter to newer ones, Esc closes
        ToolTip.SetTip(_findCase, "Match case");
        var older = HeaderButton("↑", "Older match (Enter)", () => FindNext(-1));
        var newer = HeaderButton("↓", "Newer match (Shift+Enter)", () => FindNext(1));
        var closeFind = HeaderButton("✕", "Close the search (Esc)", CloseFind);
        Avalonia.Automation.AutomationProperties.SetName(_findText, "Find in the output");
        _findText.TextChanged += (_, _) => FindNext(0);
        _findCase.IsCheckedChanged += (_, _) => FindNext(0);
        _findText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : -1);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseFind();
            }
        };
        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 2, 8, 4) };
        findRow.Children.AddRange([_findText, _findCase, older, newer, _findStatus, closeFind]);
        _findBar = new Border { Child = findRow, IsVisible = false };
        SetDock(_findBar, Dock.Top);
        Children.Add(_findBar);
        Children.Add(_host);
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmStatusBrush");
    }

    /// <summary>The find bar is open (tests).</summary>
    public bool IsFindOpen => _findBar.IsVisible;

    /// <summary>The text searched for (tests).</summary>
    public string FindText
    {
        get => _findText.Text ?? string.Empty;
        set
        {
            _findText.Text = value;
            FindNext(0);
        }
    }

    /// <summary>"2 of 5", "No results" (tests).</summary>
    public string FindStatus => _findStatus.Text ?? string.Empty;

    /// <summary>Opens the find bar with the text field focused (Ctrl+Shift+F, the Find button).</summary>
    public void OpenFind()
    {
        _findBar.IsVisible = true;
        _findText.Focus();
        _findText.SelectAll();
        FindNext(0);
    }

    /// <summary>Closes the find bar: the marks go, the terminal takes the keys again.</summary>
    public void CloseFind()
    {
        _findBar.IsVisible = false;
        _findStatus.Text = string.Empty;
        _active?.ClearFind();
        _active?.Activate();
    }

    /// <summary>The newest match (0), an older (-1) or a newer one (+1) in the active tab.</summary>
    public void FindNext(int direction)
    {
        if (!_findBar.IsVisible || _active is not { } tab)
        {
            return;
        }
        var text = FindText;
        var (current, count) = tab.Find(text, _findCase.IsChecked == true, direction);
        _findStatus.Text = text.Length == 0 ? string.Empty
            : count == 0 ? Core.Localization.Localizer.T("No results")
            : Core.Localization.Localizer.T("{0} of {1}").Replace("{0}", current.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
                .Replace("{1}", count.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
    }

    /// <summary>"Open the log folder": the folder of the terminal logs in the active panel.</summary>
    public event EventHandler<string>? LogFolderRequested;

    /// <summary>Every new tab writes its output to a log file (setting).</summary>
    public bool LogAll { get; set; }

    /// <summary>The folder of the log files (setting; empty: logs/terminal in the settings folder).</summary>
    public string? LogFolder { get; set; }

    /// <summary>The folder the logs go to now.</summary>
    public string LogFolderPath => string.IsNullOrWhiteSpace(LogFolder)
        ? Core.Text.TerminalLog.DefaultFolder ?? Path.GetTempPath()
        : Environment.ExpandEnvironmentVariables(LogFolder.Trim());

    /// <summary>Why the last log could not start (null: it did).</summary>
    public string? LogError { get; private set; }

    /// <summary>Starts or stops the log of the active tab (the Log menu; tests).</summary>
    public void ToggleLog()
    {
        if (_active is not { } tab)
        {
            return;
        }
        if (tab.Log is not null)
        {
            tab.StopLog();
        }
        else
        {
            StartLog(tab);
        }
    }

    private void StartLog(TerminalTab tab)
    {
        LogError = tab.StartLog(LogFolder, out var error) ? null : error;
    }

    private void ShowLogMenu(Control anchor)
    {
        var flyout = new MenuFlyout();
        var logging = _active?.Log is not null;
        var toggle = new MenuItem { Header = logging ? "Stop logging" : "Log this tab to a file", Icon = MenuIcons.Create(logging ? "terminal.logstop" : "terminal.log"), IsEnabled = _active is not null };
        toggle.Click += (_, _) => ToggleLog();
        flyout.Items.Add(toggle);
        var folder = new MenuItem { Header = "Open the log folder", Icon = MenuIcons.Create("terminal.logfolder") };
        folder.Click += (_, _) => LogFolderRequested?.Invoke(this, LogFolderPath);
        flyout.Items.Add(folder);
        if (LogError is { } error)
        {
            flyout.Items.Add(new Separator());
            flyout.Items.Add(new MenuItem { Header = error, IsEnabled = false, Classes = { "noloc" } });
        }
        flyout.ShowAt(anchor);
    }

    /// <summary>The Log button shows whether the active tab is logged (its file in the tip).</summary>
    private void UpdateLogButton()
    {
        var log = _active?.Log;
        _log.Content = log is not null ? "● Log" : "Log";
        if (log is not null)
        {
            _log.Foreground = new SolidColorBrush(Color.Parse("#E5484D"));
        }
        else
        {
            _log.ClearValue(TemplatedControl.ForegroundProperty);
        }
        ToolTip.SetTip(_log, log is not null ? Core.Localization.Localizer.T("Logging to {0}").Replace("{0}", log.Path, StringComparison.Ordinal) : "Write the output of this tab to a text file");
    }

    /// <summary>The Log button is on (the active tab is logged; tests).</summary>
    public bool IsLogging => _active?.Log is not null;

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
    /// <remarks>With <paramref name="connect"/> the tab is a remote session (Telnet) on a terminal screen.</remarks>
    public TerminalTab NewTab(TerminalSession.ShellInfo? shell = null, Func<int, int, Services.Terminal.PseudoTerminal>? connect = null,
        Core.Config.RemoteConnection? connection = null)
    {
        shell ??= TerminalSession.ShellFor(Shell);
        TerminalTab console = connect is not null || (Emulation && Services.Terminal.PseudoTerminal.IsSupported)
            ? new EmulatedTerminal(shell, () => CurrentFolder?.Invoke(), connect) { Connection = connection }
            : new TerminalConsole(shell, () => CurrentFolder?.Invoke()) { Connection = connection };
        console.SetFont(_font.Family, _font.Size);
        console.ShellExited += (_, _) => CloseTab(console); // "exit" closes the tab like in VS Code
        console.FindRequested += (_, _) => OpenFind();
        console.LogChanged += (_, _) =>
        {
            if (ReferenceEquals(console, _active))
            {
                UpdateLogButton();
            }
        };
        if (LogAll)
        {
            StartLog(console);
        }
        _consoles.Add(console);
        _host.Children.Add(console);
        Select(console);
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return console;
    }

    public void Select(TerminalTab console)
    {
        _active?.ClearFind();
        _active = console;
        _files.IsVisible = console.Connection?.Protocol == Core.Config.RemoteProtocol.Ssh;
        UpdateLogButton();
        foreach (var c in _consoles)
        {
            c.IsVisible = ReferenceEquals(c, console);
        }
        RebuildTabs();
        console.Activate();
        FindNext(0); // an open search goes on in this tab
    }

    public void CloseTab(TerminalTab console)
    {
        var index = _consoles.IndexOf(console);
        if (index < 0)
        {
            return;
        }
        console.Stop();
        console.StopLog();
        _consoles.RemoveAt(index);
        _host.Children.Remove(console);
        TabsChanged?.Invoke(this, EventArgs.Empty);
        if (_consoles.Count == 0)
        {
            _active = null;
            _files.IsVisible = false;
            UpdateLogButton();
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
            var item = new MenuItem { Header = shell.DisplayName, Classes = { "noloc" } };
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
            console.StopLog();
        }
        _consoles.Clear();
        _host.Children.Clear();
        _active = null;
        RebuildTabs();
    }
}
