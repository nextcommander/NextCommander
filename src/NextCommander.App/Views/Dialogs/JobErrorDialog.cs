using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// An error of an operation and what the dialog can offer for it: the programs that have the file open, the program's
/// own log folder (a file it writes to itself), opening the log setting or the file's properties.
/// </summary>
public sealed record JobErrorRequest(JobError Error)
{
    public Func<string, IReadOnlyList<string>>? LockedBy { get; init; }

    public string? OwnLogFolder { get; init; }

    public Func<Task>? OpenLogSetting { get; init; }

    public Func<string, Task>? ShowProperties { get; init; }

    /// <summary>A lost connection is retried by itself after this many seconds (0: not).</summary>
    public int AutoRetrySeconds { get; init; } = 10;
}

/// <summary>
/// The question of an operation stopped by an error (the user's template, 21 November 2026): where the operation is,
/// what happened in plain words (a file in use, by which program; no permission; a full disk; a lost connection),
/// the file, what can be done, the technical details; Skip, Retry, Retry as administrator, Stop, and doing the same
/// for the next errors of the kind. A lost connection retries by itself after a countdown.
/// </summary>
public sealed class JobErrorDialog : Window
{
    private readonly JobErrorRequest _request;
    private readonly JobError _error;
    private readonly UserControl _view = new();
    private readonly CheckBox _same = new() { FontSize = 12.5 };
    private readonly TextBlock _countdown = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly ProgressBar _countdownBar = new() { Width = 60, Height = 4, MinHeight = 4, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly IReadOnlyList<string> _lockedBy;
    private readonly bool _ownLog;
    private Avalonia.Threading.DispatcherTimer? _timer;
    private int _left;

    public JobErrorDialog(JobErrorRequest request)
    {
        _request = request;
        _error = request.Error;
        _ownLog = IsOwnLog(_error.Item, request.OwnLogFolder);
        _lockedBy = _error.Kind == ErrorKind.InUse && !_ownLog && request.LockedBy is { } who ? who(_error.Item) : [];
        Title = T("Error");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ConnectionLook.Apply(_view);
        _view.Content = Build();
        Content = _view;
        KeyDown += (_, e) =>
        {
            ErrorAction? action = e.Key switch
            {
                Key.Escape => ErrorAction.Cancel,
                Key.R when e.KeyModifiers == KeyModifiers.Alt => ErrorAction.Retry,
                Key.S when e.KeyModifiers == KeyModifiers.Alt => ErrorAction.Skip,
                Key.A when e.KeyModifiers == KeyModifiers.Alt && _error.Kind == ErrorKind.AccessDenied => ErrorAction.Elevate,
                _ => null,
            };
            if (action is { } chosen)
            {
                e.Handled = true;
                Choose(chosen);
            }
        };
        Closed += (_, _) => _timer?.Stop();
        Opened += (_, _) =>
        {
            if (_error.Kind == ErrorKind.NetworkLost && request.AutoRetrySeconds > 0)
            {
                StartCountdown(request.AutoRetrySeconds);
            }
        };
    }

    /// <summary>The choice (Cancel when closed).</summary>
    public ErrorAction Result { get; private set; } = ErrorAction.Cancel;

    /// <summary>The action of the accent button (Enter).</summary>
    public ErrorAction Primary => _error.Kind switch
    {
        ErrorKind.InUse when _ownLog => ErrorAction.Skip,
        ErrorKind.AccessDenied => ErrorAction.Elevate,
        ErrorKind.InUse or ErrorKind.DiskFull or ErrorKind.NetworkLost => ErrorAction.Retry,
        _ => ErrorAction.Skip,
    };

    /// <summary>The title, the line under it and the suggestions (tests).</summary>
    public string Text => string.Join("\n", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_view).OfType<TextBlock>().Select(t => t.Text));

    public bool SameForNext
    {
        get => _same.IsChecked == true;
        set => _same.IsChecked = value;
    }

    /// <summary>Picks an action: Skip with "the same for the next ones" skips every later error of the kind.</summary>
    public void Choose(ErrorAction action)
    {
        Result = action == ErrorAction.Skip && SameForNext ? ErrorAction.SkipAllOfKind : action;
        Close();
    }

    private static string T(string text) => Core.Localization.Localizer.T(text);

