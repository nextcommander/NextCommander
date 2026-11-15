using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Rclone;

/// <summary>
/// The remotes of the installed rclone (<c>rclone://remote/folder/file</c>; <c>rclone:///</c> lists them): the
/// remotes the user set up with "rclone config" (70+ services), their secrets staying with rclone. rclone runs once as
/// a local API server (rcd, on 127.0.0.1 with a random user and password); files go through local temporary files
/// with operations/copyfile, which keeps their modification time where the service can.
/// Shipped as a plugin (plugins/rclone next to the program); its entry point "rclone remotes" is listed with the
/// saved connections.
/// </summary>
public sealed class RcloneFileSystemProvider : IFileSystemProvider, IUploadTimeSupport, IPluginEntryPoint, IDisposable
{
    public const string RcloneScheme = "rclone";

    /// <summary>The remote list: <c>rclone:///</c>.</summary>
    public static VfsPath Root { get; } = VfsPath.Create(RcloneScheme, string.Empty, "/");

    private readonly string? _tool;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<VfsPath, DateTime> _uploadTimes = new();
    private Process? _daemon;
    private HttpClient? _http;

    /// <summary>The plugin loader's constructor: the installed rclone (<see cref="FindTool"/>).</summary>
    public RcloneFileSystemProvider()
        : this(FindTool())
    {
    }

    /// <param name="tool">The rclone program (null: not installed).</param>
    public RcloneFileSystemProvider(string? tool) => _tool = tool;

    public string EntryName => "rclone remotes";

    public string EntryGroup => "rclone";

    public VfsPath EntryRoot => Root;

    public string? Unavailable => IsInstalled ? null
        : "rclone is not installed. Install it from rclone.org/downloads (or set FM_RCLONE to its path), set up your remotes with \"rclone config\", then open this again.";

    /// <summary>Tests: the HTTP handler of the API (a fake rclone); no process is started.</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; init; }

    public bool IsInstalled => _tool is not null || HandlerFactory is not null;

    public string Scheme => RcloneScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename;

