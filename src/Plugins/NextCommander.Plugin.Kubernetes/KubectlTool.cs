using System.Diagnostics;
using System.Text;

namespace NextCommander.Plugin.Kubernetes;

/// <summary>What a kubectl command gave: its exit code, its output and its error text.</summary>
public sealed record KubectlResult(int ExitCode, string Output, string Error);

/// <summary>Runs the kubectl program (a fake one in tests).</summary>
public interface IKubectlRunner
{
    /// <summary>Runs kubectl with <paramref name="arguments"/>; <paramref name="input"/> (if any) is sent to its standard input.</summary>
    Task<KubectlResult> RunAsync(IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken);

    /// <summary>Runs kubectl and streams its output (a file's content); closing the stream ends the process.</summary>
    Stream OpenOutput(IReadOnlyList<string> arguments);
}

/// <summary>
/// The installed kubectl (or the program FM_KUBECTL names): found once, run without a window, its own messages kept for
/// the errors. It works with the kubeconfig kubectl itself reads (KUBECONFIG, ~/.kube/config) unless the plugin's
/// settings name another.
/// </summary>
public sealed class KubectlTool(string path) : IKubectlRunner
{
    private static readonly Lazy<KubectlTool?> s_installed = new(() => Find() is { } path ? new KubectlTool(path) : null);

    /// <summary>The kubectl program.</summary>
    public string Path { get; } = path;

    /// <summary>The kubectl the plugin's settings name (empty: the one found).</summary>
    public static string? ConfiguredPath { get; set; }

    private static KubectlTool? s_configured;

    /// <summary>The kubectl to use: the one the settings name when it exists, else the installed one, or null.</summary>
    public static KubectlTool? Installed
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
                configured = new KubectlTool(path);
                s_configured = configured;
            }
            return configured;
        }
    }

    /// <summary>kubectl(.exe): FM_KUBECTL, then PATH, then the usual install folders (Docker Desktop and Rancher Desktop bring one).</summary>
    public static string? Find()
    {
        var windows = OperatingSystem.IsWindows();
        var exe = windows ? "kubectl.exe" : "kubectl";
        if (Environment.GetEnvironmentVariable("FM_KUBECTL") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d.Trim('"'), exe)).ToList();
        if (windows)
        {
            candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", exe));
            candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rd", "bin", exe));
        }
        else
        {
            candidates.AddRange(["/usr/bin/kubectl", "/usr/local/bin/kubectl", "/opt/homebrew/bin/kubectl", "/snap/bin/kubectl",
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rd", "bin", "kubectl")]);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private ProcessStartInfo Start(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Path)
        {
            // a neutral folder: no relative path of the program's own folder is read
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
        // no colors in the output that is read
        info.Environment["NO_COLOR"] = "1";
        return info;
    }

    public async Task<KubectlResult> RunAsync(IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken)
    {
        using var process = Process.Start(Start(arguments)) ?? throw new IOException("kubectl could not be started.");
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
        return new KubectlResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    public Stream OpenOutput(IReadOnlyList<string> arguments)
    {
        var process = Process.Start(Start(arguments)) ?? throw new IOException("kubectl could not be started.");
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync(); // drained, so a chatty kubectl never blocks
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