    private static bool IsOwnLog(string item, string? folder) =>
        folder is { Length: > 0 } && item.StartsWith(folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(item).StartsWith("nextcommander-", StringComparison.OrdinalIgnoreCase);

    /// <summary>The title, the reason and the color of the error (red: the step failed; amber: it can be fixed and retried).</summary>
    private (string Title, string Why, bool Amber, string Glyph) Explain()
    {
        var program = _lockedBy.Count > 0 ? string.Join(", ", _lockedBy) : null;
        return _error.Kind switch
        {
            ErrorKind.InUse when _ownLog => ("The file is in use", T("NextCommander writes to this file right now (it is today's program log), so it cannot be changed while the program runs."), true, "🔒"),
            ErrorKind.InUse when program is not null => ("The file is in use", T("{0} has the file open, so it cannot be changed now.").Replace("{0}", program, StringComparison.Ordinal), true, "🔒"),
            ErrorKind.InUse => ("The file is in use", T("Another program has the file open, so it cannot be changed now."), true, "🔒"),
            ErrorKind.AccessDenied => ("You don't have permission", T("Your account is not allowed to change this file. It may belong to another user, be read-only or be protected by the system."), false, "⛨"),
            ErrorKind.DiskFull => ("Not enough space", T("There is not enough free space on the target drive for this file."), false, "▭"),
            ErrorKind.NetworkLost => ("The network connection was lost", T("The connection to the server dropped while the file was being processed."), true, "⌁"),
            ErrorKind.NotFound => ("The file is not there", T("The file or folder is no longer there; it may have been moved or deleted meanwhile."), true, "?"),
            _ => ("The file could not be processed", _error.Message, false, "!"),
        };
    }

    /// <summary>What can be done: a line each, some with a button.</summary>
    private List<(string Text, string? Button, Func<Task>? Click)> Suggestions()
    {
        var program = _lockedBy.Count > 0 ? string.Join(", ", _lockedBy) : null;
        var list = new List<(string, string?, Func<Task>?)>();
        switch (_error.Kind)
        {
            case ErrorKind.InUse when _ownLog:
                list.Add((T("Skip it. It can be deleted after the program is closed, or tomorrow when a new log starts."), null, null));
                if (_request.OpenLogSetting is { } open)
                {
                    list.Add((T("To stop writing the program log, turn it off in the options (Log file)."), T("Open the setting"), open));
                }
                break;
            case ErrorKind.InUse:
                list.Add((program is null ? T("Close the program that uses it and choose Retry.") : T("Close {0} and choose Retry.").Replace("{0}", program, StringComparison.Ordinal), null, null));
                list.Add((T("Or skip the file and go on with the rest."), null, null));
                break;
            case ErrorKind.AccessDenied:
                list.Add((T("Retry as administrator: the file is done with administrator rights when the operation ends."), null, null));
                if (_request.ShowProperties is { } properties && (File.Exists(_error.Item) || Directory.Exists(_error.Item)))
                {
                    list.Add((T("Check the file's security settings in its properties."), T("Properties"), () => properties(_error.Item)));
                }
                list.Add((T("Or skip it and go on."), null, null));
                break;
            case ErrorKind.DiskFull:
                list.Add((T("Free up space on the target drive, then choose Retry."), null, null));
                list.Add((T("Or copy to another drive."), null, null));
                break;
            case ErrorKind.NetworkLost:
                list.Add((T("Check the network or the VPN; Retry goes on from this file."), null, null));
                if (_request.AutoRetrySeconds > 0)
                {
                    list.Add((T("It retries by itself while the countdown runs."), null, null));
                }
                break;
            case ErrorKind.NotFound:
                list.Add((T("Skip it and go on with the rest."), null, null));
                break;
            default:
                list.Add((T("Retry, skip the file, or stop the operation."), null, null));
                break;
        }
        return list;
    }

    // ---------------------------------------------------------------- layout

    private Control Build()
    {
        var (title, why, amber, glyph) = Explain();
        var root = new DockPanel();

        // where the operation is: its name, how far, paused
        var context = new DockPanel { Margin = new Thickness(20, 9) };
        var paused = ConnectionLook.Muted(new TextBlock { Text = "Paused until you choose", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(paused, Dock.Right);
        var where = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        where.Children.Add(ConnectionLook.Muted(new TextBlock { Text = _error.Operation ?? T("Operation"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }));
        if (_error.TotalFiles > 0)
        {
            var done = Math.Min(_error.DoneFiles, _error.TotalFiles);
            var bar = new ProgressBar { Width = 140, Height = 4, MinHeight = 4, Minimum = 0, Maximum = _error.TotalFiles, Value = done, VerticalAlignment = VerticalAlignment.Center };
            bar[!ForegroundProperty] = new DynamicResourceExtension("ConnWarn");
            var count = new TextBlock
            {
                Text = T("File {0} of {1} · {2} done").Replace("{0}", Math.Min(done + 1, _error.TotalFiles).ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                    .Replace("{1}", _error.TotalFiles.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
                    .Replace("{2}", done.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal),
                FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" },
            };
            where.Children.AddRange([bar, ConnectionLook.Muted(count)]);
        }
        context.Children.AddRange([paused, where]);
        var contextBar = new Border { Child = context, BorderThickness = new Thickness(0, 0, 0, 1) };
        contextBar[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnSide");
        contextBar[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");
        DockPanel.SetDock(contextBar, Dock.Top);

        var main = new StackPanel { Margin = new Thickness(20, 18, 20, 6) };

        // the error: its tile, title and reason
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0) };
        tile[!Border.BackgroundProperty] = new DynamicResourceExtension(amber ? "ConnWarnBack" : "ConnProblemBack");
        var sign = new TextBlock { Text = glyph, FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
        sign[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(amber ? "ConnWarn" : "ConnProblem");
        tile.Child = sign;
        var texts = new StackPanel();
        texts.Children.AddRange([
            new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap },
            ConnectionLook.Muted(new TextBlock { Text = why, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } }),
        ]);
        var head = new DockPanel();
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.AddRange([tile, texts]);
        main.Children.Add(head);

        var indented = new StackPanel { Margin = new Thickness(54, 0, 0, 0) };
        indented.Children.Add(FileCard());
        if (_lockedBy.Count > 0)
        {
            var who = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Margin = new Thickness(0, 12, 0, 0) };
            who[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnWarnBack");
            var whoText = new TextBlock { Text = "🔒  " + T("Open in {0}").Replace("{0}", string.Join(", ", _lockedBy), StringComparison.Ordinal), FontSize = 12.5, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } };
            whoText[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("ConnWarn");
            who.Child = whoText;
            indented.Children.Add(who);
        }
        if (_error.Kind == ErrorKind.NetworkLost && _request.AutoRetrySeconds > 0)
        {
            var stop = new Button { Content = "Stop retrying", Classes = { "tb" }, Height = 26, MinHeight = 26, Padding = new Thickness(8, 0), FontSize = 12 };
            stop.Click += (_, _) =>
            {
                _timer?.Stop();
                _countdown.Text = T("Automatic retry stopped.");
                stop.IsEnabled = false;
            };
            var auto = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
            auto.Children.AddRange([_countdownBar, ConnectionLook.Muted(_countdown), stop]);
            indented.Children.Add(auto);
        }

        // what can be done
        var todo = new StackPanel { Spacing = 6, Margin = new Thickness(0, 14, 0, 0) };
        todo.Children.Add(ConnectionLook.Muted(new TextBlock { Text = "What you can do", FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) }));
        foreach (var (text, button, click) in Suggestions())
        {
            var arrow = new TextBlock { Text = "→", FontSize = 13, Margin = new Thickness(0, 0, 8, 0), Classes = { "noloc" } };
            arrow[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("ConnAccent");
            var line = new DockPanel();
            DockPanel.SetDock(arrow, Dock.Left);
            line.Children.Add(arrow);
            if (button is not null && click is not null)
            {
                var action = new Button { Content = button, Classes = { "tb", "noloc" }, Height = 26, MinHeight = 26, Padding = new Thickness(8, 0), FontSize = 12, Margin = new Thickness(8, -3, 0, -3), VerticalAlignment = VerticalAlignment.Top };
                action.Click += async (_, _) => await click();
                DockPanel.SetDock(action, Dock.Right);
                line.Children.Add(action);
            }
            line.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Classes = { "noloc" } });
            todo.Children.Add(line);
        }
        indented.Children.Add(todo);
        indented.Children.Add(Details());
        main.Children.Add(indented);

        // the bar at the bottom: the same for the next ones, Stop, Skip, Retry (as administrator)
        _same.Content = T("Do the same for the next errors like this");
        _same.Classes.Add("noloc");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var stopOperation = Footer("Stop the operation", ErrorAction.Cancel);
        ToolTip.SetTip(stopOperation, "Esc");
        buttons.Children.Add(stopOperation);
        buttons.Children.Add(Footer("Skip", ErrorAction.Skip, "Alt+S"));
        buttons.Children.Add(Footer("Retry", ErrorAction.Retry, "Alt+R"));
        if (_error.Kind == ErrorKind.AccessDenied)
        {
            buttons.Children.Add(Footer("Retry as administrator", ErrorAction.Elevate, "Alt+A"));
        }
        // the check box under the details (beside four buttons it would wrap), the buttons on the right of the bar
        _same.Margin = new Thickness(0, 12, 0, 0);
        indented.Children.Add(_same);
        var footer = new DockPanel { Margin = new Thickness(20, 12) };
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(buttons);
        var footerBar = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 14, 0, 0) };
        footerBar[!Border.BackgroundProperty] = new DynamicResourceExtension("ConnSide");
        footerBar[!Border.BorderBrushProperty] = new DynamicResourceExtension("ConnStroke");
        DockPanel.SetDock(footerBar, Dock.Bottom);

        root.Children.AddRange([contextBar, footerBar, main]);
        return root;
    }

    private Button Footer(string text, ErrorAction action, string? keys = null)
    {
        var button = new Button { Content = text, Classes = { "tb" }, Tag = action };
        if (action == Primary)
        {
            button.Classes.Add("primary");
            button.IsDefault = true;
            Opened += (_, _) => button.Focus();
        }
        if (keys is not null)
        {
            ToolTip.SetTip(button, keys);
        }
        button.Click += (_, _) => Choose(action);
        return button;
    }

    /// <summary>The file: its name, its folder (the end of it shown), its size and date when it is on this computer.</summary>
    private Border FileCard()
    {
        var item = _error.Item;
        var name = Path.GetFileName(item.TrimEnd('\\', '/'));
        var folder = Path.GetDirectoryName(item.TrimEnd('\\', '/')) ?? string.Empty;
        string? meta = null;
        try
        {
            if (File.Exists(item))
            {
                var info = new FileInfo(item);
                meta = Core.Formatting.SizeFormatter.Human(info.Length) + " · " + info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        var icon = ConnectionLook.Muted(new TextBlock { Text = "🗎", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Classes = { "noloc" } }, 3);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.AddRange([
            new TextBlock { Text = name.Length > 0 ? name : item, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } },
            ConnectionLook.Muted(new TextBlock { Text = folder, FontFamily = ConnectionLook.Mono, FontSize = 11.5, TextTrimming = TextTrimming.LeadingCharacterEllipsis, IsVisible = folder.Length > 0, Classes = { "noloc" } }, 3),
        ]);
        ToolTip.SetTip(texts, item);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        if (meta is not null)
        {
            var size = ConnectionLook.Muted(new TextBlock { Text = meta, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Classes = { "noloc" } }, 3);
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
        }
        row.Children.Add(texts);
        var card = ConnectionLook.Card(row, new Thickness(12, 10));
        card.Margin = new Thickness(0, 14, 0, 0);
        return card;
    }

    /// <summary>The system's name of an error number (Windows), for the technical details.</summary>
    private static string CodeName(int code) => code switch
    {
        2 => "ERROR_FILE_NOT_FOUND",
        3 => "ERROR_PATH_NOT_FOUND",
        5 => "ERROR_ACCESS_DENIED",
        32 => "ERROR_SHARING_VIOLATION",
        33 => "ERROR_LOCK_VIOLATION",
        39 => "ERROR_HANDLE_DISK_FULL",
        53 => "ERROR_BAD_NETPATH",
        59 => "ERROR_UNEXP_NET_ERR",
        64 => "ERROR_NETNAME_DELETED",
        112 => "ERROR_DISK_FULL",
        121 => "ERROR_SEM_TIMEOUT",
        1231 => "ERROR_NETWORK_UNREACHABLE",
        _ => string.Empty,
    };

    /// <summary>The technical details, folded: the operation, the file, the error number and message, the time; Copy details.</summary>
    private Expander Details()
    {
        var code = _error.Code != 0
            ? "0x" + _error.Code.ToString("X2", CultureInfo.InvariantCulture) + (OperatingSystem.IsWindows() && CodeName(_error.Code) is { Length: > 0 } name ? " · " + name : string.Empty)
            : "-";
        (string Caption, string Value)[] rows =
        [
            ("Operation", _error.Operation ?? "-"),
            ("File", _error.Item),
            ("Error", code),
            ("Message", _error.Message),
            ("Time", _error.Time.ToString("G", CultureInfo.CurrentCulture)),
        ];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*") };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = ConnectionLook.Muted(new TextBlock { Text = rows[i].Caption, FontSize = 12, Margin = new Thickness(0, 2) }, 3);
            var value = new SelectableTextBlock { Text = rows[i].Value, FontFamily = ConnectionLook.Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2), Classes = { "noloc" } };
            Grid.SetRow(caption, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.AddRange([caption, value]);
        }
        var copy = new Button { Content = "Copy details", Classes = { "tb" }, Height = 26, MinHeight = 26, Padding = new Thickness(8, 0), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, rows.Select(r => r.Caption + ": " + r.Value)));
            }
        };
        var content = new StackPanel();
        content.Children.AddRange([grid, copy]);
        var card = ConnectionLook.Card(content, new Thickness(12, 10));
        card.Margin = new Thickness(0, 8, 0, 0);
        var header = ConnectionLook.Muted(new TextBlock { Text = "Technical details", FontSize = 12.5 });
        return new Expander { Header = header, Content = card, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "connmore" } };
    }

    // ---------------------------------------------------------------- countdown

    private void StartCountdown(int seconds)
    {
        _left = seconds;
        void Tick()
        {
            _countdown.Text = T("Retrying in {0} s…").Replace("{0}", _left.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
            _countdownBar.Value = 1 - ((double)_left / seconds);
            if (_left-- <= 0)
            {
                _timer?.Stop();
                Choose(ErrorAction.Retry);
            }
        }
        Tick();
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }
}
