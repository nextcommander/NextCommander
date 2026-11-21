using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;

namespace NextCommander.App.Views;

/// <summary>
/// A terminal tab with a real terminal: the shell runs on a pseudo terminal (ConPTY / pty) and its output is drawn
/// by a VT100 / xterm screen (<see cref="VtScreen"/>), so colors, cursor movement and full screen programs (editors,
/// pagers, top, vim, mc) work. Keys go to the shell as a terminal sends them; Ctrl+Shift+C / Ctrl+Shift+V (or the
/// right mouse button) copy and paste, the mouse wheel and Shift+PgUp / PgDn scroll back.
/// </summary>
public sealed class EmulatedTerminal : TerminalTab
{
    private readonly TerminalScreen _screen;
    private PseudoTerminal? _terminal;
    private readonly Func<int, int, PseudoTerminal>? _connect;

    public EmulatedTerminal(TerminalSession.ShellInfo shell, Func<string?> currentFolder, Func<int, int, PseudoTerminal>? connect = null)
        : base(shell, currentFolder)
    {
        _connect = connect;
        _screen = new TerminalScreen(this);
        Child = _screen;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmListerBackgroundBrush");
    }

    public VtScreen Screen => _screen.Vt;

    /// <summary>The control that draws the screen and takes the keys.</summary>
    public Control ScreenControl => _screen;

    public PseudoTerminal? Terminal => _terminal;

    /// <summary>A remote session (Telnet) in place of a local shell.</summary>
    public override bool IsRemote => _connect is not null;

    /// <summary>Scrollback and screen as text.</summary>
    public override string OutputText => _screen.Vt.AllText();

    public override string? WorkingDirectory => _screen.Vt.WorkingDirectory;

    public override void Activate()
    {
        if (_terminal is null || _terminal.HasExited)
        {
            StartShell();
        }
        Dispatcher.UIThread.Post(() => _screen.Focus(), DispatcherPriority.Input);
    }

    private void StartShell()
    {
        _terminal?.Dispose();
        var folder = CurrentFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        StartFolder = folder;
        try
        {
            var terminal = _connect is not null
                ? _connect(_screen.Vt.Columns, _screen.Vt.Rows)
                : PseudoTerminal.Start(Shell.FileName, Shell.TerminalArguments, folder, _screen.Vt.Columns, _screen.Vt.Rows);
            terminal.Output += _screen.Enqueue;
            terminal.Output += LogOutput;
            terminal.Exited += code =>
            {
                if (!ReferenceEquals(terminal, _terminal))
                {
                    return;
                }
                if (_connect is not null)
                {
                    // the tab stays with what the server wrote last; Enter connects again
                    _screen.Enqueue("\r\n[" + Core.Localization.Localizer.T("Disconnected. Press Enter to connect again.") + "]\r\n");
                }
                else
                {
                    Dispatcher.UIThread.Post(RaiseShellExited);
                }
            };
            _terminal = terminal;
            terminal.Open();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            _terminal = null;
            _screen.Enqueue($"{Shell.FileName}: {ex.Message}\r\n");
        }
    }

    /// <summary>Key presses and pasted text for the shell.</summary>
    internal void Send(string text)
    {
        if (_terminal is null || _terminal.HasExited)
        {
            StartShell();
        }
        _terminal?.Write(text);
    }

    internal void ResizeTerminal(int columns, int rows) => _terminal?.Resize(columns, rows);

    public override void Restart()
    {
        var old = _terminal;
        _terminal = null; // its exit is not "the shell ended"
        old?.Dispose();
        _screen.Vt.Feed("^C\r\n");
        StartShell();
        _screen.Focus();
    }

    public override void Clear()
    {
        _screen.Vt.Clear();
        _screen.InvalidateVisual();
        // the shell draws its prompt again (a remote session is left alone)
        if (_connect is null)
        {
            Send(Shell.IsCommandPrompt ? "cls\r" : "\f");
        }
        _screen.Focus();
    }

    public override void Run(string line)
    {
        if (!string.IsNullOrEmpty(line))
        {
            Send(line + "\r");
        }
    }

    public override void SetFont(string family, double size) => _screen.SetFont(family, size);

    public override void Stop()
    {
        var old = _terminal;
        _terminal = null;
        old?.Dispose();
    }
}

