using System.Diagnostics;
using System.Text;

namespace NextCommander.Plugin.Docker;

/// <summary>What a docker command gave: its exit code, its output and its error text.</summary>
public sealed record DockerResult(int ExitCode, string Output, string Error);

/// <summary>Runs the docker program (a fake one in tests).</summary>
public interface IDockerRunner
{
    /// <summary>Runs docker with <paramref name="arguments"/>; <paramref name="input"/> (if any) is sent to its standard input.</summary>
    Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken);

    /// <summary>Runs docker and streams its output (a file's content); closing the stream ends the process.</summary>
    Stream OpenOutput(IReadOnlyList<string> arguments);
}

/// <summary>
/// The installed docker command line (Docker Desktop, Docker Engine, or another tool with the same commands named by
/// FM_DOCKER, such as podman): found once, run without a window, its own messages kept for the errors.
/// </summary>
public sealed class DockerTool(string path) : IDockerRunner
{
    private static readonly Lazy<DockerTool?> s_installed = new(() => Find() is { } path ? new DockerTool(path) : null);

    /// <summary>The docker program.</summary>
    public string Path { get; } = path;

    /// <summary>The docker the plugin's settings name (empty: the one found).</summary>
    public static string? ConfiguredPath { get; set; }

    private static DockerTool? s_configured;

    /// <summary>The docker to use: the one the settings name when it exists, else the installed one, or null.</summary>
    public static DockerTool? Installed
    {
        get
        {
            if (ConfiguredPath is not { Length: > 0 } path)
            {
                return s_installed.Value;
            }
            if (!File.Exists(path))
            {
                return null;
            }
            var configured = s_configured;
            if (configured?.Path != path)
            {
                configured = new DockerTool(path);
                s_configured = configured;
            }
            return configured;
        }
    }

    /// <summary>docker(.exe): FM_DOCKER, then PATH, then the usual install folders.</summary>
    public static string? Find()
    {
        var windows = OperatingSystem.IsWindows();
        var exe = windows ? "docker.exe" : "docker";
        if (Environment.GetEnvironmentVariable("FM_DOCKER") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d.Trim('"'), exe)).ToList();
        if (windows)
        {
            candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", exe));
        }
        else
        {
            candidates.AddRange(["/usr/bin/docker", "/usr/local/bin/docker", "/opt/homebrew/bin/docker", "/Applications/Docker.app/Contents/Resources/bin/docker"]);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private ProcessStartInfo Start(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Path)
        {
            // a neutral folder: docker cp reads a relative local path from here, and no repository's settings apply
            WorkingDirectory = System.IO.Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        // no "What's next" hints and no colors in the output that is read
        info.Environment["DOCKER_CLI_HINTS"] = "false";
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    public async Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken)
    {
        using var process = Process.Start(Start(arguments)) ?? throw new IOException("docker could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null)
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            Kill(process);
            if (ex is OperationCanceledException)
            {
                throw;
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return new DockerResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    public Stream OpenOutput(IReadOnlyList<string> arguments)
    {
        var process = Process.Start(Start(arguments)) ?? throw new IOException("docker could not be started.");
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync(); // drained, so a chatty docker never blocks
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
}
