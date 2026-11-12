using System.Globalization;
using System.Text;

namespace NextCommander.App.Services.Terminal;

[Flags]
public enum VtFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,
    Strike = 64,
}

/// <summary>Colors and attributes of a cell. A color is -1 (default), 0-255 (palette) or 0x1000000 | RGB.</summary>
public readonly record struct VtStyle(int Foreground, int Background, VtFlags Flags)
{
    public static readonly VtStyle Default = new(-1, -1, VtFlags.None);

    public static int Rgb(int r, int g, int b) => 0x1000000 | (r << 16) | (g << 8) | b;
}

/// <summary>One character cell: the code point (0: empty, -1: the right half of a wide character) and its style.</summary>
public readonly record struct VtCell(int CodePoint, VtStyle Style)
{
    public static readonly VtCell Blank = new(0, VtStyle.Default);
}

/// <summary>
/// The screen of a VT100 / xterm compatible terminal: a grid of cells with a cursor, the scrollback of the main screen,
/// the alternate screen of full screen programs, scroll regions and the usual CSI / OSC / ESC sequences (cursor moves,
/// erasing, inserting and deleting, SGR colors with 16, 256 and true colors, DEC private modes). Output of the shell
/// goes in through <see cref="Feed"/>; answers to the program (cursor position, device attributes) come out through
/// <see cref="Reply"/>.
/// </summary>
public sealed class VtScreen
{
    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        Charset,
        Dcs,
        DcsEscape,
    }

    private readonly List<VtCell[]> _scrollback = [];
    private VtCell[][] _main;
    private VtCell[][]? _alternate;
    private State _state;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _osc = new();
    private char _private;
    private char _intermediate;
    private VtStyle _style = VtStyle.Default;
    private (int X, int Y, VtStyle Style) _saved;
    private (int X, int Y, VtStyle Style) _savedMain;
    private bool _wrapPending;
    private int _top;
    private int _bottom;
    private char _highSurrogate;
    private int _lastPrinted = ' ';

    public VtScreen(int columns, int rows, int scrollbackLimit = 5000)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        ScrollbackLimit = scrollbackLimit;
        _main = NewLines(Rows);
        _bottom = Rows - 1;
    }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int ScrollbackLimit { get; }

    public int CursorX { get; private set; }

    public int CursorY { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    /// <summary>DECCKM: arrow keys send ESC O x instead of ESC [ x.</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>Mode 2004: pasted text is wrapped in ESC [200~ ... ESC [201~.</summary>
    public bool BracketedPaste { get; private set; }

    public bool AutoWrap { get; private set; } = true;

    public bool InsertMode { get; private set; }

    /// <summary>A full screen program uses the alternate screen (no scrollback there).</summary>
    public bool IsAlternateScreen => _alternate is not null;

    /// <summary>Window title set by the program (OSC 0 / 2).</summary>
    public string? Title { get; private set; }

    /// <summary>Lines scrolled off the top of the main screen (oldest first).</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Text the terminal sends back to the program (DSR, DA).</summary>
    public event Action<string>? Reply;

    public event Action? TitleChanged;

    /// <summary>The program rang the bell.</summary>
    public event Action? Bell;

    private VtCell[][] Lines => _alternate ?? _main;

    private VtCell[][] NewLines(int count) => Enumerable.Range(0, count).Select(_ => BlankLine()).ToArray();

    private VtCell[] BlankLine() => Enumerable.Repeat(VtCell.Blank, Columns).ToArray();

    private VtCell Erased => new(0, VtStyle.Default with { Background = _style.Background });

    /// <summary>A row of the screen (0 = top).</summary>
    public IReadOnlyList<VtCell> Line(int row) => Lines[row];

    /// <summary>A line of the scrollback (0 = oldest).</summary>
    public IReadOnlyList<VtCell> ScrollbackLine(int index) => _scrollback[index];

    /// <summary>The text of a row without trailing blanks.</summary>
    public static string TextOf(IReadOnlyList<VtCell> line)
    {
        var text = new StringBuilder();
        foreach (var cell in line)
        {
            if (cell.CodePoint > 0)
            {
                text.Append(char.ConvertFromUtf32(cell.CodePoint));
            }
            else if (cell.CodePoint == 0)
            {
                text.Append(' ');
            }
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The screen as text (rows without trailing blanks, trailing empty rows left out).</summary>
    public string ScreenText() => string.Join('\n', Lines.Select(TextOf)).TrimEnd('\n');

    /// <summary>Scrollback and screen as text.</summary>
    public string AllText() => string.Join('\n', _scrollback.Select(TextOf).Concat(Lines.Select(TextOf))).TrimEnd('\n');

    /// <summary>Clears the scrollback and the screen; the cursor goes home.</summary>
    public void Clear()
    {
        _scrollback.Clear();
        for (var i = 0; i < Rows; i++)
        {
            Lines[i] = BlankLine();
        }
        CursorX = CursorY = 0;
        _wrapPending = false;
    }

    /// <summary>New size: lines are cut or padded; when rows go, the top ones move to the scrollback so the cursor stays.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }
        VtCell[] Fit(VtCell[] line)
        {
            var result = new VtCell[columns];
            for (var x = 0; x < columns; x++)
            {
                result[x] = x < line.Length ? line[x] : VtCell.Blank;
            }
            return result;
        }
        VtCell[][] Rebuild(VtCell[][] lines, bool keepCursor)
        {
            var list = lines.Select(Fit).ToList();
            if (rows < list.Count)
            {
                // drop rows below the cursor first, then move rows above it to the scrollback
                var below = keepCursor ? Math.Min(list.Count - 1 - CursorY, list.Count - rows) : 0;
                for (var i = 0; i < below && list.Count > rows && TextOf(list[^1]).Length == 0; i++)
                {
                    list.RemoveAt(list.Count - 1);
                }
                while (list.Count > rows)
                {
                    if (keepCursor && _alternate is null)
                    {
                        AddScrollback(list[0]);
                    }
                    list.RemoveAt(0);
                    if (keepCursor)
                    {
                        CursorY = Math.Max(0, CursorY - 1);
                    }
                }
            }
            while (list.Count < rows)
            {
                list.Add(Enumerable.Repeat(VtCell.Blank, columns).ToArray());
            }
            return [.. list];
        }
        var oldColumns = Columns;
        Columns = columns;
        if (_alternate is not null)
        {
            _main = Rebuild(_main, keepCursor: false);
            _alternate = Rebuild(_alternate, keepCursor: true);
        }
        else
        {
            _main = Rebuild(_main, keepCursor: true);
        }
        if (oldColumns != columns)
        {
            for (var i = 0; i < _scrollback.Count; i++)
            {
                _scrollback[i] = Fit(_scrollback[i]);
            }
        }
        Rows = rows;
        _top = 0;
        _bottom = Rows - 1;
        CursorX = Math.Min(CursorX, Columns - 1);
        CursorY = Math.Min(CursorY, Rows - 1);
        _wrapPending = false;
    }

    private void AddScrollback(VtCell[] line)
    {
        _scrollback.Add(line);
        if (_scrollback.Count > ScrollbackLimit)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
        }
    }

    // ---------------------------------------------------------------- parser

    /// <summary>Output of the program (already decoded from UTF-8).</summary>
    public void Feed(string text)
    {
        foreach (var c in text)
        {
            Feed(c);
        }
    }

    private void Feed(char c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                break;
            case State.Escape:
                Escape(c);
                break;
            case State.EscapeIntermediate:
                _state = State.Ground; // ESC # 8, ESC % G ...: nothing to do
                break;
            case State.Charset:
                _state = State.Ground; // ESC ( B: character sets are not switched
                break;
            case State.Csi:
                Csi(c);
                break;
            case State.Osc:
                if (c == '\a')
                {
                    EndOsc();
                }
                else if (c == '\x1b')
                {
                    _state = State.OscEscape;
                }
                else if (_osc.Length < 4096)
                {
                    _osc.Append(c);
                }
                break;
            case State.OscEscape:
                EndOsc(); // ESC \ (string terminator)
                if (c != '\\')
                {
                    Feed(c);
                }
                break;
            case State.Dcs:
                if (c == '\x1b')
                {
                    _state = State.DcsEscape;
                }
                else if (c == '\a')
                {
                    _state = State.Ground;
                }
                break;
            case State.DcsEscape:
                _state = c == '\\' ? State.Ground : State.Dcs;
                break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                return;
            case '\a':
                Bell?.Invoke();
                return;
            case '\b':
                if (CursorX > 0)
                {
                    CursorX--;
                }
                _wrapPending = false;
                return;
            case '\t':
                CursorX = Math.Min(Columns - 1, (CursorX / 8 + 1) * 8);
                _wrapPending = false;
                return;
            case '\n' or '\v' or '\f':
                LineFeed();
                return;
            case '\r':
                CursorX = 0;
                _wrapPending = false;
                return;
            case '\x0e' or '\x0f' or '\0' or '\x7f':
                return;
        }
        if (c < ' ')
        {
            return;
        }
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }
        var codePoint = c;
        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate == 0)
            {
                return;
            }
            Print(char.ConvertToUtf32(_highSurrogate, c));
            _highSurrogate = '\0';
            return;
        }
        _highSurrogate = '\0';
        Print(codePoint);
    }

    /// <summary>Columns a character takes: 0 (combining marks), 2 (wide East Asian characters, emoji) or 1.</summary>
    public static int WidthOf(int codePoint)
    {
        if (codePoint < 0x300)
        {
            return 1;
        }
        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }
        return codePoint switch
        {
            >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                or >= 0xA000 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60
                or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1F64F or >= 0x1F900 and <= 0x1F9FF or >= 0x1FA70 and <= 0x1FAFF
                or >= 0x20000 and <= 0x3FFFD => 2,
            _ => 1,
        };
    }

    private void Print(int codePoint)
    {
        var width = WidthOf(codePoint);
        if (width == 0)
        {
            return; // combining marks are not drawn separately
        }
        if (_wrapPending)
        {
            if (AutoWrap)
            {
                CursorX = 0;
                LineFeed();
            }
            _wrapPending = false;
        }
        if (width == 2 && CursorX == Columns - 1)
        {
            if (AutoWrap)
            {
                Lines[CursorY][CursorX] = Erased;
                CursorX = 0;
                LineFeed();
            }
            else
            {
                return;
            }
        }
        var line = Lines[CursorY];
        if (InsertMode)
        {
            Array.Copy(line, CursorX, line, CursorX + width, Columns - CursorX - width);
        }
        line[CursorX] = new VtCell(codePoint, _style);
        if (width == 2)
        {
            line[CursorX + 1] = new VtCell(-1, _style);
        }
        _lastPrinted = codePoint;
        if (CursorX + width >= Columns)
        {
            CursorX = Columns - 1;
            _wrapPending = true;
        }
        else
        {
            CursorX += width;
        }
    }

    private void LineFeed()
    {
        if (CursorY == _bottom)
        {
            ScrollUp(1);
        }
        else if (CursorY < Rows - 1)
        {
            CursorY++;
        }
        _wrapPending = false;
    }

    private void ReverseIndex()
    {
        if (CursorY == _top)
        {
            ScrollDown(1);
        }
        else if (CursorY > 0)
        {
            CursorY--;
        }
        _wrapPending = false;
    }

    /// <summary>Lines of the scroll region move up; the top one goes to the scrollback (main screen, full region).</summary>
    private void ScrollUp(int count)
    {
        var lines = Lines;
        for (var n = 0; n < Math.Min(count, _bottom - _top + 1); n++)
        {
            if (_alternate is null && _top == 0)
            {
                AddScrollback(lines[0]);
            }
            for (var y = _top; y < _bottom; y++)
            {
                lines[y] = lines[y + 1];
            }
            lines[_bottom] = Enumerable.Repeat(Erased, Columns).ToArray();
        }
    }

    private void ScrollDown(int count)
    {
        var lines = Lines;
        for (var n = 0; n < Math.Min(count, _bottom - _top + 1); n++)
        {
            for (var y = _bottom; y > _top; y--)
            {
                lines[y] = lines[y - 1];
            }
            lines[_top] = Enumerable.Repeat(Erased, Columns).ToArray();
        }
    }

    private void Escape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _params.Clear();
                _private = '\0';
                _intermediate = '\0';
                _state = State.Csi;
                break;
            case ']':
                _osc.Clear();
                _state = State.Osc;
                break;
            case 'P' or 'X' or '^' or '_':
                _state = State.Dcs; // device control and other strings: skipped
                break;
            case '(' or ')' or '*' or '+' or '-' or '.' or '/':
                _state = State.Charset;
                break;
            case '#' or '%' or ' ':
                _state = State.EscapeIntermediate;
                break;
            case '7':
                _saved = (CursorX, CursorY, _style);
                break;
            case '8':
                (CursorX, CursorY, _style) = _saved;
                _wrapPending = false;
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorX = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
            case '=' or '>':
                break; // keypad modes
            case '\x1b':
                _state = State.Escape;
                break;
        }
    }

    private void Reset()
    {
        _alternate = null;
        _style = VtStyle.Default;
        _main = NewLines(Rows);
        _top = 0;
        _bottom = Rows - 1;
        CursorX = CursorY = 0;
        CursorVisible = true;
        ApplicationCursorKeys = BracketedPaste = InsertMode = false;
        AutoWrap = true;
        _wrapPending = false;
    }

    private void EndOsc()
    {
        _state = State.Ground;
        var text = _osc.ToString();
        var semicolon = text.IndexOf(';', StringComparison.Ordinal);
        if (semicolon > 0 && text[..semicolon] is "0" or "2")
        {
            Title = text[(semicolon + 1)..];
            TitleChanged?.Invoke();
        }
    }

    private void Csi(char c)
    {
        if (c is >= '0' and <= '9' or ';' or ':')
        {
            _params.Append(c == ':' ? ';' : c);
            return;
        }
        if (c is '?' or '>' or '<' or '=' && _params.Length == 0)
        {
            _private = c;
            return;
        }
        if (c is >= ' ' and <= '/')
        {
            _intermediate = c;
            return;
        }
        if (c == '\x1b')
        {
            _state = State.Escape; // cancelled
            return;
        }
        if (c < ' ')
        {
            Ground(c); // a control character inside a sequence still acts
            return;
        }
        _state = State.Ground;
        var p = _params.ToString().Split(';').Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1).ToArray();
        int P(int index, int fallback = 1) => index < p.Length && p[index] > 0 ? p[index] : fallback;
        int Z(int index) => index < p.Length && p[index] > 0 ? p[index] : 0;
        if (_intermediate != '\0')
        {
            return; // DECSCUSR (cursor shape), DECSTR ...: not used
        }
        if (_private == '?')
        {
            if (c is 'h' or 'l')
            {
                foreach (var mode in p)
                {
                    PrivateMode(mode, c == 'h');
                }
            }
            return;
        }
        if (_private != '\0')
        {
            if (_private == '>' && c == 'c')
            {
                Reply?.Invoke("\x1b[>0;10;1c");
            }
            return;
        }
        var lines = Lines;
        switch (c)
        {
            case '@':
                InsertCells(P(0));
                break;
            case 'A':
                CursorY = Math.Max(CursorY >= _top ? _top : 0, CursorY - P(0));
                _wrapPending = false;
                break;
            case 'B':
                CursorY = Math.Min(CursorY <= _bottom ? _bottom : Rows - 1, CursorY + P(0));
                _wrapPending = false;
                break;
            case 'C' or 'a':
                CursorX = Math.Min(Columns - 1, CursorX + P(0));
                _wrapPending = false;
                break;
            case 'D':
                CursorX = Math.Max(0, CursorX - P(0));
                _wrapPending = false;
                break;
            case 'E':
                CursorY = Math.Min(Rows - 1, CursorY + P(0));
                CursorX = 0;
                _wrapPending = false;
                break;
            case 'F':
                CursorY = Math.Max(0, CursorY - P(0));
                CursorX = 0;
                _wrapPending = false;
                break;
            case 'G' or '`':
                CursorX = Math.Clamp(P(0) - 1, 0, Columns - 1);
                _wrapPending = false;
                break;
            case 'H' or 'f':
                CursorY = Math.Clamp(P(0) - 1, 0, Rows - 1);
                CursorX = Math.Clamp(P(1) - 1, 0, Columns - 1);
                _wrapPending = false;
                break;
            case 'I':
                for (var i = 0; i < P(0); i++)
                {
                    CursorX = Math.Min(Columns - 1, (CursorX / 8 + 1) * 8);
                }
                break;
            case 'Z':
                for (var i = 0; i < P(0); i++)
                {
                    CursorX = Math.Max(0, (CursorX - 1) / 8 * 8);
                }
                break;
            case 'J':
                EraseDisplay(Z(0));
                break;
            case 'K':
                EraseLine(Z(0));
                break;
            case 'L':
                if (CursorY >= _top && CursorY <= _bottom)
                {
                    var top = _top;
                    _top = CursorY;
                    ScrollDown(P(0));
                    _top = top;
                }
                break;
            case 'M':
                if (CursorY >= _top && CursorY <= _bottom)
                {
                    var top = _top;
                    _top = CursorY;
                    // deleted lines do not go to the scrollback
                    for (var n = 0; n < Math.Min(P(0), _bottom - _top + 1); n++)
                    {
                        for (var y = _top; y < _bottom; y++)
                        {
                            lines[y] = lines[y + 1];
                        }
                        lines[_bottom] = Enumerable.Repeat(Erased, Columns).ToArray();
                    }
                    _top = top;
                }
                break;
            case 'P':
                DeleteCells(P(0));
                break;
            case 'S':
                ScrollUp(P(0));
                break;
            case 'T':
                ScrollDown(P(0));
                break;
            case 'X':
                for (var x = CursorX; x < Math.Min(Columns, CursorX + P(0)); x++)
                {
                    lines[CursorY][x] = Erased;
                }
                break;
            case 'b':
                for (var i = 0; i < Math.Min(P(0), 1000); i++)
                {
                    Print(_lastPrinted);
                }
                break;
            case 'c':
                Reply?.Invoke("\x1b[?1;2c"); // VT100 with advanced video
                break;
            case 'd':
                CursorY = Math.Clamp(P(0) - 1, 0, Rows - 1);
                _wrapPending = false;
                break;
            case 'e':
                CursorY = Math.Min(Rows - 1, CursorY + P(0));
                _wrapPending = false;
                break;
            case 'h' or 'l':
                if (p.Contains(4))
                {
                    InsertMode = c == 'h';
                }
                break;
            case 'm':
                Sgr(p);
                break;
            case 'n':
                if (Z(0) == 6)
                {
                    Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R");
                }
                else if (Z(0) == 5)
                {
                    Reply?.Invoke("\x1b[0n");
                }
                break;
            case 'r':
                var first = P(0) - 1;
                var last = P(1, Rows) - 1;
                if (first < last && last < Rows)
                {
                    _top = first;
                    _bottom = last;
                    CursorX = 0;
                    CursorY = 0;
                    _wrapPending = false;
                }
                break;
            case 's':
                _saved = (CursorX, CursorY, _style);
                break;
            case 'u':
                (CursorX, CursorY, _style) = _saved;
                _wrapPending = false;
                break;
        }
    }

    private void PrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 7:
                AutoWrap = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 2004:
                BracketedPaste = on;
                break;
            case 47 or 1047 or 1049:
                if (on && _alternate is null)
                {
                    if (mode == 1049)
                    {
                        _savedMain = (CursorX, CursorY, _style);
                    }
                    _alternate = NewLines(Rows);
                    if (mode == 1049)
                    {
                        CursorX = CursorY = 0;
                    }
                }
                else if (!on && _alternate is not null)
                {
                    _alternate = null;
                    if (mode == 1049)
                    {
                        (CursorX, CursorY, _style) = _savedMain;
                    }
                }
                _top = 0;
                _bottom = Rows - 1;
                _wrapPending = false;
                break;
        }
    }

    private void InsertCells(int count)
    {
        var line = Lines[CursorY];
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(line, CursorX, line, CursorX + count, Columns - CursorX - count);
        for (var x = CursorX; x < CursorX + count; x++)
        {
            line[x] = Erased;
        }
    }

    private void DeleteCells(int count)
    {
        var line = Lines[CursorY];
        count = Math.Min(count, Columns - CursorX);
        Array.Copy(line, CursorX + count, line, CursorX, Columns - CursorX - count);
        for (var x = Columns - count; x < Columns; x++)
        {
            line[x] = Erased;
        }
        _wrapPending = false;
    }

    private void EraseLine(int mode)
    {
        var line = Lines[CursorY];
        var (from, to) = mode switch
        {
            1 => (0, CursorX + 1),
            2 => (0, Columns),
            _ => (CursorX, Columns),
        };
        for (var x = from; x < Math.Min(to, Columns); x++)
        {
            line[x] = Erased;
        }
        _wrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        var lines = Lines;
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var y = CursorY + 1; y < Rows; y++)
                {
                    lines[y] = Enumerable.Repeat(Erased, Columns).ToArray();
                }
                break;
            case 1:
                EraseLine(1);
                for (var y = 0; y < CursorY; y++)
                {
                    lines[y] = Enumerable.Repeat(Erased, Columns).ToArray();
                }
                break;
            case 2:
                for (var y = 0; y < Rows; y++)
                {
                    lines[y] = Enumerable.Repeat(Erased, Columns).ToArray();
                }
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void Sgr(int[] p)
    {
        if (p.Length == 0)
        {
            _style = VtStyle.Default;
            return;
        }
        for (var i = 0; i < p.Length; i++)
        {
            var code = Math.Max(0, p[i]);
            switch (code)
            {
                case 0:
                    _style = VtStyle.Default;
                    break;
                case 1:
                    _style = _style with { Flags = _style.Flags | VtFlags.Bold };
                    break;
                case 2:
                    _style = _style with { Flags = _style.Flags | VtFlags.Dim };
                    break;
                case 3:
                    _style = _style with { Flags = _style.Flags | VtFlags.Italic };
                    break;
                case 4:
                    _style = _style with { Flags = _style.Flags | VtFlags.Underline };
                    break;
                case 7:
                    _style = _style with { Flags = _style.Flags | VtFlags.Inverse };
                    break;
                case 8:
                    _style = _style with { Flags = _style.Flags | VtFlags.Hidden };
                    break;
                case 9:
                    _style = _style with { Flags = _style.Flags | VtFlags.Strike };
                    break;
                case 21 or 22:
                    _style = _style with { Flags = _style.Flags & ~(VtFlags.Bold | VtFlags.Dim) };
                    break;
                case 23:
                    _style = _style with { Flags = _style.Flags & ~VtFlags.Italic };
                    break;
                case 24:
                    _style = _style with { Flags = _style.Flags & ~VtFlags.Underline };
                    break;
                case 27:
                    _style = _style with { Flags = _style.Flags & ~VtFlags.Inverse };
                    break;
                case 28:
                    _style = _style with { Flags = _style.Flags & ~VtFlags.Hidden };
                    break;
                case 29:
                    _style = _style with { Flags = _style.Flags & ~VtFlags.Strike };
                    break;
                case >= 30 and <= 37:
                    _style = _style with { Foreground = code - 30 };
                    break;
                case 38:
                    _style = _style with { Foreground = ExtendedColor(p, ref i) };
                    break;
                case 39:
                    _style = _style with { Foreground = -1 };
                    break;
                case >= 40 and <= 47:
                    _style = _style with { Background = code - 40 };
                    break;
                case 48:
                    _style = _style with { Background = ExtendedColor(p, ref i) };
                    break;
                case 49:
                    _style = _style with { Background = -1 };
                    break;
                case >= 90 and <= 97:
                    _style = _style with { Foreground = code - 90 + 8 };
                    break;
                case >= 100 and <= 107:
                    _style = _style with { Background = code - 100 + 8 };
                    break;
            }
        }
    }

    /// <summary>38;5;n (palette) or 38;2;r;g;b (true color); also the colon forms (38:2::r:g:b).</summary>
    private static int ExtendedColor(int[] p, ref int i)
    {
        if (i + 1 >= p.Length)
        {
            return -1;
        }
        if (p[i + 1] == 5 && i + 2 < p.Length)
        {
            i += 2;
            return Math.Clamp(p[i], 0, 255);
        }
        if (p[i + 1] == 2)
        {
            // 38;2;r;g;b or 38:2:colorspace:r:g:b
            var start = i + 5 < p.Length && p[i + 2] == -1 ? i + 3 : i + 2; // an empty color space (colon form)
            if (start + 2 < p.Length)
            {
                i = start + 2;
                return VtStyle.Rgb(Math.Clamp(p[start], 0, 255), Math.Clamp(p[start + 1], 0, 255), Math.Clamp(p[start + 2], 0, 255));
            }
        }
        i = p.Length;
        return -1;
    }
}
