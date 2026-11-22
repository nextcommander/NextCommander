using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Globalization;
using System.Text.Json;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using static NextCommander.Core.Vfs.PosixShell;

namespace NextCommander.Plugin.Docker;

/// <summary>
/// The files of Docker containers and volumes through the installed docker command line:
/// <list type="bullet">
/// <item><c>docker:///</c> lists the containers (running and stopped) and the folder "(volumes)".</item>
/// <item><c>docker://&lt;container&gt;/etc/...</c> is a path inside a container. A running one is listed with its shell
/// (<see cref="PosixShell"/>), read with cat and written through "exec -i" (a file keeps its owner, the container user's
/// rights apply); changes are shell commands. A stopped one is read-only through "docker cp" (a tar stream).</item>
/// <item><c>docker://~volumes/&lt;volume&gt;/...</c> is a volume, reached through a small helper container that mounts it
/// (the image of the settings, busybox by default; removed when the program ends).</item>
/// </list>
/// The right click menu of a container starts, stops, restarts, pauses and resumes it. Its entry point "Docker containers"
/// is listed with the saved connections.
/// </summary>
public sealed class DockerFileSystemProvider : IFileSystemProvider, IUploadTimeSupport, IPluginEntryPoint, IOriginProvider, IConfigurablePlugin,
    IPluginEntryActions, IDisposable
{
    public const string DockerScheme = "docker";

    /// <summary>The authority of the volume list (no container can have this name: "~" is not allowed in one).</summary>
    public const string VolumesAuthority = "~volumes";

    /// <summary>The name of the volume folder in the container list (no container name has parentheses).</summary>
    public const string VolumesFolder = "(volumes)";

    /// <summary>Where a helper container mounts its volume.</summary>
    private const string VolumeMount = "/v";

    /// <summary>The container list: <c>docker:///</c>.</summary>
    public static VfsPath Root { get; } = VfsPath.Create(DockerScheme, string.Empty, "/");

    /// <summary>The volume list: <c>docker://~volumes/</c>.</summary>
    public static VfsPath VolumesRoot { get; } = VfsPath.Create(DockerScheme, VolumesAuthority, "/");

    private readonly IDockerRunner? _fixedRunner;
    private readonly bool _installed;
    private readonly ConcurrentDictionary<VfsPath, DateTime> _uploadTimes = new();

    /// <summary>The state of each container at the last listing ("running", "exited", "paused" ...): the menu's actions.</summary>
    private readonly ConcurrentDictionary<string, string> _states = new(StringComparer.Ordinal);

    /// <summary>The helper containers of the volumes, with when they were started.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _helpers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _helperLock = new(1, 1);

    /// <summary>The user commands in a container run as (null: the container's own user); the plugin's settings.</summary>
    internal string? ExecUser { get; set; }

    /// <summary>The image of the volume helpers (it needs sh, ls, stat and sleep); the plugin's settings.</summary>
    internal string HelperImage { get; set; } = DefaultHelperImage;

    internal const string DefaultHelperImage = "busybox";

    /// <summary>A helper is started again after this long (it sleeps one hour and then removes itself).</summary>
    private static readonly TimeSpan s_helperLife = TimeSpan.FromMinutes(50);

    /// <summary>The plugin loader's constructor: the installed docker.</summary>
    public DockerFileSystemProvider() => _installed = true;

    /// <param name="runner">The docker program (null: not installed); a fake one in tests.</param>
    public DockerFileSystemProvider(IDockerRunner? runner) => _fixedRunner = runner;

    /// <summary>The docker in use: for the loader's instance the one the settings name now, else the one given.</summary>
    private IDockerRunner? CurrentRunner => _installed ? DockerTool.Installed : _fixedRunner;

    public string EntryName => "Docker containers";

    public string EntryGroup => "Docker";

    public VfsPath EntryRoot => Root;

    public string? Unavailable => CurrentRunner is null ? NotInstalled : null;

    internal const string NotInstalled = "Docker is not installed. Install Docker Desktop or Docker Engine (or set FM_DOCKER to the docker program), then open this again.";

    public string Scheme => DockerScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.SetTimes;

    /// <summary>A container in the list.</summary>
    internal sealed record Container(string Name, string State, string Image, DateTime Created)
    {
        public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A container that is not running: its files can only be read (through docker cp).</summary>
    internal sealed class ContainerStoppedException(string message) : IOException(message);

    private IDockerRunner Runner => CurrentRunner ?? throw new IOException(NotInstalled);

    // ---------------------------------------------------------------- running docker

    /// <summary>The message of a docker failure as the matching exception.</summary>
    internal static Exception ErrorOf(DockerResult result, VfsPath path)
    {
        var text = (result.Error + "\n" + result.Output).Trim();
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        bool Has(string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has("Cannot connect to the Docker daemon") || Has("failed to connect to the docker API") || Has("error during connect")
            || Has("Is the docker daemon running"))
        {
            return new IOException("Docker is not running: start Docker Desktop (or the docker service), then try again.");
        }
        if (Has("is not running") || Has("is paused") || Has("is restarting"))
        {
            return new ContainerStoppedException($"The container is not running: start it (docker start {path.Authority}) to work with its files.");
        }
        if (Has("No such container"))
        {
            return new DirectoryNotFoundException($"No such container: {path.Authority}");
        }
        if (Has("executable file not found") || Has("no such file or directory: unknown") || Has("OCI runtime exec failed"))
        {
            return new IOException("The container has no shell (sh), so its files cannot be listed.");
        }
        // ("sh: can't create /etc/x: Permission denied": the reason decides, not the verb)
        if (Has("Permission denied") || Has("Operation not permitted") || Has("Read-only file system"))
        {
            return new UnauthorizedAccessException($"Access denied: {path.Path}");
        }
        if (Has("No such file") || Has("does not exist") || Has("can't cd"))
        {
            return new FileNotFoundException($"Not found: {path.Path}", path.Path);
        }
        return new IOException(line.Length > 0 ? $"docker: {line}" : $"docker: the command failed (exit code {result.ExitCode}).");
    }

    private async Task<string> RunAsync(VfsPath path, IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(arguments, input, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : throw ErrorOf(result, path);
    }

    /// <summary>
    /// Where a path is: the container that runs the commands and the path inside it (a volume's: its helper container
    /// and the path under the mount). Container null: a list (the containers, the volumes).
    /// </summary>
    private readonly record struct Target(string? Container, string Path, bool IsVolume);

    private async Task<Target> TargetAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0)
        {
            return new Target(null, "/", false);
        }
        if (path.Authority != VolumesAuthority)
        {
            return new Target(path.Authority, path.Path, false);
        }
        var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new Target(null, "/", true);
        }
        var helper = await HelperAsync(parts[0], cancellationToken).ConfigureAwait(false);
        return new Target(helper, VolumeMount + "/" + string.Join('/', parts.Skip(1)), true);
    }

    /// <summary>A shell command inside the container.</summary>
    private Task<string> ShellAsync(Target target, VfsPath path, string command, CancellationToken cancellationToken) =>
        RunAsync(path, Exec(target, interactive: false, "sh", "-c", command), null, cancellationToken);

    /// <summary>"exec [-i] [-u user] container command...": as the user the settings name (a volume's helper: its own), else the container's own.</summary>
    private string[] Exec(Target target, bool interactive, params string[] command) =>
        [
            "exec",
            .. interactive ? (string[])["-i"] : [],
            .. !target.IsVolume && ExecUser is { Length: > 0 } user ? (string[])["-u", user] : [],
            target.Container!,
            .. command,
        ];

    // ---------------------------------------------------------------- the containers

    /// <summary>The lines of "docker ps -a --format {{json .}}": one JSON object per container.</summary>
    internal static IEnumerable<Container> ParseContainers(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Container? container = null;
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                string Field(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
                // Names may list several, comma separated (links); the first is the container's own
                var name = Field("Names").Split(',')[0].Trim();
                if (name.Length > 0)
                {
                    container = new Container(name, Field("State"), Field("Image"), ParseCreated(Field("CreatedAt")));
                }
            }
            catch (JsonException)
            {
            }
            if (container is not null)
            {
                yield return container;
            }
        }
    }

    /// <summary>docker's "2026-11-22 10:15:30 +0300 +03" (Go's time format) as local time; MinValue when it cannot be read.</summary>
    internal static DateTime ParseCreated(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[2].Length == 5)
        {
            var offset = parts[2][..3] + ":" + parts[2][3..];
            if (DateTimeOffset.TryParseExact($"{parts[0]} {parts[1]} {offset}", "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return time.LocalDateTime;
            }
        }
        return DateTime.MinValue;
    }

    internal async Task<IReadOnlyList<Container>> ContainersAsync(CancellationToken cancellationToken)
    {
        var output = await RunAsync(Root, ["ps", "-a", "--no-trunc", "--format", "{{json .}}"], null, cancellationToken).ConfigureAwait(false);
        // our own volume helpers are not the user's containers
        var containers = ParseContainers(output).Where(c => !c.Name.StartsWith(HelperPrefix, StringComparison.Ordinal))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var container in containers)
        {
            _states[container.Name] = container.State;
        }
        return containers;
    }

    /// <summary>The names of "docker volume ls --format {{json .}}".</summary>
    internal static IEnumerable<string> ParseVolumes(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? name = null;
            try
            {
                using var json = JsonDocument.Parse(line);
                name = json.RootElement.TryGetProperty("Name", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch (JsonException)
            {
            }
            if (!string.IsNullOrEmpty(name))
            {
                yield return name;
            }
        }
    }

    // ---------------------------------------------------------------- volumes: helper containers

    internal const string HelperPrefix = "nc-volume-";

    /// <summary>The helper container of a volume, started when there is none (or it is about to end).</summary>
    private async Task<string> HelperAsync(string volume, CancellationToken cancellationToken)
    {
        var name = HelperPrefix + volume;
        if (_helpers.TryGetValue(volume, out var started) && DateTime.UtcNow - started < s_helperLife)
        {
            return name;
        }
        await _helperLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_helpers.TryGetValue(volume, out started) && DateTime.UtcNow - started < s_helperLife)
            {
                return name;
            }
            var path = VfsPath.Create(DockerScheme, VolumesAuthority, "/" + volume);
            await Runner.RunAsync(["rm", "-f", name], null, cancellationToken).ConfigureAwait(false); // an older one, if any
            var run = await Runner.RunAsync(["run", "-d", "--rm", "--name", name, "--label", "nextcommander=volume-helper", "-v", volume + ":" + VolumeMount,
                HelperImage, "sleep", "3600"], null, cancellationToken).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                var reason = ErrorOf(run, path).Message;
                throw new IOException($"The volume could not be opened: no helper container from the image {HelperImage} ({reason}).");
            }
            _helpers[volume] = DateTime.UtcNow;
            return name;
        }
        finally
        {
            _helperLock.Release();
        }
    }

    /// <summary>Removes the helper containers this run started.</summary>
    public void Dispose()
    {
        if (CurrentRunner is { } runner)
        {
            foreach (var volume in _helpers.Keys)
            {
                try
                {
                    runner.RunAsync(["rm", "-f", HelperPrefix + volume], null, CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
                }
                catch (AggregateException)
                {
                }
            }
        }
        _helpers.Clear();
        _helperLock.Dispose();
    }

    // ---------------------------------------------------------------- reading

    private static VfsEntry EntryOf(VfsPath path, (string Name, bool IsDirectory, bool IsLink, long Size, DateTime Modified) item) => new()
    {
        Name = item.Name,
        Path = path,
        IsDirectory = item.IsDirectory,
        IsLink = item.IsLink,
        Size = item.IsDirectory ? 0 : item.Size,
        LastWriteTime = item.Modified,
    };

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        if (directory.Authority.Length == 0)
        {
            var containers = await ContainersAsync(cancellationToken).ConfigureAwait(false);
            return
            [
                new VfsEntry { Name = VolumesFolder, Path = VolumesRoot, IsDirectory = true, LastWriteTime = DateTime.MinValue },
                .. containers.Select(c => new VfsEntry { Name = c.Name, Path = VfsPath.Create(DockerScheme, c.Name, "/"), IsDirectory = true, LastWriteTime = c.Created }),
            ];
        }
        var target = await TargetAsync(directory, cancellationToken).ConfigureAwait(false);
        if (target.Container is null)
        {
            var output = await RunAsync(directory, ["volume", "ls", "--format", "{{json .}}"], null, cancellationToken).ConfigureAwait(false);
            return [.. ParseVolumes(output).Order(StringComparer.OrdinalIgnoreCase)
                .Select(v => new VfsEntry { Name = v, Path = VfsPath.Create(DockerScheme, VolumesAuthority, "/" + v), IsDirectory = true, LastWriteTime = DateTime.MinValue })];
        }
        try
        {
            var output = await ShellAsync(target, directory, ListCommand(target.Path), cancellationToken).ConfigureAwait(false);
            return [.. ParseListing(output).Select(item => EntryOf(directory.Combine(item.Name), item))];
        }
        catch (ContainerStoppedException) when (!target.IsVolume)
        {
            return await ListStoppedAsync(directory, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0)
        {
            return new VfsEntry { Name = "Docker containers", Path = Root, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        if (path.Path == "/")
        {
            return new VfsEntry { Name = path.Authority == VolumesAuthority ? VolumesFolder : path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        var target = await TargetAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            var output = (await ShellAsync(target, path, StatCommand(target.Path), cancellationToken).ConfigureAwait(false)).Trim();
            return output.Length > 0 && ParseLine(output) is { } item ? EntryOf(path, item) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (ContainerStoppedException) when (!target.IsVolume && path.Parent is { } parent)
        {
            // a stopped container: its folder through docker cp
            return (await ListStoppedAsync(parent, cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e.Name == path.Name);
        }
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        if (entry.IsDirectory)
        {
            throw new IOException($"Not a file: {path.Path}");
        }
        var target = await TargetAsync(path, cancellationToken).ConfigureAwait(false);
        if (!target.IsVolume && !await IsRunningAsync(target.Container!, cancellationToken).ConfigureAwait(false))
        {
            return OpenStoppedFile(path);
        }
        return Runner.OpenOutput(Exec(target, interactive: false, "cat", "--", target.Path));
    }

    /// <summary>Whether a container runs now (docker inspect; an answer it does not understand counts as running).</summary>
    private async Task<bool> IsRunningAsync(string container, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(["inspect", "-f", "{{.State.Running}}", container], null, cancellationToken).ConfigureAwait(false);
        return !(result.ExitCode == 0 && result.Output.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- a stopped container: docker cp, read-only

    /// <summary>
    /// A folder of a stopped container: "docker cp container:/folder -" gives it as a tar stream with everything below
    /// it, of which only the direct entries are kept (a big folder takes as long as copying it).
    /// </summary>
    private async Task<IReadOnlyList<VfsEntry>> ListStoppedAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        await using var stream = Runner.OpenOutput(["cp", directory.Authority + ":" + directory.Path, "-"]);
        var entries = await Task.Run(() => ReadTarListing(stream, directory, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (entries is null)
        {
            throw new DirectoryNotFoundException($"Not found: {directory.Path}");
        }
        return entries;
    }

    /// <summary>The direct entries of a folder from docker cp's tar ("dir/", "dir/file"; the root: "/", "/file"); null when the stream is empty.</summary>
    internal static List<VfsEntry>? ReadTarListing(Stream tar, VfsPath directory, CancellationToken cancellationToken)
    {
        var root = directory.Path == "/";
        var entries = new List<VfsEntry>();
        var any = false;
        using var reader = new TarReader(tar, leaveOpen: true);
        try
        {
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                any = true;
                var parts = entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // the root: "/x" is a child; a folder: "folder/x" is one (its first part is the folder itself)
                var child = root ? parts.Length == 1 ? parts[0] : null : parts.Length == 2 ? parts[1] : null;
                if (child is null)
                {
                    continue;
                }
                var isDirectory = entry.EntryType == TarEntryType.Directory;
                entries.Add(new VfsEntry
                {
                    Name = child,
                    Path = directory.Combine(child),
                    IsDirectory = isDirectory,
                    IsLink = entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink,
                    LinkTarget = string.IsNullOrEmpty(entry.LinkName) ? null : entry.LinkName,
                    Size = isDirectory ? 0 : entry.Length,
                    LastWriteTime = entry.ModificationTime.LocalDateTime,
                });
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            // what was read is shown; a cut stream (the process ended) ends the listing
        }
        return any ? entries : null;
    }

    /// <summary>
    /// A file of a stopped container: docker cp gives it as a tar with one entry, whose data is the file. A link is
    /// followed here (docker cp -L resolves a relative link against the wrong folder), at most 8 times.
    /// </summary>
    private Stream OpenStoppedFile(VfsPath path)
    {
        var file = path.Path;
        for (var hops = 0; hops < 8; hops++)
        {
            var process = Runner.OpenOutput(["cp", path.Authority + ":" + file, "-"]);
            TarReader? reader = null;
            try
            {
                reader = new TarReader(process, leaveOpen: true);
                var entry = reader.GetNextEntry(copyData: false);
                if (entry is { EntryType: TarEntryType.SymbolicLink, LinkName: { Length: > 0 } link })
                {
                    file = ResolveLink(file, link);
                    reader.Dispose();
                    process.Dispose();
                    continue;
                }
                if (entry?.DataStream is not { } data)
                {
                    throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
                }
                return new OwnedStream(data, reader, process);
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                process.Dispose();
                if (ex is EndOfStreamException or InvalidDataException)
                {
                    throw new FileNotFoundException($"Not found: {path.Path}", path.Path, ex);
                }
                throw;
            }
        }
        throw new IOException($"Too many links: {path.Path}");
    }

    /// <summary>A link's target as an absolute path in the container ("../usr/lib/x" from "/etc/os-release" → "/usr/lib/x").</summary>
    internal static string ResolveLink(string linkPath, string target)
    {
        var parts = new List<string>();
        if (!target.StartsWith('/'))
        {
            parts.AddRange(linkPath.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1));
        }
        foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>A stream that closes what it comes from (the tar reader, the docker process) with it.</summary>
    private sealed class OwnedStream(Stream inner, params IDisposable[] owners) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

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
                foreach (var owner in owners)
                {
                    owner.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// The data is collected in a temporary file and sent through "exec -i" with cat when the stream is closed (an
    /// existing file keeps its owner and rights); then the time announced by the copy (<see cref="PrepareUpload"/>) is set.
    /// </summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        RequireContainer(path);
        var existing = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream((data, ct) => UploadAsync(path, data, ct), cancellationToken);
    }

    private async Task UploadAsync(VfsPath path, Stream data, CancellationToken cancellationToken)
    {
        data.Position = 0;
        var target = await TargetAsync(path, cancellationToken).ConfigureAwait(false);
        await RunAsync(path, Exec(target, interactive: true, "sh", "-c", "cat > " + Quote(target.Path)), data, cancellationToken).ConfigureAwait(false);
        if (_uploadTimes.TryRemove(path, out var time) && time.Year >= 1980)
        {
            await ShellAsync(target, path, TouchCommand(target.Path, time), cancellationToken).ConfigureAwait(false);
        }
    }

    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _uploadTimes[path] = lastWriteTime;

    // ---------------------------------------------------------------- changes

    /// <summary>Containers and volumes themselves are made and removed with docker; their files can be changed.</summary>
    private static void RequireContainer(VfsPath path)
    {
        var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Authority.Length == 0 || parts.Length == 0 || (path.Authority == VolumesAuthority && parts.Length < 2))
        {
            throw new NotSupportedException("Containers are made and removed with docker itself.");
        }
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        RequireContainer(path);
        var target = await TargetAsync(path, cancellationToken).ConfigureAwait(false);
        await ShellAsync(target, path, "mkdir -p -- " + Quote(target.Path), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        RequireContainer(path);
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        var target = await TargetAsync(path, cancellationToken).ConfigureAwait(false);
        var command = !entry.IsDirectory || entry.IsLink ? "rm -f -- " + Quote(target.Path)
            : recursive ? "rm -rf -- " + Quote(target.Path)
            : "rmdir -- " + Quote(target.Path);
        await ShellAsync(target, path, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        RequireContainer(source);
        var sameVolume = source.Authority == VolumesAuthority && destination.Authority == VolumesAuthority
            && source.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)[0] == destination.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (source.Scheme != destination.Scheme || source.Authority != destination.Authority || (source.Authority == VolumesAuthority && !sameVolume))
        {
            throw new CrossDeviceMoveException("Moving between containers is done by copying.");
        }
        if (await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && !string.Equals(source.Path, destination.Path, StringComparison.Ordinal)
            && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        var from = await TargetAsync(source, cancellationToken).ConfigureAwait(false);
        var to = await TargetAsync(destination, cancellationToken).ConfigureAwait(false);
        await ShellAsync(from, source, $"mv -f -- {Quote(from.Path)} {Quote(to.Path)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0 || path.Path == "/" || lastWriteTime is not { Year: >= 1980 } time)
        {
            return;
        }
        var target = await TargetAsync(path, cancellationToken).ConfigureAwait(false);
        if (target.Container is not null)
        {
            await ShellAsync(target, path, TouchCommand(target.Path, time), cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- the right click menu of a container

    public IReadOnlyList<PluginAction> ActionsFor(VfsEntry entry)
    {
        var path = entry.Path;
        if (path.Scheme != DockerScheme || path.Authority.Length == 0 || path.Authority == VolumesAuthority || path.Path != "/")
        {
            return [];
        }
        var name = path.Authority;
        PluginAction Action(string text, PluginActionIcon icon, params string[] command) =>
            new(text, icon, async ct =>
            {
                await RunAsync(path, [.. command, name], null, ct).ConfigureAwait(false);
                return null;
            });
        var state = _states.TryGetValue(name, out var known) ? known : string.Empty;
        return state.ToLowerInvariant() switch
        {
            "running" => [Action("Stop the container", PluginActionIcon.Stop, "stop"), Action("Restart the container", PluginActionIcon.Restart, "restart"),
                Action("Pause the container", PluginActionIcon.Pause, "pause")],
            "paused" => [Action("Resume the container", PluginActionIcon.Start, "unpause"), Action("Stop the container", PluginActionIcon.Stop, "stop")],
            "restarting" => [Action("Stop the container", PluginActionIcon.Stop, "stop")],
            _ => [Action("Start the container", PluginActionIcon.Start, "start")],
        };
    }

    // ---------------------------------------------------------------- settings (Configuration > Plugins > Docker)

    internal const string ProgramKey = "program";
    internal const string UserKey = "user";
    internal const string HelperImageKey = "helperImage";

    public string SettingsTitle => "Docker";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new(ProgramKey, PluginSettingKind.Program, "docker program")
        {
            Description = "Empty: FM_DOCKER, then docker on the PATH and in the usual folders. Another program with the same commands (podman) works too.",
            Placeholder = DockerTool.Find() ?? "docker",
        },
        new(UserKey, PluginSettingKind.Choice, "Work in containers as")
        {
            Description = "The user that lists, reads and writes the files inside a container.",
            Default = string.Empty,
            Choices = [(string.Empty, "The container's own user"), ("root", "root")],
        },
        new(HelperImageKey, PluginSettingKind.Text, "Image for opening volumes")
        {
            Description = "A volume is opened through a small container that mounts it; its image needs sh, ls, stat and sleep (downloaded the first time).",
            Default = DefaultHelperImage,
        },
    ];

    public string? Validate(IPluginSettingValues values) =>
        values.Get(ProgramKey) is { } program && !File.Exists(program) ? string.Format(CultureInfo.CurrentCulture, "The program was not found: {0}", program) : null;

    public void Apply(IPluginSettingValues values)
    {
        DockerTool.ConfiguredPath = values.Get(ProgramKey);
        ExecUser = values.Get(UserKey);
        var image = values.Get(HelperImageKey) ?? DefaultHelperImage;
        if (image != HelperImage)
        {
            HelperImage = image;
            _helpers.Clear(); // started again from the new image
        }
    }

    /// <summary>".." from a container's (or the volume list's) root goes back to the container list, onto it; from a volume to the volume list.</summary>
    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root)
    {
        if (root.Scheme != DockerScheme || root.Authority.Length == 0 || root.Path != "/")
        {
            return null;
        }
        return root.Authority == VolumesAuthority ? (Root, VolumesFolder) : (Root, root.Authority);
    }
}
