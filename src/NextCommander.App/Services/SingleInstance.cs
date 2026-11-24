using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>
/// One window per user and settings folder: a second start sends its arguments (folders, files, --lister / --edit)
/// over a named pipe (a Unix socket on Linux / macOS) to the running instance and exits. The running instance answers
/// once its window has shown them; without that answer (an instance whose window hangs, or a process left after its
/// window closed) the second start opens its own window instead of ending silently (user's report, 24 November 2026:
/// on macOS the program "did not open at all").
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

    /// <summary>The answer of a running instance that has shown the arguments.</summary>
    private const byte Shown = 1;

    /// <summary>
    /// Hands the arguments to a running instance; true only when it answers that its window has shown them (within
    /// <paramref name="answerMs"/>). False when none listens or none answers.
    /// </summary>
    public static bool TrySend(string pipeName, string[] args, int timeoutMs = 400, int answerMs = 8000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(timeoutMs);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Normalize(args)));
            client.Write(BitConverter.GetBytes(payload.Length));
            client.Write(payload);
            client.Flush();
            var answer = new byte[1];
            using var wait = new CancellationTokenSource(answerMs);
            var read = client.ReadAsync(answer, wait.Token).AsTask().GetAwaiter().GetResult();
            return read == 1 && answer[0] == Shown;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
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
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var length = new byte[4];
                await server.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
                var payload = new byte[Math.Clamp(BitConverter.ToInt32(length), 0, 1 << 20)];
                await server.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                var args = JsonSerializer.Deserialize<string[]>(payload) ?? [];
                try
                {
                    // the answer goes once the window has taken them (a hanging window gives none: the other start opens its own)
                    await received(args).ConfigureAwait(false);
                    await server.WriteAsync(new[] { Shown }, cancellationToken).ConfigureAwait(false);
                    await server.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
                {
                    Log.Warning(ex, "Arguments from another start could not be opened");
                }
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
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or EndOfStreamException)
            {
                Log.Warning(ex, "Single instance pipe");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
