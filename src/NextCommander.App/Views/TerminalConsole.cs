using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.App.Services;

namespace NextCommander.App.Views;

/// <summary>
/// One tab of the internal terminal: a shell (<see cref="TerminalSession"/>) and one text area that shows its output
/// and takes the command right behind the prompt. Everything before <see cref="InputStart"/> is output and cannot be
/// changed; Enter sends what follows it.
/// </summary>
public sealed partial class TerminalConsole : TerminalTab
{
    private const int MaxOutput = 400_000;
    private readonly TextBox _console;
    private readonly StringBuilder _pending = new();
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private bool _flushQueued;
    private TerminalSession? _session;

    public TerminalConsole(TerminalSession.ShellInfo shell, Func<string?> currentFolder)
        : base(shell, currentFolder)
    {
        _console = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12,
            Margin = new Thickness(4, 0, 4, 4),
            Padding = new Thickness(6, 4),
            BorderThickness = new Thickness(0),
            IsUndoEnabled = false,
            Text = string.Empty,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_console, ScrollBarVisibility.Auto);
        Avalonia.Automation.AutomationProperties.SetName(_console, "Terminal");
        _console.AddHandler(KeyDownEvent, OnConsoleKeyDown, RoutingStrategies.Tunnel);
        _console.AddHandler(TextInputEvent, (_, _) => KeepCaretInInput(), RoutingStrategies.Tunnel);
        _console.AddHandler(TextBox.PastingFromClipboardEvent, (_, _) => KeepCaretInInput(), RoutingStrategies.Tunnel);
        _console[!TextBox.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FmListerBackgroundBrush");
        Child = _console;
    }

    /// <summary>Everything shown: output and the command being typed.</summary>
    public override string OutputText => _console.Text ?? string.Empty;

    private int _findAt = -1;

    /// <summary>See <see cref="TerminalTab.Find"/>: the match is selected in the text.</summary>
    public override (int Current, int Count) Find(string text, bool matchCase, int direction)
    {
        var all = OutputText;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<int>();
        if (text.Length > 0)
        {
            for (var at = all.IndexOf(text, comparison); at >= 0; at = all.IndexOf(text, at + text.Length, comparison))
            {
                matches.Add(at);
            }
        }
        if (matches.Count == 0)
        {
            _findAt = -1;
            return (0, 0);
        }
        var index = direction == 0 || _findAt < 0 ? matches.Count - 1
            : direction < 0 ? matches.FindLastIndex(m => m < _findAt) is >= 0 and var before ? before : matches.Count - 1
            : matches.FindIndex(m => m > _findAt) is >= 0 and var after ? after : 0;
        _findAt = matches[index];
        _console.CaretIndex = _findAt + text.Length; // scrolls to it (moving the caret drops a selection: first)
        _console.SelectionStart = _findAt;
        _console.SelectionEnd = _findAt + text.Length;
        return (index + 1, matches.Count);
    }

    public override void ClearFind()
    {
        _findAt = -1;
        _console.SelectionStart = _console.SelectionEnd = OutputText.Length;
    }

    /// <summary>Where the command being typed begins (the output ends).</summary>
    public int InputStart { get; private set; }

    /// <summary>The command typed behind the prompt (not sent yet).</summary>
    public string CurrentInput
    {
        get => OutputText.Length >= InputStart ? OutputText[InputStart..] : string.Empty;
        set
        {
            _console.Text = OutputText[..InputStart] + value;
            _console.CaretIndex = _console.Text.Length;
        }
    }

    public TerminalSession? Session => _session;

    /// <summary>The text area (output and input).</summary>
    public TextBox Console => _console;

    /// <summary>Starts the shell if none runs and puts the focus behind the prompt.</summary>
    public override void Activate()
    {
        if (_session is null || _session.HasExited)
        {
            StartShell();
        }
        Dispatcher.UIThread.Post(() =>
        {
            _console.Focus();
            _console.CaretIndex = OutputText.Length;
        }, DispatcherPriority.Input);
    }

