using Avalonia.Controls;
using NextCommander.App.Services;

namespace NextCommander.App.Views;

/// <summary>
/// One tab of the internal terminal: a shell either on a pseudo terminal with a real terminal screen
/// (<see cref="EmulatedTerminal"/>) or with redirected input and output in a text area (<see cref="TerminalConsole"/>).
/// </summary>
public abstract class TerminalTab : Border
{
    protected TerminalTab(TerminalSession.ShellInfo shell, Func<string?> currentFolder)
    {
        Shell = shell;
        CurrentFolder = currentFolder;
    }

    /// <summary>The shell ended by itself (exit); the tab can be closed.</summary>
    public event EventHandler? ShellExited;

    public TerminalSession.ShellInfo Shell { get; }

    /// <summary>The local folder of the active panel (for a new shell and "Panel folder").</summary>
    public Func<string?> CurrentFolder { get; }

    /// <summary>Tab title: the shell's shown name.</summary>
    public virtual string Title => Shell.DisplayName;

    /// <summary>A session on another computer (Telnet): no local folder, not kept with the shells between starts.</summary>
    public virtual bool IsRemote => false;

    /// <summary>The connection of a remote session (SSH, Telnet ...); null for a local shell.</summary>
    public Core.Config.RemoteConnection? Connection { get; init; }

    /// <summary>The folder the shell reported last (OSC 7), null when it reports none.</summary>
    public virtual string? WorkingDirectory => null;

    /// <summary>What the terminal shows as text (output and, for the line console, the command being typed).</summary>
    public abstract string OutputText { get; }

    /// <summary>The folder the shell started in (the panel's folder, else the user's home).</summary>
    public string? StartFolder { get; protected set; }

    /// <summary>Starts the shell if none runs and takes the keyboard focus.</summary>
    public abstract void Activate();

    /// <summary>Stops the running program: the shell is ended and started again (in the folder of the active panel).</summary>
    public abstract void Restart();

    /// <summary>Removes the output.</summary>
    public abstract void Clear();

    /// <summary>Runs a command line in the shell, as if typed and Enter pressed.</summary>
    public abstract void Run(string line);

    /// <summary>Ends the shell (the tab was closed or the window closes).</summary>
    public abstract void Stop();

    /// <summary>The font of the terminal text (a fixed-width font; a list falls back in order).</summary>
    public abstract void SetFont(string family, double size);

    /// <summary>Changes the shell's folder to the folder of the active panel.</summary>
    public void GoToPanelFolder()
    {
        if (!IsRemote && CurrentFolder() is { } folder)
        {
            Run(TerminalSession.ChangeDirectoryCommand(Shell, folder));
        }
    }

    protected void RaiseShellExited() => ShellExited?.Invoke(this, EventArgs.Empty);

    private volatile Core.Text.TerminalLog? _log;

    /// <summary>The log file the output goes to (null: not logged).</summary>
    public Core.Text.TerminalLog? Log => _log;

    /// <summary>Logging started or stopped.</summary>
    public event EventHandler? LogChanged;

    /// <summary>
    /// Writes the output from now on to a new file in <paramref name="folder"/> (empty: logs/terminal in the settings
    /// folder); false when the file cannot be made (the reason in <paramref name="error"/>).
    /// </summary>
    public bool StartLog(string? folder, out string? error)
    {
        error = null;
        if (_log is not null)
        {
            return true;
        }
        try
        {
            _log = Core.Text.TerminalLog.Start(folder, Connection is { } c ? Core.Net.ConnectionLog.NameOf(c) : Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
        LogChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Ends the log file (the tab goes on).</summary>
    public void StopLog()
    {
        if (_log is not { } log)
        {
            return;
        }
        _log = null;
        log.Dispose();
        LogChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Output of the session for the log (any thread).</summary>
    protected void LogOutput(string text) => _log?.Write(text);
}