    /// <summary>rclone(.exe): FM_RCLONE, then PATH, then the usual install folders.</summary>
    public static string? FindTool()
    {
        var windows = OperatingSystem.IsWindows();
        var exe = windows ? "rclone.exe" : "rclone";
        if (Environment.GetEnvironmentVariable("FM_RCLONE") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe)).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (windows)
        {
            candidates.Add(Path.Combine(home, "scoop", "shims", exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "rclone", exe));
        }
        else
        {
            candidates.AddRange(["/opt/homebrew/bin/rclone", "/usr/local/bin/rclone", "/usr/bin/rclone", Path.Combine(home, ".local", "bin", "rclone")]);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------------------------------------------------------------- the API server

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<HttpClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_http is { } ready && (_daemon is null || !_daemon.HasExited))
        {
            return ready;
        }
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_http is { } started && (_daemon is null || !_daemon.HasExited))
            {
                return started;
            }
            _http?.Dispose();
            _http = null;
            if (HandlerFactory is { } factory)
            {
                return _http = new HttpClient(factory()) { BaseAddress = new Uri("http://rc.test/") };
            }
            var tool = _tool ?? throw new IOException("rclone is not installed (rclone.org/downloads; or set FM_RCLONE to rclone's path).");
            var port = FreePort();
            var user = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var info = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var argument in new[] { "rcd", "--rc-addr", "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) })
            {
                info.ArgumentList.Add(argument);
            }
            // the credentials go by environment (not visible in the process list)
            info.Environment["RCLONE_RC_USER"] = user;
            info.Environment["RCLONE_RC_PASS"] = password;
            var daemon = Process.Start(info) ?? throw new IOException("rclone could not be started.");
            daemon.StandardInput.Close();
            var log = new StringBuilder();
            daemon.ErrorDataReceived += (_, e) =>
            {
                lock (log)
                {
                    if (e.Data is not null && log.Length < 4000)
                    {
                        log.AppendLine(e.Data);
                    }
                }
            };
            daemon.OutputDataReceived += (_, _) => { };
            daemon.BeginErrorReadLine();
            daemon.BeginOutputReadLine();
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
            for (var waited = 0; ; waited += 100)
            {
                if (daemon.HasExited || waited > 15000)
                {
                    http.Dispose();
                    Stop(daemon);
                    string text;
                    lock (log)
                    {
                        text = log.ToString().Trim();
                    }
                    throw new IOException("rclone: the API server did not start." + (text.Length > 0 ? " " + text : string.Empty));
                }
                try
                {
                    using var noop = await http.PostAsync("rc/noop", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
                    if (noop.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            _daemon = daemon;
            return _http = http;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static void Stop(Process process)
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
        process.Dispose();
    }

    /// <summary>An rc call: JSON in, JSON out; an error answer ({"error": ..., "status": ...}) becomes the matching exception.</summary>
    private async Task<JsonNode?> CallAsync(string command, JsonObject body, string path, CancellationToken cancellationToken)
    {
        var http = await ClientAsync(cancellationToken).ConfigureAwait(false);
        using var response = await http.PostAsync(command, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? json = null;
        try
        {
            json = text.Length > 0 ? JsonNode.Parse(text) : null;
        }
        catch (JsonException)
        {
        }
        if (response.IsSuccessStatusCode)
        {
            return json;
        }
        throw ErrorOf(response.StatusCode, json?["error"]?.GetValue<string>() ?? text, path);
    }

    internal static Exception ErrorOf(HttpStatusCode status, string message, string path)
    {
        message = message.Trim();
        if (status == HttpStatusCode.NotFound || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) || message.Contains("no such file", StringComparison.OrdinalIgnoreCase))
        {
            return new FileNotFoundException($"Not found: {path}", path);
        }
        if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
        {
            return new UnauthorizedAccessException($"{path}: {message}");
        }
        if (message.Contains("directory not empty", StringComparison.OrdinalIgnoreCase))
        {
            return new IOException($"Folder is not empty: {path}");
        }
        return new IOException($"rclone: {message}");
    }

    // ---------------------------------------------------------------- paths

    /// <summary>The rclone fs of a path's remote ("gdrive:").</summary>
    public static string FsOf(VfsPath path) => path.Authority + ":";

    /// <summary>The path inside the remote, without a leading slash ("" for its root).</summary>
    public static string RemoteOf(VfsPath path) => path.Path.Trim('/');

    private static VfsEntry EntryOf(VfsPath path, JsonNode item)
    {
        var folder = item["IsDir"]?.GetValue<bool>() == true;
        var modified = DateTimeOffset.TryParse(item["ModTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.LocalDateTime : DateTime.MinValue;
        var size = item["Size"]?.GetValue<long>() ?? 0;
        return new VfsEntry { Name = path.Name, Path = path, IsDirectory = folder, Size = folder || size < 0 ? 0 : size, LastWriteTime = modified };
    }

    // ---------------------------------------------------------------- reading

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        if (directory.Authority.Length == 0)
        {
            var remotes = await CallAsync("config/listremotes", [], "/", cancellationToken).ConfigureAwait(false);
            return [.. (remotes?["remotes"]?.AsArray() ?? []).Select(r => r?.GetValue<string>()).OfType<string>()
                .Select(r => new VfsEntry { Name = r, Path = VfsPath.Create(RcloneScheme, r, "/"), IsDirectory = true, LastWriteTime = DateTime.MinValue })];
        }
        var json = await CallAsync("operations/list", new JsonObject { ["fs"] = FsOf(directory), ["remote"] = RemoteOf(directory), ["opt"] = new JsonObject() },
            directory.Path, cancellationToken).ConfigureAwait(false);
        var result = new List<VfsEntry>();
        foreach (var item in (json?["list"]?.AsArray() ?? []).OfType<JsonNode>())
        {
            if (item["Name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                result.Add(EntryOf(directory.Combine(name), item));
            }
        }
        return result;
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0)
        {
            return new VfsEntry { Name = "rclone", Path = Root, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        if (RemoteOf(path).Length == 0)
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        try
        {
            var json = await CallAsync("operations/stat", new JsonObject { ["fs"] = FsOf(path), ["remote"] = RemoteOf(path), ["opt"] = new JsonObject() },
                path.Path, cancellationToken).ConfigureAwait(false);
            return json?["item"] is JsonObject item ? EntryOf(path, item) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A local temporary folder for one transfer (deleted after it).</summary>
    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fm-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The file is copied by rclone into a temporary file, read from there (deleted when closed).</summary>
    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var folder = TempFolder();
        try
        {
            await CallAsync("operations/copyfile", new JsonObject
            {
                ["srcFs"] = FsOf(path), ["srcRemote"] = RemoteOf(path), ["dstFs"] = folder, ["dstRemote"] = "download",
            }, path.Path, cancellationToken).ConfigureAwait(false);
            var local = Path.Combine(folder, "download");
            if (!File.Exists(local))
            {
                throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
            }
            var moved = Path.Combine(Path.GetTempPath(), "fm-rclone-" + Guid.NewGuid().ToString("N"));
            File.Move(local, moved);
            return new FileStream(moved, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    /// <summary>The data is collected in a temporary file and copied up by rclone when the stream is closed (with the time the copy announced).</summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        if (RemoteOf(path).Length == 0)
        {
            throw new IOException("rclone: files are written into a remote.");
        }
        var existing = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream(async (data, ct) =>
        {
            var folder = TempFolder();
            try
            {
                var local = Path.Combine(folder, "upload");
                await using (var file = File.Create(local))
                {
                    data.Position = 0;
                    await data.CopyToAsync(file, ct).ConfigureAwait(false);
                }
                if (_uploadTimes.TryRemove(path, out var time) && time.Year >= 1980)
                {
                    File.SetLastWriteTimeUtc(local, time.ToUniversalTime());
                }
                await CallAsync("operations/copyfile", new JsonObject
                {
                    ["srcFs"] = folder, ["srcRemote"] = "upload", ["dstFs"] = FsOf(path), ["dstRemote"] = RemoteOf(path),
                }, path.Path, ct).ConfigureAwait(false);
            }
            finally
            {
                DeleteFolder(folder);
            }
        }, cancellationToken);
    }

    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _uploadTimes[path] = lastWriteTime;

    // ---------------------------------------------------------------- changes

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        path.Authority.Length == 0 || RemoteOf(path).Length == 0
            ? Task.CompletedTask
            : CallAsync("operations/mkdir", new JsonObject { ["fs"] = FsOf(path), ["remote"] = RemoteOf(path) }, path.Path, cancellationToken);

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        if (RemoteOf(path).Length == 0)
        {
            throw new IOException("rclone: a remote is removed with \"rclone config\".");
        }
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        var command = !entry.IsDirectory ? "operations/deletefile" : recursive ? "operations/purge" : "operations/rmdir";
        await CallAsync(command, new JsonObject { ["fs"] = FsOf(path), ["remote"] = RemoteOf(path) }, path.Path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A file with movefile, a folder with sync/move (on the server where the service can).</summary>
    public async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (source.Scheme != destination.Scheme || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between remotes is done by copying.");
        }
        var entry = await GetEntryAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {source.Path}", source.Path);
        if (!string.Equals(source.Path, destination.Path, StringComparison.Ordinal) && await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && (!overwrite || existing.IsDirectory || entry.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        if (entry.IsDirectory)
        {
            await CallAsync("sync/move", new JsonObject
            {
                ["srcFs"] = FsOf(source) + RemoteOf(source), ["dstFs"] = FsOf(destination) + RemoteOf(destination),
                ["createEmptySrcDirs"] = true, ["deleteEmptySrcDirs"] = true,
            }, source.Path, cancellationToken).ConfigureAwait(false);
            try
            {
                // the emptied source folders (sync/move leaves the folder itself)
                await CallAsync("operations/rmdirs", new JsonObject { ["fs"] = FsOf(source), ["remote"] = RemoteOf(source), ["leaveRoot"] = false },
                    source.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
            }
            return;
        }
        await CallAsync("operations/movefile", new JsonObject
        {
            ["srcFs"] = FsOf(source), ["srcRemote"] = RemoteOf(source), ["dstFs"] = FsOf(destination), ["dstRemote"] = RemoteOf(destination),
        }, source.Path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The time is given with the upload (<see cref="PrepareUpload"/>); rclone has no call to change it later.</summary>
    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _http?.Dispose();
        _http = null;
        if (_daemon is { } daemon)
        {
            _daemon = null;
            Stop(daemon); // our own API server, started for this run
        }
        _startLock.Dispose();
    }
}