    private void StartShell()
    {
        _session?.Dispose();
        var folder = CurrentFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        StartFolder = folder;
        try
        {
            var session = TerminalSession.Start(folder, Shell);
            session.Output += OnOutput;
            session.Exited += code =>
            {
                OnOutput($"\n[{session.ShellName}: {code}]\n");
                if (ReferenceEquals(session, _session))
                {
                    Dispatcher.UIThread.Post(RaiseShellExited);
                }
            };
            _session = session;
            if (!session.EchoesInput)
            {
                OnOutput($"{folder}\n{session.Prompt}");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _session = null;
            OnOutput(ex.Message + "\n");
        }
    }

    /// <summary>Stops the running program: the shell is ended and started again (in the folder of the active panel).</summary>
    public override void Restart()
    {
        var old = _session;
        _session = null; // its exit is not "the shell ended"
        old?.Dispose();
        CurrentInput = string.Empty;
        OnOutput("^C\n");
        StartShell();
        _console.Focus();
    }

    /// <summary>Removes the output; the command being typed stays.</summary>
    public override void Clear()
    {
        lock (_pending)
        {
            _pending.Clear();
        }
        var input = CurrentInput;
        InputStart = 0;
        _console.Text = input;
        _console.CaretIndex = input.Length;
        _console.Focus();
    }

    /// <summary>Runs a command line in the shell, as if typed behind the prompt and Enter pressed.</summary>
    public override void Run(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }
        CurrentInput = line;
        Submit();
    }

    /// <summary>Enter: sends the command behind the prompt.</summary>
    private void Submit()
    {
        if (_session is null || _session.HasExited)
        {
            StartShell();
        }
        var line = CurrentInput.Replace("\r\n", "\n", StringComparison.Ordinal);
        _historyIndex = -1;
        if (line.Trim().Length > 0)
        {
            _history.Remove(line);
            _history.Insert(0, line);
        }
        if (_session is not { } session)
        {
            return;
        }
        if (session.EchoesInput)
        {
            CurrentInput = string.Empty; // the shell writes the command after its prompt itself
        }
        else
        {
            var prompt = OutputText[..InputStart].EndsWith(session.Prompt, StringComparison.Ordinal) ? string.Empty : session.Prompt;
            AppendOutput(prompt + line + "\n", replaceInput: true);
            // the next prompt once the command has written its output
            _ = PromptLaterAsync(session);
        }
        session.Send(line);
        RaiseTyped(line + "\r");
    }

    private readonly StringBuilder _received = new();

    /// <summary>A saved command: put on the command line, run when it ends with Enter.</summary>
    public override void TypeText(string text)
    {
        if (text.EndsWith('\r'))
        {
            Run(CurrentInput + text.TrimEnd('\r'));
        }
        else
        {
            CurrentInput += text;
            _console.CaretIndex = OutputText.Length;
        }
        _console.Focus();
    }

