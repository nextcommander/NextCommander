using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using static NextCommander.Core.Vfs.PosixShell;

namespace NextCommander.Plugin.Docker;

/// <summary>
/// The files of Docker containers through the installed docker command line: <c>docker:///</c> lists the containers
/// (running and stopped), <c>docker://&lt;container&gt;/etc/...</c> is a path inside one. Folders are listed with the
/// container's shell (<see cref="PosixShell"/>), files are read with cat and written through "exec -i" (so a file keeps
/// its owner and the container user's rights apply); changes are shell commands. A container must be running; a
/// stopped one says how to start it. Its entry point "Docker containers" is listed with the saved connections.
/// </summary>
public sealed class DockerFileSystemProvider : IFileSystemProvider, IUploadTimeSupport, IPluginEntryPoint, IOriginProvider, IConfigurablePlugin
{
    public const string DockerScheme = "docker";

    /// <summary>The container list: <c>docker:///</c>.</summary>
    public static VfsPath Root { get; } = VfsPath.Create(DockerScheme, string.Empty, "/");

    private readonly IDockerRunner? _fixedRunner;
    private readonly bool _installed;

    /// <summary>The user commands in a container run as (null: the container's own user); the plugin's settings.</summary>
    internal string? ExecUser { get; set; }
    private readonly ConcurrentDictionary<VfsPath, DateTime> _uploadTimes = new();

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
            return new IOException($"The container is not running: start it (docker start {path.Authority}) to work with its files.");
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

    /// <summary>A shell command inside the container.</summary>
    private Task<string> ShellAsync(VfsPath path, string command, CancellationToken cancellationToken) =>
        RunAsync(path, Exec(path, "sh", "-c", command), null, cancellationToken);

    /// <summary>"exec [-i] [-u user] container command...": as the user the settings name, else the container's own.</summary>
    private string[] Exec(VfsPath path, params string[] command) => Exec(path, interactive: false, command);

    private string[] Exec(VfsPath path, bool interactive, params string[] command) =>
        [
            "exec",
            .. interactive ? (string[])["-i"] : [],
            .. ExecUser is { Length: > 0 } user ? (string[])["-u", user] : [],
            path.Authority,
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
        return [.. ParseContainers(output).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
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
            return [.. containers.Select(c => new VfsEntry { Name = c.Name, Path = VfsPath.Create(DockerScheme, c.Name, "/"), IsDirectory = true, LastWriteTime = c.Created })];
        }
        var output = await ShellAsync(directory, ListCommand(directory.Path), cancellationToken).ConfigureAwait(false);
        return [.. ParseListing(output).Select(item => EntryOf(directory.Combine(item.Name), item))];
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Authority.Length == 0)
        {
            return new VfsEntry { Name = "Docker containers", Path = Root, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        if (path.Path == "/")
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.MinValue };
        }
        try
        {
            var output = (await ShellAsync(path, StatCommand(path.Path), cancellationToken).ConfigureAwait(false)).Trim();
            return output.Length > 0 && ParseLine(output) is { } item ? EntryOf(path, item) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        if (entry.IsDirectory)
        {
            throw new IOException($"Not a file: {path.Path}");
        }
        return Runner.OpenOutput(Exec(path, "cat", "--", path.Path));
    }

    /// <summary>
    /// The data is collected in a temporary file and sent through "exec -i" with cat when the stream is closed (an
    /// existing file keeps its owner and rights); then the time announced by the copy (<see cref="PrepareUpload"/>) is set.
    /// </summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
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
        await RunAsync(path, Exec(path, interactive: true, "sh", "-c", "cat > " + Quote(path.Path)), data, cancellationToken).ConfigureAwait(false);
        if (_uploadTimes.TryRemove(path, out var time) && time.Year >= 1980)
        {
            await ShellAsync(path, TouchCommand(path.Path, time), cancellationToken).ConfigureAwait(false);
        }
    }

    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _uploadTimes[path] = lastWriteTime;

    // ---------------------------------------------------------------- changes

    private static void RequireContainer(VfsPath path)
    {
        if (path.Authority.Length == 0 || path.Path == "/")
        {
            throw new NotSupportedException("Containers are made and removed with docker itself.");
        }
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        RequireContainer(path);
        return ShellAsync(path, "mkdir -p -- " + Quote(path.Path), cancellationToken);
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        RequireContainer(path);
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        var command = !entry.IsDirectory || entry.IsLink ? "rm -f -- " + Quote(path.Path)
            : recursive ? "rm -rf -- " + Quote(path.Path)
            : "rmdir -- " + Quote(path.Path);
        await ShellAsync(path, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        RequireContainer(source);
        if (source.Scheme != destination.Scheme || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between containers is done by copying.");
        }
        if (await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && !string.Equals(source.Path, destination.Path, StringComparison.Ordinal)
            && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        await ShellAsync(source, $"mv -f -- {Quote(source.Path)} {Quote(destination.Path)}", cancellationToken).ConfigureAwait(false);
    }

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) =>
        path.Authority.Length > 0 && path.Path != "/" && lastWriteTime is { Year: >= 1980 } time
            ? ShellAsync(path, TouchCommand(path.Path, time), cancellationToken)
            : Task.CompletedTask;

    // ---------------------------------------------------------------- settings (Configuration > Plugins > Docker)

    internal const string ProgramKey = "program";
    internal const string UserKey = "user";

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
    ];

    public string? Validate(IPluginSettingValues values) =>
        values.Get(ProgramKey) is { } program && !File.Exists(program) ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "The program was not found: {0}", program) : null;

    public void Apply(IPluginSettingValues values)
    {
        DockerTool.ConfiguredPath = values.Get(ProgramKey);
        ExecUser = values.Get(UserKey);
    }

    /// <summary>".." from a container's root goes back to the container list, with the cursor on the container.</summary>
    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root) =>
        root.Scheme == DockerScheme && root.Authority.Length > 0 && root.Path == "/" ? (Root, root.Authority) : null;
}
