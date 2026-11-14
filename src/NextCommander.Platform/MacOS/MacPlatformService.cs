namespace NextCommander.Platform.MacOS;

internal sealed class MacPlatformService : PlatformServiceBase, IPlatformService
{
    public override bool IsAvailable => true;

    /// <summary>Moves into ~/.Trash (no Finder "Put Back" metadata; avoids Automation permission prompts).</summary>
    public override Task MoveToTrashAsync(string nativePath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var trash = Path.Combine(HomeDirectory, ".Trash");
        Directory.CreateDirectory(trash);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(nativePath));
        var target = Path.Combine(trash, name);
        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
        {
            target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        }
        if (Directory.Exists(nativePath) && new DirectoryInfo(nativePath).LinkTarget is null)
        {
            Directory.Move(nativePath, target);
        }
        else
        {
            File.Move(nativePath, target);
        }
    }, cancellationToken);

    protected override IReadOnlyList<DriveItem> GetSystemDrives()
    {
        var result = new List<DriveItem>
        {
            new("~", HomeDirectory, null, 0, 0, DriveKind.Home, true),
        };
        foreach (var d in DriveInfo.GetDrives())
        {
            var root = d.RootDirectory.FullName;
            if (root == "/")
            {
                result.Add(ToDriveItem(d, "/"));
            }
            else if (root.StartsWith("/Volumes/", StringComparison.Ordinal) && !root.StartsWith("/Volumes/Recovery", StringComparison.Ordinal))
            {
                result.Add(ToDriveItem(d, Path.GetFileName(root)));
            }
        }
        return result;
    }

    public override void OpenWithDefaultApp(string path) => Start("open", path);

    public override void RunCommandLine(string commandLine, string workingDirectory, bool keepTerminalOpen)
    {
        if (keepTerminalOpen)
        {
            var script = $"cd {QuoteShell(workingDirectory)}; {commandLine}";
            Start("osascript", "-e", $"tell application \"Terminal\" to do script \"{script.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
            return;
        }
        StartIn(workingDirectory, "/bin/zsh", "-c", commandLine);
    }

    public override void OpenTerminal(string directory) => Start("open", "-a", "Terminal", directory);

    public bool PlaySound(string file)
    {
        if (!File.Exists(file))
        {
            return false;
        }
        Start("afplay", file);
        return true;
    }

    /// <summary>The authorization dialog of macOS ("with administrator privileges"); error -128 = cancelled.</summary>
    public override async Task<bool> RunElevatedAsync(string program, IReadOnlyList<string> arguments)
    {
        var shell = string.Join(' ', new[] { program }.Concat(arguments).Select(QuoteShell));
        var escaped = shell.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var psi = new System.Diagnostics.ProcessStartInfo("osascript") { UseShellExecute = false, RedirectStandardError = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"do shell script \"{escaped}\" with administrator privileges");
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            return false;
        }
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return !error.Contains("-128", StringComparison.Ordinal);
    }

    public override void StartElevated(string program, string? arguments, string workingDirectory, NextCommander.Core.Config.ProgramWindowState window = default)
    {
        var shell = $"cd {QuoteShell(workingDirectory)} && {QuoteShell(program)} {arguments}";
        var escaped = shell.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        Start("osascript", "-e", $"do shell script \"{escaped}\" with administrator privileges");
    }

    public override void RevealInSystemFileManager(string path) => Start("open", "-R", path);
}
