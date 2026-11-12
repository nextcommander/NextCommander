using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>
/// One window per user and settings folder: a second start sends its arguments (folders, files, --lister / --edit)
/// over a named pipe (a Unix socket on Linux / macOS) to the running instance and exits.
/// </summary>
public static class SingleInstance
{
    /// <summary>Pipe name: per user and settings folder (portable copies do not interfere).</summary>
    public static string PipeName(string settingsDirectory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + settingsDirectory.ToUpperInvariant())))[..16];
        return "NextCommander-" + hash;
    }

    /// <summary>
    /// True when an instance listens on the pipe. Cheap: connecting to a missing pipe would wait for the whole timeout.
    /// </summary>
    public static bool IsRunning(string pipeName)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? Directory.EnumerateFiles(@"\\.\pipe\", pipeName).Any()
                : File.Exists(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Relative paths are made absolute here: the running instance has another working folder.</summary>
    public static string[] Normalize(string[] args) =>
        args.Select(a => a.StartsWith("--", StringComparison.Ordinal) || a.Contains("://", StringComparison.Ordinal) ? a : Path.GetFullPath(a)).ToArray();

    /// <summary>Hands the arguments to a running instance; false when none answers.</summary>
    public static bool TrySend(string pipeName, string[] args, int timeoutMs = 400)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Normalize(args)));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Receives arguments from later starts until <paramref name="cancellationToken"/> is cancelled.</summary>
    public static async Task ListenAsync(string pipeName, Func<string[], Task> received, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var ms = new MemoryStream();
                await server.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(ms.ToArray()) ?? [];
                // Handle in the background: the next start must not wait until this one's tab is open.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await received(args).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        Log.Warning(ex, "Arguments from another start could not be opened");
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x800700E7)) // ERROR_PIPE_BUSY: another instance owns the name
            {
                Log.Warning("Another instance already listens on {Pipe}; not listening.", pipeName);
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Single instance pipe");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
