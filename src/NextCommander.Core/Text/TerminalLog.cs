using System.Globalization;
using System.Text;

namespace NextCommander.Core.Text;

/// <summary>
/// The output of a terminal tab written to a text file (MobaXterm's session logs, user's request, 21 November 2026):
/// plain text without the terminal's escape sequences (colors, cursor movement, titles), line ends as "\n", a header
/// with the session and the time. Written from any thread; a sequence split between two writes is still removed.
/// </summary>
public sealed class TerminalLog : IDisposable
{
    private enum State
    {
        Text,
        Escape,
        Csi,
        String,
        StringEscape,
        Charset,
    }

    private readonly Lock _lock = new();
    private readonly StreamWriter _writer;
    private State _state;
    private bool _carriageReturn;

    /// <summary>The folder of the terminal logs when the settings name none (logs/terminal in the settings folder).</summary>
    public static string? DefaultFolder { get; set; }

    private TerminalLog(string path, string title)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _writer.Write($"===== {title}: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} =====\n");
    }

    /// <summary>The file written to.</summary>
    public string Path { get; }

    /// <summary>Starts a log in <paramref name="folder"/> (empty: <see cref="DefaultFolder"/>) named after the session and the time.</summary>
    public static TerminalLog Start(string? folder, string title, DateTime? now = null)
    {
        var dir = string.IsNullOrWhiteSpace(folder) ? DefaultFolder ?? System.IO.Path.GetTempPath() : Environment.ExpandEnvironmentVariables(folder.Trim());
        var safe = new string([.. title.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]).Trim();
        var stamp = (now ?? DateTime.Now).ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        return new TerminalLog(System.IO.Path.Combine(dir, $"{(safe.Length > 0 ? safe : "terminal")} {stamp}.log"), title);
    }

    /// <summary>Output of the session (any thread).</summary>
    public void Write(string text)
    {
        lock (_lock)
        {
            var plain = Strip(text);
            if (plain.Length > 0)
            {
                try
                {
                    _writer.Write(plain);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // the disk is full or the log was closed meanwhile: the session goes on
                }
            }
        }
    }

    /// <summary>The text without escape sequences and control characters ("\r\n" and lone "\r" become "\n", tabs stay).</summary>
    private string Strip(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (_state)
            {
                case State.Text:
                    if (c == '\u001b')
                    {
                        _state = State.Escape;
                    }
                    else if (c == '\u009b')
                    {
                        _state = State.Csi;
                    }
                    else if (c == '\r')
                    {
                        _carriageReturn = true;
                    }
                    else if (c == '\n')
                    {
                        result.Append('\n');
                        _carriageReturn = false;
                    }
                    else if (c == '\t' || c >= ' ' && c != '\u007f' && c is < '\u0080' or > '\u009f')
                    {
                        if (_carriageReturn)
                        {
                            result.Append('\n'); // a line written over (progress): kept as lines of its own
                        }
                        result.Append(c);
                        _carriageReturn = false;
                    }
                    break;
                case State.Escape:
                    _state = c switch
                    {
                        '[' => State.Csi,
                        ']' or 'P' or 'X' or '^' or '_' => State.String,
                        '(' or ')' or '*' or '+' or '#' or '%' => State.Charset,
                        _ => State.Text, // a two-character sequence (ESC 7, ESC =, ESC M ...)
                    };
                    break;
                case State.Csi:
                    if (c is >= '@' and <= '~')
                    {
                        _state = State.Text;
                    }
                    break;
                case State.String:
                    if (c == '\u0007')
                    {
                        _state = State.Text;
                    }
                    else if (c == '\u001b')
                    {
                        _state = State.StringEscape;
                    }
                    break;
                case State.StringEscape:
                    _state = c == '\\' ? State.Text : State.String;
                    break;
                case State.Charset:
                    _state = State.Text;
                    break;
            }
        }
        return result.ToString();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _writer.Write($"\n===== {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} =====\n");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // going anyway
            }
            _writer.Dispose();
        }
    }
}
