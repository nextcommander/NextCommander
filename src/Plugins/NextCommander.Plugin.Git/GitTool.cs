using System.Diagnostics;
using System.Text;

namespace NextCommander.Plugin.Git;

/// <summary>
/// The installed git: found once (FM_GIT, PATH, the usual folders), run with its output read as text or streamed.
/// Errors become <see cref="IOException"/> with git's own message.
/// </summary>
public sealed class GitTool
{
    private static readonly Lazy<GitTool?> s_installed = new(() => Find() is { } path ? new GitTool(path) : null);

    public GitTool(string path) => Path = path;

    /// <summary>The git program.</summary>
    public string Path { get; }

    /// <summary>The installed git, or null.</summary>
    public static GitTool? Installed => s_installed.Value;

    public const string NotInstalled = "git is not installed. Install it from git-scm.com (or set FM_GIT to its path), then open this again.";

    /// <summary>git(.exe): FM_GIT, then PATH, then the usual install folders.</summary>
    public static string? Find()
    {
        var windows = OperatingSystem.IsWindows();
        var exe = windows ? "git.exe" : "git";
        if (Environment.GetEnvironmentVariable("FM_GIT") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d.Trim('"'), exe)).ToList();
        if (windows)
        {
            candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", exe));
            candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", exe));
        }
        else
        {
            candidates.AddRange(["/usr/bin/git", "/usr/local/bin/git", "/opt/homebrew/bin/git"]);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private ProcessStartInfo Start(string repository, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(Path)
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // names as they are (no octal quoting), no pager, never ask for anything
        foreach (var argument in (string[])["-c", "core.quotepath=off", "--no-pager", .. arguments])
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        return info;
    }

    /// <summary>Runs git in <paramref name="repository"/> and gives its output; a failure becomes an IOException with git's message.</summary>
    public async Task<string> RunAsync(string repository, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(Start(repository, arguments)) ?? throw new IOException("git could not be started.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        var text = await output.ConfigureAwait(false);
        var message = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException("git: " + (message.Trim() is { Length: > 0 } m ? m.Split('\n')[0].Trim() : "exit code " + process.ExitCode));
        }
        return text;
    }

    /// <summary>Runs git and streams its output (a file's content); the stream ends the process when it is closed.</summary>
    public Stream Stream(string repository, IEnumerable<string> arguments)
    {
        var process = Process.Start(Start(repository, arguments)) ?? throw new IOException("git could not be started.");
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync(); // drained, so a chatty git never blocks
        return new ProcessStream(process);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>The standard output of a process; disposing it ends the process.</summary>
    private sealed class ProcessStream(Process process) : Stream
    {
        private readonly Stream _inner = process.StandardOutput.BaseStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Kill(process);
                process.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The repository folder holding <paramref name="folder"/> (the one with .git), or null.</summary>
    public static string? RepositoryOf(string folder)
    {
        for (var dir = folder; !string.IsNullOrEmpty(dir); dir = System.IO.Path.GetDirectoryName(dir))
        {
            var git = System.IO.Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir;
            }
        }
        return null;
    }
}