/// <summary>Draws a <see cref="VtScreen"/> as a grid of cells and turns key presses into terminal input.</summary>
internal sealed class TerminalScreen : Control
{
    private const double Padding = 6;

    /// <summary>The 16 basic colors (dark and bright).</summary>
    private static readonly Color[] s_basic =
    [
        Color.FromRgb(0x0C, 0x0C, 0x0C), Color.FromRgb(0xC5, 0x0F, 0x1F), Color.FromRgb(0x13, 0xA1, 0x0E), Color.FromRgb(0xC1, 0x9C, 0x00),
        Color.FromRgb(0x00, 0x37, 0xDA), Color.FromRgb(0x88, 0x17, 0x98), Color.FromRgb(0x3A, 0x96, 0xDD), Color.FromRgb(0xCC, 0xCC, 0xCC),
        Color.FromRgb(0x76, 0x76, 0x76), Color.FromRgb(0xE7, 0x48, 0x56), Color.FromRgb(0x16, 0xC6, 0x0C), Color.FromRgb(0xF9, 0xF1, 0xA5),
        Color.FromRgb(0x3B, 0x78, 0xFF), Color.FromRgb(0xB4, 0x00, 0x9E), Color.FromRgb(0x61, 0xD6, 0xD6), Color.FromRgb(0xF2, 0xF2, 0xF2),
    ];

    private readonly EmulatedTerminal _owner;
    private readonly StringBuilder _pending = new();
    private FontFamily _font = new(NextCommander.Core.Config.AppSettings.DefaultTerminalFont);
    private double _fontSize = 14;
    private bool _flushQueued;
    private double _cellWidth;
    private double _cellHeight;
    private int _scrollOffset;
    private (int Line, int Column)? _selectionStart;
    private (int Line, int Column)? _selectionEnd;
    private bool _selecting;
    private bool _suppressText;

    public TerminalScreen(EmulatedTerminal owner)
    {
        _owner = owner;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Vt = new VtScreen(80, 24);
        Vt.Reply += reply => _owner.Send(reply);
        Vt.TitleChanged += () => Dispatcher.UIThread.Post(() => (Parent as Control)?.InvalidateVisual());
        Avalonia.Automation.AutomationProperties.SetName(this, "Terminal");
        MeasureCell();
    }

    public VtScreen Vt { get; }

    private Typeface Typeface(VtFlags flags) => new(_font, flags.HasFlag(VtFlags.Italic) ? FontStyle.Italic : FontStyle.Normal,
        flags.HasFlag(VtFlags.Bold) ? FontWeight.Bold : FontWeight.Normal);

