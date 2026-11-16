using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Tools;

namespace NextCommander.Platform.Adb;

/// <summary>A device the ADB server knows: its serial (USB serial or ip:port), name (the model) and state.</summary>
public sealed record AdbDevice(string Serial, string Name, string State)
{
    /// <summary>Usable: authorized and online ("unauthorized" waits for the prompt on the phone, "offline" for the connection).</summary>
    public bool IsReady => State == "device";
}

/// <summary>What an adb command printed and its exit code.</summary>
public sealed record AdbResult(int ExitCode, string Output, string Error);

/// <summary>Runs the adb program (a fake in tests).</summary>
public interface IAdbRunner
{
    Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>The standard output of a command as a stream (exec-out: binary data); the process ends when it is closed.</summary>
    Task<Stream> OpenOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Android phones over ADB (Android Debug Bridge; USB debugging on): the installed adb program (the Android SDK
/// platform-tools; <c>FM_ADB</c>, PATH or the SDK folders) runs the commands; the device list comes from the ADB
/// server (localhost:5037) directly, which is cheap enough to be asked every second by the drive watch.
/// </summary>
public sealed class AdbBridge(IAdbRunner? runner, Func<IReadOnlyList<AdbDevice>> devices)
{
    private static readonly Lazy<AdbBridge> s_shared = new(() =>
    {
        var tool = FindTool();
        var runner = tool is null ? null : new AdbProcessRunner(tool);
        return new AdbBridge(runner, () => ServerDevices(runner));
    });

    /// <summary>The bridge of the application (no runner and no devices when adb is not installed).</summary>
    public static AdbBridge Shared => s_shared.Value;

    /// <summary>The adb program; null when it is not installed.</summary>
    public IAdbRunner? Runner { get; } = runner;

    /// <summary>The devices connected now (ready or not).</summary>
    public IReadOnlyList<AdbDevice> GetDevices()
    {
        try
        {
            return devices();
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            return [];
        }
    }

    // ---------------------------------------------------------------- the adb program

    /// <summary>adb(.exe): FM_ADB, then PATH, then the usual Android SDK folders.</summary>
    public static string? FindTool() => FindTool(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    internal static string? FindTool(Func<string, string?> env, Func<string, bool> exists, bool windows, bool mac)
    {
        var exe = windows ? "adb.exe" : "adb";
        if (env("FM_ADB") is { Length: > 0 } configured && exists(configured))
        {
            return configured;
        }
        var candidates = new List<string>();
        foreach (var dir in (env("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim('"'), exe));
        }
        foreach (var sdk in new[] { env("ANDROID_HOME"), env("ANDROID_SDK_ROOT") })
        {
            if (!string.IsNullOrEmpty(sdk))
            {
                candidates.Add(Path.Combine(sdk, "platform-tools", exe));
            }
        }
        var home = env(windows ? "USERPROFILE" : "HOME") ?? string.Empty;
        if (windows && env("LOCALAPPDATA") is { Length: > 0 } local)
        {
            candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", exe));
        }
        else if (mac)
        {
            candidates.Add(Path.Combine(home, "Library", "Android", "sdk", "platform-tools", exe));
            candidates.Add("/opt/homebrew/bin/adb");
            candidates.Add("/usr/local/bin/adb");
        }
        else if (!windows)
        {
            candidates.Add(Path.Combine(home, "Android", "Sdk", "platform-tools", exe));
        }
        return candidates.FirstOrDefault(exists);
    }

    // ---------------------------------------------------------------- the device list

    /// <summary>
    /// The lines of "adb devices -l" (or host:devices-l): "serial  state  product:x model:Pixel_7 device:y". The name is
    /// the model (underscores as spaces), else the serial.
    /// </summary>
    public static IReadOnlyList<AdbDevice> ParseDevices(string text)
    {
        var result = new List<AdbDevice>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            var properties = parts.Skip(2).Select(p => p.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            var name = properties.GetValueOrDefault("model") ?? properties.GetValueOrDefault("product") ?? parts[0];
            result.Add(new AdbDevice(parts[0], name.Replace('_', ' '), parts[1]));
        }
        return result;
    }

    private static int s_startTried;

    /// <summary>
    /// The devices from the ADB server's host protocol (no process started). When no server runs yet, adb starts one
    /// once in the background; its phones show up with the next drive check.
    /// </summary>
    private static IReadOnlyList<AdbDevice> ServerDevices(IAdbRunner? runner)
    {
        if (runner is null)
        {
            return [];
        }
        if (QueryServer("host:devices-l") is { } text)
        {
            return ParseDevices(text);
        }
        if (Interlocked.Exchange(ref s_startTried, 1) == 0)
        {
            _ = Task.Run(() => runner.RunAsync(["start-server"], CancellationToken.None));
        }
        return [];
    }

    /// <summary>One request of the ADB host protocol ("000chost:version" ...); null when no server answers.</summary>
    internal static string? QueryServer(string request, int port = 5037)
    {
        using var tcp = new TcpClient();
        try
        {
            // at most 300 ms: the connect is cancelled and its end awaited (a connect left running and then closed
            // with the socket ended as an unobserved "Operation canceled" exception on macOS)
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                try
                {
                    tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return null; // no ADB server answering
                }
            }
            var stream = tcp.GetStream();
            stream.ReadTimeout = 2000;
            stream.Write(Encoding.ASCII.GetBytes(request.Length.ToString("x4", CultureInfo.InvariantCulture) + request));
            var status = ReadExactly(stream, 4);
            if (status != "OKAY")
            {
                return null;
            }
            var length = int.Parse(ReadExactly(stream, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return ReadExactly(stream, length);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException or FormatException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static string ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }
}

/// <summary>Runs the installed adb program.</summary>
public sealed class AdbProcessRunner(string tool) : IAdbRunner
{
    private ProcessStartInfo Info(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
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
        return info;
    }

    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private bool _serverStarted;

    /// <summary>
    /// The ADB server runs before any command: a command that starts it would hand our output pipes on to the server
    /// (it lives on), and its output would never end. "start-server" itself is waited for without reading its output.
    /// </summary>
    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        if (_serverStarted)
        {
            return;
        }
        await _serverLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_serverStarted)
            {
                using var process = Process.Start(Info(["start-server"])) ?? throw new IOException("ADB: adb could not be started.");
                process.StandardInput.Close();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); // no stream is read: the exit alone is waited for
                _serverStarted = true;
            }
        }
        finally
        {
            _serverLock.Release();
        }
    }

    public async Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
        if (arguments is ["start-server"])
        {
            return new AdbResult(0, string.Empty, string.Empty);
        }
        using var process = Process.Start(Info(arguments)) ?? throw new IOException("ADB: adb could not be started.");
        process.StandardInput.Close();
        // not waited for after a while (see below): a read failing later is marked as seen
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None).Observe();
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None).Observe();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        // the output ends with the process; should a server started meanwhile hold the pipes, it is not waited for
        await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
        return new AdbResult(process.ExitCode, output.IsCompletedSuccessfully ? output.Result : string.Empty, error.IsCompletedSuccessfully ? error.Result : string.Empty);
    }

    public async Task<Stream> OpenOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
        var info = Info(arguments);
        info.StandardOutputEncoding = null;
        var process = Process.Start(info) ?? throw new IOException("ADB: adb could not be started.");
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None); // not left to fill its pipe
        return new ProcessOutputStream(process);
    }

    /// <summary>The output of a process; closing it before the end stops the process.</summary>
    private sealed class ProcessOutputStream(Process process) : Stream
    {
        private readonly Stream _inner = process.StandardOutput.BaseStream;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                _inner.Dispose();
                process.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
