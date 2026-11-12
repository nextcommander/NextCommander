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

    /// <summary>Tab title: the shell's name.</summary>
    public virtual string Title => Shell.Name;

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
        if (CurrentFolder() is { } folder)
        {
            Run(TerminalSession.ChangeDirectoryCommand(Shell, folder));
        }
    }

    protected void RaiseShellExited() => ShellExited?.Invoke(this, EventArgs.Empty);
}
