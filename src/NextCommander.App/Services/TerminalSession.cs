using System.Diagnostics;
using System.Text;

namespace NextCommander.App.Services;

/// <summary>
/// The shell behind the internal terminal (Commands > Internal terminal): a cmd.exe (Windows) or $SHELL / /bin/sh
/// process with redirected input and output. Output arrives in pieces as it is written (also prompts without a line
/// end); <see cref="Send"/> writes one input line. Not a full terminal emulator: programs that draw on the screen
/// (editors, pagers) need an external terminal.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private TerminalSession(Process process, string shellName)
    {
        _process = process;
        ShellName = shellName;
    }

    /// <summary>Text written by the shell (output and errors), on a worker thread.</summary>
    public event Action<string>? Output;

    /// <summary>The shell ended (exit code), on a worker thread.</summary>
    public event Action<int>? Exited;

    public string ShellName { get; }

    /// <summary>True when the shell itself echoes the commands it reads (cmd.exe shows prompt and command).</summary>
    public bool EchoesInput { get; private init; }

    /// <summary>Prompt the terminal shows for a shell that does not echo ("PS> ", "$ ").</summary>
    public string Prompt { get; private init; } = "$ ";

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>A shell the internal terminal can start.</summary>
    /// <param name="Name">Shown name ("cmd", "PowerShell 7", "Git Bash" ...).</param>
    /// <param name="Echoes">The shell writes its prompt and the command it reads (cmd); otherwise the terminal shows them.</param>
    /// <param name="Prompt">Prompt shown by the terminal for shells that do not echo ("PS> ", "$ ").</param>
    /// <param name="TerminalArguments">Arguments when the shell runs on a pseudo terminal (interactive, with its own prompt).</param>
    public sealed record ShellInfo(string Name, string FileName, string Arguments, bool Echoes, string Prompt, string TerminalArguments = "");

    private static ShellInfo Cmd(string file) => new("cmd", file, "/K \"@chcp 65001>nul\"", true, string.Empty);

    private static ShellInfo PowerShell(string name, string file) => new(name, file, "-NoLogo -NoProfile -NoExit -Command -", false, "PS> ", "-NoLogo");

    private static ShellInfo Posix(string name, string file) => new(name, file, string.Empty, false, "$ ",
        name == "Git Bash" ? "--login -i" : OperatingSystem.IsWindows() ? string.Empty : "-l");

    /// <summary>The shell for a setting ("pwsh", "/bin/zsh", a name of <see cref="AvailableShells"/>); empty: the system's default.</summary>
    public static ShellInfo ShellFor(string? shell)
    {
        if (!string.IsNullOrWhiteSpace(shell))
        {
            var wanted = shell.Trim();
            if (AvailableShells().FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.FileName, wanted, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                return known;
            }
            var name = Path.GetFileNameWithoutExtension(wanted).ToLowerInvariant();
            return name switch
            {
                "cmd" => Cmd(wanted),
                "powershell" or "pwsh" => PowerShell(name, wanted),
                _ => Posix(Path.GetFileName(wanted), wanted),
            };
        }
        return AvailableShells()[0];
    }

    private static IReadOnlyList<ShellInfo>? s_available;

    /// <summary>
    /// Shells installed on this computer, the system's default first. Windows: cmd, Windows PowerShell, PowerShell 7,
    /// Git Bash, WSL. Others: $SHELL, the shells of /etc/shells, PowerShell 7.
    /// </summary>
    public static IReadOnlyList<ShellInfo> AvailableShells() => s_available ??= FindShells();

    private static IReadOnlyList<ShellInfo> FindShells()
    {
        var shells = new List<ShellInfo>();
        string? OnPath(string file) => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), file))
            .FirstOrDefault(File.Exists);
        if (OperatingSystem.IsWindows())
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var cmd = Path.Combine(system, "cmd.exe");
            shells.Add(Cmd(File.Exists(cmd) ? cmd : "cmd.exe"));
            var windowsPowerShell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(windowsPowerShell))
            {
                shells.Add(PowerShell("Windows PowerShell", windowsPowerShell));
            }
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pwsh = OnPath("pwsh.exe") ?? new[] { Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe") }.FirstOrDefault(File.Exists);
            if (pwsh is not null)
            {
                shells.Add(PowerShell("PowerShell 7", pwsh));
            }
            var gitBash = new[] { Path.Combine(programFiles, "Git", "bin", "bash.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe") }
                .FirstOrDefault(File.Exists);
            if (gitBash is not null)
            {
                shells.Add(Posix("Git Bash", gitBash));
            }
            var wsl = Path.Combine(system, "wsl.exe");
            if (File.Exists(wsl))
            {
                shells.Add(Posix("WSL", wsl));
            }
            return shells;
        }
        var own = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(own) && File.Exists(own))
        {
            shells.Add(Posix(Path.GetFileName(own), own));
        }
        try
        {
            if (File.Exists("/etc/shells"))
            {
                foreach (var line in File.ReadAllLines("/etc/shells"))
                {
                    var file = line.Trim();
                    if (file.StartsWith('/') && File.Exists(file) && !shells.Any(s => s.FileName == file)
                        && !shells.Any(s => s.Name == Path.GetFileName(file)))
                    {
                        shells.Add(Path.GetFileName(file) == "pwsh" ? PowerShell("PowerShell 7", file) : Posix(Path.GetFileName(file), file));
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        if (!shells.Any(s => s.Name == "PowerShell 7") && OnPath("pwsh") is { } unixPwsh)
        {
            shells.Add(PowerShell("PowerShell 7", unixPwsh));
        }
        if (shells.Count == 0)
        {
            shells.Add(Posix("sh", "/bin/sh"));
        }
        return shells;
    }

    /// <summary>Starts a shell (default: <see cref="ShellFor"/> of null) in <paramref name="directory"/>.</summary>
    public static TerminalSession Start(string directory, ShellInfo? shell = null)
    {
        shell ??= ShellFor(null);
        var (fileName, arguments) = (shell.FileName, shell.Arguments);
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = Directory.Exists(directory) ? directory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        info.Environment["TERM"] = "dumb";
        var process = Process.Start(info) ?? throw new InvalidOperationException(fileName);
        var session = new TerminalSession(process, shell.Name) { EchoesInput = shell.Echoes, Prompt = shell.Prompt };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }
            session.Exited?.Invoke(code);
        };
        _ = session.PumpAsync(process.StandardOutput);
        _ = session.PumpAsync(process.StandardError);
        return session;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                Output?.Invoke(new string(buffer, 0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // the shell ended or the session was closed
        }
    }

    /// <summary>Writes one line to the shell.</summary>
    public void Send(string line)
    {
        if (HasExited)
        {
            return;
        }
        try
        {
            _process.StandardInput.Write(line + "\n");
            _process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the shell ended meanwhile
        }
    }

    /// <summary>The command that changes the shell's directory (switch drive too on Windows).</summary>
    public string ChangeDirectoryCommand(string directory) => ChangeDirectoryCommand(ShellName, Prompt, directory);

    /// <summary>The command that changes the directory of a shell.</summary>
    public static string ChangeDirectoryCommand(ShellInfo shell, string directory) => ChangeDirectoryCommand(shell.Name, shell.Prompt, directory);

    private static string ChangeDirectoryCommand(string shellName, string prompt, string directory) =>
        shellName.StartsWith("cmd", StringComparison.OrdinalIgnoreCase) ? $"cd /d \"{directory}\""
        : prompt == "PS> " ? $"Set-Location -LiteralPath '{directory.Replace("'", "''", StringComparison.Ordinal)}'"
        : $"cd \"{UnixPath(shellName, directory).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    /// <summary>C:\a\b as Git Bash (/c/a/b) or WSL (/mnt/c/a/b) sees it.</summary>
    private static string UnixPath(string shellName, string directory)
    {
        if (directory.Length < 2 || directory[1] != ':' || shellName is not ("Git Bash" or "WSL"))
        {
            return directory;
        }
        var rest = directory[2..].Replace('\\', '/');
        var drive = char.ToLowerInvariant(directory[0]);
        return (shellName == "WSL" ? "/mnt/" : "/") + drive + rest;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // already gone
        }
        _process.Dispose();
        _cts.Dispose();
    }
}