    private void MeasureCell()
    {
        var sample = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface(VtFlags.None), _fontSize, Brushes.White);
        _cellWidth = Math.Max(4, sample.WidthIncludingTrailingWhitespace / 10);
        _cellHeight = Math.Max(8, Math.Ceiling(sample.Height));
    }

    /// <summary>Output of the shell (any thread): fed to the screen on the UI thread in batches.</summary>
    public void Enqueue(string text)
    {
        lock (_pending)
        {
            _pending.Append(text);
            if (_flushQueued)
            {
                return;
            }
            _flushQueued = true;
        }
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        string text;
        lock (_pending)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushQueued = false;
        }
        Vt.Feed(text);
        InvalidateVisual();
    }

    /// <summary>Another font: the cell size changes, so do the columns and rows.</summary>
    public void SetFont(string family, double size)
    {
        if (string.Equals(_font.ToString(), family, StringComparison.Ordinal) && Math.Abs(_fontSize - size) < 0.01)
        {
            return;
        }
        _font = new FontFamily(string.IsNullOrWhiteSpace(family) ? NextCommander.Core.Config.AppSettings.DefaultTerminalFont : family);
        _fontSize = Math.Clamp(size, 6, 48);
        MeasureCell();
        Fit(Bounds.Size);
        InvalidateVisual();
    }

    /// <summary>Character cell size (tests).</summary>
    public Size CellSize => new(_cellWidth, _cellHeight);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Fit(e.NewSize);
    }

    private void Fit(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        var columns = Math.Max(10, (int)((size.Width - Padding * 2) / _cellWidth));
        var rows = Math.Max(2, (int)((size.Height - Padding) / _cellHeight));
        if (columns != Vt.Columns || rows != Vt.Rows)
        {
            Vt.Resize(columns, rows);
            _owner.ResizeTerminal(columns, rows);
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>Text color: dark on a light background, light on a dark one.</summary>
    private static Color ForegroundFor(Color background) =>
        (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255 > 0.5 ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xCC, 0xCC, 0xCC);

    /// <summary>The color theme's viewer background, else the window's region color of the light / dark theme.</summary>
    private Color DefaultBackground =>
        (this.FindResource(ActualThemeVariant, "FmListerBackgroundBrush") as ISolidColorBrush)?.Color
        ?? (this.FindResource(ActualThemeVariant, "SystemRegionColor") is Color region ? region : (Color?)null)
        ?? (ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White);

    private static Color PaletteColor(int index)
    {
        if (index < 16)
        {
            return s_basic[index];
        }
        if (index < 232)
        {
            var i = index - 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }
        var gray = (byte)(8 + (index - 232) * 10);
        return Color.FromRgb(gray, gray, gray);
    }

    private static Color? ColorOf(int value) => value switch
    {
        < 0 => null,
        >= 0x1000000 => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value),
        _ => PaletteColor(value),
    };

    /// <summary>The first line shown: an index into scrollback + screen.</summary>
    private int TopLine => Vt.ScrollbackCount - _scrollOffset;

    private IReadOnlyList<VtCell> LineAt(int index) =>
        index < Vt.ScrollbackCount ? Vt.ScrollbackLine(index) : Vt.Line(index - Vt.ScrollbackCount);

    private bool IsSelected(int line, int column)
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b)
        {
            return false;
        }
        var (start, end) = (a.Line, a.Column).CompareTo((b.Line, b.Column)) <= 0 ? (a, b) : (b, a);
        return (line, column).CompareTo((start.Line, start.Column)) >= 0 && (line, column).CompareTo((end.Line, end.Column)) <= 0;
    }

    public override void Render(DrawingContext context)
    {
        var background = DefaultBackground;
        var foreground = ForegroundFor(background);
        context.FillRectangle(new SolidColorBrush(background), new Rect(Bounds.Size));
        var top = TopLine;
        var selection = new SolidColorBrush(Color.FromArgb(0x60, 0x3A, 0x96, 0xDD));
        for (var row = 0; row < Vt.Rows; row++)
        {
            var index = top + row;
            if (index < 0 || index >= Vt.ScrollbackCount + Vt.Rows)
            {
                continue;
            }
            var line = LineAt(index);
            var y = Padding / 2 + row * _cellHeight;
            var x = 0;
            while (x < line.Count)
            {
                // a run of cells with the same style (wide and non-ASCII characters are drawn one by one)
                var style = line[x].Style;
                var start = x;
                var text = new StringBuilder();
                var single = line[x].CodePoint > 0x7E;
                do
                {
                    var cell = line[x];
                    text.Append(cell.CodePoint switch
                    {
                        > 0 => char.ConvertFromUtf32(cell.CodePoint),
                        0 => " ",
                        _ => string.Empty,
                    });
                    x++;
                }
                while (!single && x < line.Count && line[x].Style == style && line[x].CodePoint is >= 0 and <= 0x7E);
                if (single && x < line.Count && line[x].CodePoint == -1)
                {
                    x++; // the right half of a wide character
                }
                var fg = ColorOf(style.Foreground) ?? foreground;
                var bg = ColorOf(style.Background);
                if (style.Foreground is >= 0 and < 8 && style.Flags.HasFlag(VtFlags.Bold))
                {
                    fg = PaletteColor(style.Foreground + 8); // bold as bright
                }
                if (style.Flags.HasFlag(VtFlags.Inverse))
                {
                    (fg, bg) = (bg ?? background, fg);
                }
                if (style.Flags.HasFlag(VtFlags.Dim))
                {
                    fg = Color.FromArgb(0xA0, fg.R, fg.G, fg.B);
                }
                var rect = new Rect(Padding + start * _cellWidth, y, (x - start) * _cellWidth, _cellHeight);
                if (bg is { } b)
                {
                    context.FillRectangle(new SolidColorBrush(b), rect);
                }
                var content = text.ToString();
                if (content.Trim().Length > 0 && !style.Flags.HasFlag(VtFlags.Hidden))
                {
                    var brush = new SolidColorBrush(fg);
                    var formatted = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface(style.Flags), _fontSize, brush);
                    context.DrawText(formatted, rect.TopLeft);
                    if (style.Flags.HasFlag(VtFlags.Underline))
                    {
                        context.DrawLine(new Pen(brush), new Point(rect.Left, rect.Bottom - 1), new Point(rect.Right, rect.Bottom - 1));
                    }
                    if (style.Flags.HasFlag(VtFlags.Strike))
                    {
                        context.DrawLine(new Pen(brush), new Point(rect.Left, rect.Center.Y), new Point(rect.Right, rect.Center.Y));
                    }
                }
            }
            if (_selectionStart is not null)
            {
                for (var column = 0; column < line.Count; column++)
                {
                    if (IsSelected(index, column))
                    {
                        context.FillRectangle(selection, new Rect(Padding + column * _cellWidth, y, _cellWidth, _cellHeight));
                    }
                }
            }
        }
        // the cursor: a block when focused, an outline otherwise
        if (Vt.CursorVisible && _scrollOffset == 0)
        {
            var rect = new Rect(Padding + Vt.CursorX * _cellWidth, Padding / 2 + Vt.CursorY * _cellHeight, _cellWidth, _cellHeight);
            var cursor = Color.FromArgb(0xB0, foreground.R, foreground.G, foreground.B);
            if (IsFocused)
            {
                context.FillRectangle(new SolidColorBrush(cursor), rect);
            }
            else
            {
                context.DrawRectangle(new Pen(new SolidColorBrush(cursor)), rect.Deflate(0.5));
            }
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- keys

    /// <summary>The bytes a terminal sends for a key; null when the key is typed text (handled by text input).</summary>
    public static string? KeySequence(Key key, KeyModifiers modifiers, bool applicationCursor)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var code = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Cursor(char final) => code > 1 ? $"\x1b[1;{code}{final}" : applicationCursor ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int number) => code > 1 ? $"\x1b[{number};{code}~" : $"\x1b[{number}~";
        string Function(char final) => code > 1 ? $"\x1b[1;{code}{final}" : $"\x1bO{final}";
        switch (key)
        {
            case Key.Up:
                return Cursor('A');
            case Key.Down:
                return Cursor('B');
            case Key.Right:
                return Cursor('C');
            case Key.Left:
                return Cursor('D');
            case Key.Home:
                return Cursor('H');
            case Key.End:
                return Cursor('F');
            case Key.Insert:
                return Tilde(2);
            case Key.Delete:
                return Tilde(3);
            case Key.PageUp:
                return Tilde(5);
            case Key.PageDown:
                return Tilde(6);
            case Key.F1:
                return Function('P');
            case Key.F2:
                return Function('Q');
            case Key.F3:
                return Function('R');
            case Key.F4:
                return Function('S');
            case Key.F5:
                return Tilde(15);
            case Key.F6:
                return Tilde(17);
            case Key.F7:
                return Tilde(18);
            case Key.F8:
                return Tilde(19);
            case Key.F9:
                return Tilde(20);
            case Key.F10:
                return Tilde(21);
            case Key.F11:
                return Tilde(23);
            case Key.F12:
                return Tilde(24);
            case Key.Enter:
                return alt ? "\x1b\r" : "\r";
            case Key.Back:
                return ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f";
            case Key.Tab:
                return shift ? "\x1b[Z" : "\t";
            case Key.Escape:
                return "\x1b";
        }
        // AltGr (Ctrl+Alt on Windows) types characters: left to text input
        if (ctrl && alt)
        {
            return null;
        }
        if (ctrl)
        {
            return key switch
            {
                >= Key.A and <= Key.Z => ((char)(key - Key.A + 1)).ToString(),
                Key.Space or Key.D2 => "\0",
                Key.OemOpenBrackets => "\x1b",
                Key.OemPipe or Key.OemBackslash => "\x1c",
                Key.OemCloseBrackets => "\x1d",
                Key.D6 => "\x1e",
                Key.OemMinus => "\x1f",
                _ => null,
            };
        }
        if (alt && key is >= Key.A and <= Key.Z)
        {
            var letter = (char)('a' + (key - Key.A));
            return "\x1b" + (shift ? char.ToUpperInvariant(letter) : letter);
        }
        return null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var ctrlShift = mods == (KeyModifiers.Control | KeyModifiers.Shift);
        // copy, paste and scrolling stay with the terminal window
        if ((ctrlShift && e.Key == Key.C) || (mods == KeyModifiers.Control && e.Key == Key.C && HasSelection) || (mods == KeyModifiers.Control && e.Key == Key.Insert))
        {
            e.Handled = true;
            _ = CopyAsync();
            return;
        }
        if ((ctrlShift && e.Key == Key.V) || (mods == KeyModifiers.Shift && e.Key == Key.Insert))
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }
        if (mods == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            ScrollBy(e.Key == Key.PageUp ? Vt.Rows - 1 : -(Vt.Rows - 1));
            return;
        }
        if (KeySequence(e.Key, mods, Vt.ApplicationCursorKeys) is { } sequence)
        {
            e.Handled = true;
            _suppressText = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt);
            ClearSelection();
            ScrollToBottom();
            _owner.Send(sequence);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _suppressText)
        {
            _suppressText = false;
            e.Handled = !string.IsNullOrEmpty(e.Text);
            return;
        }
        var text = new string(e.Text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length > 0)
        {
            e.Handled = true;
            ClearSelection();
            ScrollToBottom();
            _owner.Send(text);
        }
    }

    // ---------------------------------------------------------------- mouse, selection, clipboard

    private bool HasSelection => _selectionStart is { } a && _selectionEnd is { } b && a != b;

    private (int Line, int Column) CellAt(Point point) =>
        (TopLine + Math.Clamp((int)((point.Y - Padding / 2) / _cellHeight), 0, Vt.Rows - 1),
         Math.Clamp((int)((point.X - Padding) / _cellWidth), 0, Vt.Columns - 1));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            // like Windows Terminal: copies a selection, otherwise pastes
            _ = HasSelection ? CopyAsync() : PasteAsync();
            e.Handled = true;
            return;
        }
        if (point.Properties.IsLeftButtonPressed)
        {
            _selectionStart = _selectionEnd = CellAt(point.Position);
            _selecting = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            _selectionEnd = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            if (!HasSelection)
            {
                ClearSelection();
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Vt.IsAlternateScreen)
        {
            // full screen programs scroll themselves: arrow keys
            var arrow = KeySequence(e.Delta.Y > 0 ? Key.Up : Key.Down, KeyModifiers.None, Vt.ApplicationCursorKeys)!;
            _owner.Send(string.Concat(Enumerable.Repeat(arrow, 3)));
        }
        else
        {
            ScrollBy((int)Math.Round(e.Delta.Y * 3));
        }
        e.Handled = true;
    }

    private void ScrollBy(int lines)
    {
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, Vt.ScrollbackCount);
        InvalidateVisual();
    }

    private void ScrollToBottom()
    {
        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }
    }

    private void ClearSelection()
    {
        if (_selectionStart is not null)
        {
            _selectionStart = _selectionEnd = null;
            InvalidateVisual();
        }
    }

    /// <summary>The selected text (lines without trailing blanks).</summary>
    public string SelectedText()
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b)
        {
            return string.Empty;
        }
        var (start, end) = (a.Line, a.Column).CompareTo((b.Line, b.Column)) <= 0 ? (a, b) : (b, a);
        var lines = new List<string>();
        for (var index = start.Line; index <= end.Line && index < Vt.ScrollbackCount + Vt.Rows; index++)
        {
            var line = LineAt(index);
            var from = index == start.Line ? start.Column : 0;
            var to = index == end.Line ? Math.Min(end.Column, line.Count - 1) : line.Count - 1;
            lines.Add(VtScreen.TextOf(line.Skip(from).Take(to - from + 1).ToList()));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task CopyAsync()
    {
        var text = SelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
        ClearSelection();
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await ClipboardExtensions.TryGetTextAsync(clipboard) is not { Length: > 0 } text)
        {
            return;
        }
        text = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        ScrollToBottom();
        _owner.Send(Vt.BracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text);
    }
}