    /// <summary>Typed in another tab ("Type in all"): whole lines run here; Ctrl+C stops; other keys are left out.</summary>
    public override void Receive(string text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r':
                    Run(_received.ToString());
                    _received.Clear();
                    break;
                case '\u0003':
                    _received.Clear();
                    Restart();
                    break;
                case '\u007f' or '\b':
                    if (_received.Length > 0)
                    {
                        _received.Length--;
                    }
                    break;
                case '\u001b':
                    _received.Clear(); // arrows, history: no line of this console
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        _received.Append(c);
                    }
                    break;
            }
        }
    }

    /// <summary>Shells without their own prompt: shows the prompt again when the output has been quiet for a moment.</summary>
    private async Task PromptLaterAsync(TerminalSession session)
    {
        var length = -1;
        while (ReferenceEquals(session, _session) && !session.HasExited)
        {
            await Task.Delay(400).ConfigureAwait(true);
            lock (_pending)
            {
                if (_pending.Length > 0 || _flushQueued)
                {
                    continue;
                }
            }
            if (InputStart == length)
            {
                if (!OutputText[..InputStart].EndsWith(session.Prompt, StringComparison.Ordinal))
                {
                    AppendOutput((OutputText[..InputStart].EndsWith('\n') || InputStart == 0 ? string.Empty : "\n") + session.Prompt, replaceInput: false);
                }
                return;
            }
            length = InputStart;
        }
    }

    /// <summary>Keeps typing, deleting and pasting behind the prompt.</summary>
    private void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var caret = _console.CaretIndex;
        var selectionStart = Math.Min(_console.SelectionStart, _console.SelectionEnd);
        var hasSelection = _console.SelectionStart != _console.SelectionEnd;
        switch (e.Key)
        {
            case Key.F when mods == (KeyModifiers.Control | KeyModifiers.Shift):
                e.Handled = true;
                RequestFind();
                break;
            case Key.Enter when mods is KeyModifiers.None or KeyModifiers.Shift:
                e.Handled = true;
                Submit();
                break;
            case Key.Up when mods == KeyModifiers.None:
                e.Handled = true;
                if (_history.Count > 0)
                {
                    _historyIndex = Math.Min(_historyIndex + 1, _history.Count - 1);
                    CurrentInput = _history[_historyIndex];
                }
                break;
            case Key.Down when mods == KeyModifiers.None:
                e.Handled = true;
                _historyIndex = Math.Max(_historyIndex - 1, -1);
                CurrentInput = _historyIndex >= 0 ? _history[_historyIndex] : string.Empty;
                break;
            case Key.L when mods == KeyModifiers.Control:
                e.Handled = true;
                Clear();
                break;
            case Key.C when mods == KeyModifiers.Control && !hasSelection:
                e.Handled = true;
                Restart();
                break;
            case Key.Escape:
                e.Handled = true;
                CurrentInput = string.Empty;
                break;
            case Key.Home when mods is KeyModifiers.None or KeyModifiers.Shift:
                e.Handled = true;
                if (mods == KeyModifiers.Shift)
                {
                    _console.SelectionStart = caret;
                    _console.SelectionEnd = InputStart;
                }
                else
                {
                    _console.CaretIndex = InputStart;
                }
                break;
            case Key.Back:
                if ((!hasSelection && caret <= InputStart) || (hasSelection && selectionStart < InputStart))
                {
                    e.Handled = true;
                    KeepCaretInInput();
                }
                break;
            case Key.Delete:
                if (caret < InputStart || (hasSelection && selectionStart < InputStart))
                {
                    e.Handled = true;
                    KeepCaretInInput();
                }
                break;
            case Key.Left when mods == KeyModifiers.None && caret <= InputStart:
                e.Handled = true;
                break;
            case Key.X when mods == KeyModifiers.Control && hasSelection && selectionStart < InputStart:
                e.Handled = true; // output cannot be cut (Ctrl+C copies it)
                break;
            case Key.V when mods == KeyModifiers.Control:
            case Key.Insert when mods == KeyModifiers.Shift:
                KeepCaretInInput();
                break;
        }
    }

    /// <summary>Typing with the caret (or a selection) in the output: the text goes to the end of the command.</summary>
    private void KeepCaretInInput()
    {
        var start = Math.Min(_console.SelectionStart, _console.SelectionEnd);
        if (_console.CaretIndex < InputStart || start < InputStart)
        {
            _console.SelectionStart = _console.SelectionEnd = OutputText.Length;
            _console.CaretIndex = OutputText.Length;
        }
    }

    /// <summary>Collects output (any thread) and shows it on the UI thread in batches.</summary>
    private void OnOutput(string text)
    {
        LogOutput(text);
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
        AppendOutput(Clean(text), replaceInput: false);
    }

    /// <summary>Puts output before the command being typed (or, <paramref name="replaceInput"/>, in its place).</summary>
    private void AppendOutput(string text, bool replaceInput)
    {
        if (text.Length == 0 && !replaceInput)
        {
            return;
        }
        var all = OutputText;
        var input = replaceInput ? string.Empty : CurrentInput;
        var caretInInput = Math.Max(0, _console.CaretIndex - InputStart);
        var output = all[..InputStart] + text;
        if (output.Length > MaxOutput)
        {
            output = output[^MaxOutput..];
        }
        InputStart = output.Length;
        _console.Text = output + input;
        _console.CaretIndex = InputStart + Math.Min(caretInInput, input.Length); // keeps the end in view
    }

    /// <summary>Removes terminal escape sequences (colors, cursor moves) and carriage returns.</summary>
    internal static string Clean(string text) => AnsiPattern().Replace(text, string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiPattern();

    /// <summary>Ends the shell (the tab was closed or the window closes).</summary>
    public override void SetFont(string family, double size)
    {
        _console.FontFamily = new FontFamily(family);
        _console.FontSize = size;
    }

    public override void Stop()
    {
        var old = _session;
        _session = null;
        old?.Dispose();
    }
}
