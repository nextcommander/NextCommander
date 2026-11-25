using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using static NextCommander.Core.Vfs.PosixShell;

namespace NextCommander.Plugin.Kubernetes;

/// <summary>
/// The files of Kubernetes containers through the installed kubectl, one level per step of the cluster:
/// <list type="bullet">
/// <item><c>k8s:///</c> lists the contexts of the kubeconfig (the current one is marked in the Kubernetes columns).</item>
/// <item><c>k8s://&lt;context&gt;/</c> lists its namespaces (only the context's own one when the account may not list them).</item>
/// <item><c>k8s://&lt;context&gt;/&lt;namespace&gt;/</c> lists its pods, with the time they were made (status, ready
/// containers, restarts and node in the Kubernetes columns).</item>
/// <item><c>k8s://&lt;context&gt;/&lt;namespace&gt;/&lt;pod&gt;/</c> lists the pod's containers, always, also when there is
/// only one: a path then says by itself which container it is in, and a pod that gets a second container (a sidecar)
/// keeps its paths.</item>
/// <item><c>k8s://&lt;context&gt;/&lt;namespace&gt;/&lt;pod&gt;/&lt;container&gt;/etc/...</c> is a path inside a running
/// container: listed with its shell (<see cref="PosixShell"/>), read with cat and written through "exec -i" (a file
/// keeps its owner, the container user's rights apply); changes are shell commands.</item>
/// </list>
/// A context's name keeps its '/' (an EKS context is an ARN with "cluster/name") as '|' in the path. The pods' logs,
/// description, deletion and the restart of their deployment are actions of the file list's menu and the Plugins menu.
/// Its entry point "Kubernetes" is listed with the saved connections.
/// </summary>
public sealed class KubernetesFileSystemProvider : IFileSystemProvider, IUploadTimeSupport, IPluginEntryPoint, IOriginProvider, IConfigurablePlugin,
    IPluginFolderActions
{
    public const string KubernetesScheme = "k8s";

    /// <summary>The context list: <c>k8s:///</c>.</summary>
    public static VfsPath Root { get; } = VfsPath.Create(KubernetesScheme, string.Empty, "/");

    private readonly IKubectlRunner? _fixedRunner;
    private readonly bool _installed;
    private readonly ConcurrentDictionary<VfsPath, DateTime> _uploadTimes = new();

    /// <summary>The pods of the last listings (their containers and owners: the menu's actions), by the pod's folder.</summary>
    private readonly ConcurrentDictionary<VfsPath, Pod> _pods = new();

    /// <summary>The deployment each replica set belongs to ("context/namespace/replica set" → deployment), from the pod listings.</summary>
    private readonly ConcurrentDictionary<string, string> _deployments = new(StringComparer.Ordinal);

    /// <summary>The contexts of the kubeconfig at the last reading, by name.</summary>
    private readonly ConcurrentDictionary<string, Context> _contexts = new(StringComparer.Ordinal);

    /// <summary>What the Kubernetes columns show for a context, pod or container (shared with <see cref="KubernetesColumns"/>).</summary>
    internal static ConcurrentDictionary<VfsPath, Details> Shown { get; } = new();

    /// <summary>The kubeconfig file the settings name (null: what kubectl reads itself: KUBECONFIG, ~/.kube/config).</summary>
    internal string? Kubeconfig { get; set; }

    /// <summary>How many of a pod's last log lines are shown; the plugin's settings.</summary>
    internal int LogLines { get; set; } = DefaultLogLines;

    internal const int DefaultLogLines = 500;

    /// <summary>The plugin loader's constructor: the installed kubectl.</summary>
    public KubernetesFileSystemProvider() => _installed = true;

    /// <param name="runner">The kubectl program (null: not installed); a fake one in tests.</param>
    public KubernetesFileSystemProvider(IKubectlRunner? runner) => _fixedRunner = runner;

    /// <summary>The kubectl in use: for the loader's instance the one the settings name now, else the one given.</summary>
    private IKubectlRunner? CurrentRunner => _installed ? KubectlTool.Installed : _fixedRunner;

    public string EntryName => "Kubernetes";

    public string EntryGroup => "Kubernetes";

    public VfsPath EntryRoot => Root;

    public string? Unavailable => CurrentRunner is null ? NotInstalled : null;

    internal const string NotInstalled = "kubectl is not installed. Install kubectl (or set FM_KUBECTL to the kubectl program, or name it in the plugin's settings), then open this again.";

    public string Scheme => KubernetesScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read | ProviderCapabilities.Write | ProviderCapabilities.Rename | ProviderCapabilities.SetTimes;

    /// <summary>A context of the kubeconfig.</summary>
    internal sealed record Context(string Name, string Cluster, string? Namespace, bool IsCurrent);

    /// <summary>A container of a pod: its state as kubectl tells it ("Running", "CrashLoopBackOff" ...).</summary>
    internal sealed record Container(string Name, string State, bool Ready, int Restarts, DateTime Started);

    /// <summary>A pod: its status as kubectl's STATUS column shows it, its containers and what made it.</summary>
    internal sealed record Pod(string Name, string Status, DateTime Created, IReadOnlyList<Container> Containers, string Node, string? OwnerKind, string? OwnerName)
    {
        public int ReadyCount => Containers.Count(c => c.Ready);

        public int Restarts => Containers.Sum(c => c.Restarts);
    }

    /// <summary>What the columns show: a status, the ready containers ("1/2"), the restarts, the node.</summary>
    internal sealed record Details(string Status, string? Ready = null, int? Restarts = null, string? Node = null);

    /// <summary>The cluster's role-based access refused the account (not a login that failed).</summary>
    internal sealed class ForbiddenException(string message) : UnauthorizedAccessException(message);

    /// <summary>A container that is not running (or a pod that ended): its files cannot be reached.</summary>
    internal sealed class ContainerStoppedException(string message) : IOException(message);

    private IKubectlRunner Runner => CurrentRunner ?? throw new IOException(NotInstalled);

    // ---------------------------------------------------------------- where a path is

    /// <summary>A context's name in a path: '/' (an EKS ARN's "cluster/name") as '|'.</summary>
    internal static string Encode(string context) => context.Replace('/', '|');

    internal static string Decode(string authority) => authority.Replace('|', '/');

    /// <summary>
    /// The parts of a path: the context (null: the context list), the namespace, the pod, the container (each null above
    /// its level) and the path inside the container.
    /// </summary>
    internal readonly record struct Location(string? Context, string? Namespace, string? Pod, string? Container, string Path)
    {
        /// <summary>0 the contexts, 1 a context's namespaces, 2 a namespace's pods, 3 a pod's containers, 4 a container's files.</summary>
        public int Level => Context is null ? 0 : Namespace is null ? 1 : Pod is null ? 2 : Container is null ? 3 : 4;
    }

    internal static Location LocationOf(VfsPath path)
    {
        if (path.Authority.Length == 0)
        {
            return new Location(null, null, null, null, "/");
        }
        var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? Part(int index) => parts.Length > index ? parts[index] : null;
        return new Location(Decode(path.Authority), Part(0), Part(1), Part(2), "/" + string.Join('/', parts.Skip(3)));
    }

    private static VfsPath PathOf(string context, params string[] parts) => VfsPath.Create(KubernetesScheme, Encode(context), "/" + string.Join('/', parts));

    // ---------------------------------------------------------------- running kubectl

    /// <summary>The message of a kubectl failure as the matching exception.</summary>
    internal static Exception ErrorOf(KubectlResult result, VfsPath path)
    {
        var text = (result.Error + "\n" + result.Output).Trim();
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        bool Has(string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
        var where = LocationOf(path);
        var context = where.Context ?? string.Empty;
        if (Has("context was not found") || Has("no context exists"))
        {
            return new DirectoryNotFoundException($"No such context in the kubeconfig: {context}");
        }
        if (Has("Unable to connect to the server") || Has("connection refused") || Has("no such host") || Has("i/o timeout")
            || Has("Client.Timeout exceeded") || Has("context deadline exceeded"))
        {
            return new IOException($"The cluster of the context {context} does not answer: check the network (a VPN?) and that the cluster runs, then try again.");
        }
        if (Has("You must be logged in") || Has("(Unauthorized)") || Has("provide credentials") || Has("token has expired") || Has("getting credentials"))
        {
            return new UnauthorizedAccessException($"The cluster did not accept the login of the context {context}: log in again (renew the credentials of the kubeconfig), then try again.");
        }
        // before "forbidden": exec into an ended pod is refused with these words
        if (Has("cannot exec into a container in a completed pod") || Has("container not found") || Has("is not running")
            || Has("is waiting to start") || Has("ContainerCreating"))
        {
            return new ContainerStoppedException($"The container is not running in the pod {where.Pod}: its files can be reached only while it runs (the pod's logs may tell why).");
        }
        if (Has("(Forbidden)") || Has(" is forbidden"))
        {
            return new ForbiddenException($"The cluster refused it: the account of the context {context} has no right to do this. Ask the cluster's administrator for a role that allows it.");
        }
        if (Has("(NotFound)") || Has("pod does not exist"))
        {
            return new DirectoryNotFoundException($"Not found in the cluster: {line}");
        }
        if (Has("executable file not found") || Has("no such file or directory: unknown") || Has("OCI runtime exec failed"))
        {
            return new IOException("The container has no shell (sh), so its files cannot be listed.");
        }
        // ("sh: can't create /etc/x: Permission denied": the reason decides, not the verb)
        if (Has("Permission denied") || Has("Operation not permitted") || Has("Read-only file system"))
        {
            return new UnauthorizedAccessException($"Access denied: {where.Path}");
        }
        if (Has("No such file") || Has("does not exist") || Has("can't cd"))
        {
            return new FileNotFoundException($"Not found: {where.Path}", where.Path);
        }
        return new IOException(line.Length > 0 ? $"kubectl: {line}" : $"kubectl: the command failed (exit code {result.ExitCode}).");
    }

    /// <summary>The arguments of a kubectl command: the kubeconfig of the settings, the context and the namespace first.</summary>
    private string[] Kubectl(string? context, string? @namespace, params string[] arguments) =>
        [
            .. Kubeconfig is { Length: > 0 } file ? (string[])["--kubeconfig", file] : [],
            .. context is not null ? (string[])["--context", context] : [],
            .. @namespace is not null ? (string[])["-n", @namespace] : [],
            .. arguments,
        ];

    /// <summary>A "get" of the cluster's objects as JSON (a cluster that does not answer gives up after 30 seconds).</summary>
    private string[] Get(string context, string? @namespace, params string[] what) =>
        Kubectl(context, @namespace, ["get", .. what, "-o", "json", "--request-timeout=30s"]);

    private async Task<string> RunAsync(VfsPath path, IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(arguments, input, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : throw ErrorOf(result, path);
    }

    /// <summary>"exec [-i] pod -c container -- command...".</summary>
    private string[] Exec(Location where, bool interactive, params string[] command) =>
        Kubectl(where.Context, where.Namespace, ["exec", .. interactive ? (string[])["-i"] : [], where.Pod!, "-c", where.Container!, "--", .. command]);

    /// <summary>A shell command inside the container.</summary>
    private Task<string> ShellAsync(Location where, VfsPath path, string command, CancellationToken cancellationToken) =>
        RunAsync(path, Exec(where, interactive: false, "sh", "-c", command), null, cancellationToken);

    // ---------------------------------------------------------------- reading kubectl's JSON

    private static string Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return string.Empty;
            }
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return [];
            }
        }
        return element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToList() : [];
    }

    /// <summary>Kubernetes' "2026-11-22T10:15:30Z" as local time; MinValue when there is none.</summary>
    internal static DateTime ParseTime(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time.LocalDateTime : DateTime.MinValue;

    /// <summary>The contexts of "kubectl config view -o json", the current one marked.</summary>
    internal static List<Context> ParseContexts(string output)
    {
        using var json = JsonDocument.Parse(output);
        var current = Text(json.RootElement, "current-context");
        return
        [
            .. Items(json.RootElement, "contexts")
                .Select(c => (Name: Text(c, "name"), Cluster: Text(c, "context", "cluster"), Namespace: Text(c, "context", "namespace")))
                .Where(c => c.Name.Length > 0)
                .Select(c => new Context(c.Name, c.Cluster, c.Namespace.Length > 0 ? c.Namespace : null, c.Name == current)),
        ];
    }

    /// <summary>The names and times of "kubectl get namespaces -o json".</summary>
    internal static List<(string Name, DateTime Created, string Phase)> ParseNamespaces(string output)
    {
        using var json = JsonDocument.Parse(output);
        return
        [
            .. Items(json.RootElement, "items")
                .Select(n => (Text(n, "metadata", "name"), ParseTime(Text(n, "metadata", "creationTimestamp")), Text(n, "status", "phase")))
                .Where(n => n.Item1.Length > 0),
        ];
    }

    /// <summary>The pods of "kubectl get pods -o json" (a list), or the one pod of "kubectl get pod name -o json".</summary>
    internal static List<Pod> ParsePods(string output)
    {
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        IEnumerable<JsonElement> items = Text(root, "kind") == "Pod" ? [root] : Items(root, "items");
        return [.. items.Select(ParsePod).Where(p => p.Name.Length > 0)];
    }

    private static Pod ParsePod(JsonElement item)
    {
        var statuses = Items(item, "status", "containerStatuses").ToDictionary(s => Text(s, "name"), StringComparer.Ordinal);
        var containers = new List<Container>();
        foreach (var spec in Items(item, "spec", "containers"))
        {
            var name = Text(spec, "name");
            if (name.Length == 0)
            {
                continue;
            }
            if (!statuses.TryGetValue(name, out var status))
            {
                containers.Add(new Container(name, "Waiting", false, 0, DateTime.MinValue));
                continue;
            }
            var ready = status.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
            var restarts = status.TryGetProperty("restartCount", out var n) && n.TryGetInt32(out var count) ? count : 0;
            string state;
            var started = DateTime.MinValue;
            if (status.TryGetProperty("state", out var s) && s.TryGetProperty("running", out var running))
            {
                state = "Running";
                started = ParseTime(Text(running, "startedAt"));
            }
            else if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("waiting", out var waiting))
            {
                state = Text(waiting, "reason") is { Length: > 0 } reason ? reason : "Waiting";
            }
            else if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("terminated", out var terminated))
            {
                state = Text(terminated, "reason") is { Length: > 0 } reason ? reason : "Terminated";
                started = ParseTime(Text(terminated, "startedAt"));
            }
            else
            {
                state = "Waiting";
            }
            containers.Add(new Container(name, state, ready, restarts, started));
        }
        // as kubectl's STATUS column: being deleted, the pod's reason (Evicted), a container's reason, else the phase
        var podStatus = Text(item, "metadata", "deletionTimestamp").Length > 0 ? "Terminating"
            : Text(item, "status", "reason") is { Length: > 0 } podReason ? podReason
            : containers.FirstOrDefault(c => c.State is not ("Running" or "Waiting" or "Completed" or "Terminated"))?.State
            ?? (Text(item, "status", "phase") is "Succeeded" ? "Completed" : Text(item, "status", "phase"));
        var owner = Items(item, "metadata", "ownerReferences").FirstOrDefault(o => o.TryGetProperty("controller", out var c) && c.ValueKind == JsonValueKind.True);
        if (owner.ValueKind != JsonValueKind.Object)
        {
            owner = Items(item, "metadata", "ownerReferences").FirstOrDefault();
        }
        return new Pod(Text(item, "metadata", "name"), podStatus, ParseTime(Text(item, "metadata", "creationTimestamp")), containers,
            Text(item, "spec", "nodeName"), owner.ValueKind == JsonValueKind.Object ? Text(owner, "kind") : null,
            owner.ValueKind == JsonValueKind.Object ? Text(owner, "name") : null);
    }

    /// <summary>The deployment of each replica set of "kubectl get replicasets -o json" (replica set → deployment).</summary>
    internal static Dictionary<string, string> ParseReplicaSetOwners(string output)
    {
        using var json = JsonDocument.Parse(output);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var set in Items(json.RootElement, "items"))
        {
            if (Items(set, "metadata", "ownerReferences").FirstOrDefault(o => Text(o, "kind") == "Deployment") is { ValueKind: JsonValueKind.Object } owner)
            {
                owners[Text(set, "metadata", "name")] = Text(owner, "name");
            }
        }
        return owners;
    }

    // ---------------------------------------------------------------- the levels above the files

    internal async Task<IReadOnlyList<Context>> ContextsAsync(CancellationToken cancellationToken)
    {
        var output = await RunAsync(Root, Kubectl(null, null, "config", "view", "-o", "json"), null, cancellationToken).ConfigureAwait(false);
        List<Context> contexts;
        try
        {
            contexts = ParseContexts(output);
        }
        catch (JsonException)
        {
            throw new IOException("kubectl gave a kubeconfig it could not read itself: check the kubeconfig file.");
        }
        if (contexts.Count == 0)
        {
            throw new IOException("The kubeconfig has no contexts: add a cluster with its own tool (for example az aks get-credentials, aws eks update-kubeconfig, gcloud container clusters get-credentials, kind or minikube), then open this again.");
        }
        foreach (var context in contexts)
        {
            _contexts[context.Name] = context;
        }
        return [.. contexts.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<IReadOnlyList<VfsEntry>> NamespacesAsync(VfsPath directory, string context, CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunAsync(directory, Get(context, null, "namespaces"), null, cancellationToken).ConfigureAwait(false);
            return [.. ParseNamespaces(output).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Select(n =>
                new VfsEntry { Name = n.Name, Path = PathOf(context, n.Name), IsDirectory = true, LastWriteTime = n.Created })];
        }
        catch (ForbiddenException)
        {
            // an account that may not list the namespaces works in its context's own one
            if (!_contexts.ContainsKey(context))
            {
                await TryContextsAsync(cancellationToken).ConfigureAwait(false);
            }
            var own = _contexts.TryGetValue(context, out var known) && known.Namespace is { } ns ? ns : "default";
            return [new VfsEntry { Name = own, Path = PathOf(context, own), IsDirectory = true, LastWriteTime = DateTime.MinValue }];
        }
    }

    private async Task TryContextsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ContextsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // no namespace known: "default"
        }
    }

    internal async Task<IReadOnlyList<Pod>> PodsAsync(VfsPath directory, Location where, CancellationToken cancellationToken)
    {
        // the replica sets alongside: which deployment a pod belongs to (an account that may not read them gets no restart)
        var sets = Runner.RunAsync(Get(where.Context!, where.Namespace, "replicasets"), null, cancellationToken);
        var output = await RunAsync(directory, Get(where.Context!, where.Namespace, "pods"), null, cancellationToken).ConfigureAwait(false);
        var pods = ParsePods(output).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            if (await sets.ConfigureAwait(false) is { ExitCode: 0 } result)
            {
                foreach (var (set, deployment) in ParseReplicaSetOwners(result.Output))
                {
                    _deployments[$"{where.Context}/{where.Namespace}/{set}"] = deployment;
                }
            }
        }
        catch (JsonException)
        {
        }
        foreach (var pod in pods)
        {
            Remember(where.Context!, where.Namespace!, pod);
        }
        return pods;
    }

    private void Remember(string context, string @namespace, Pod pod)
    {
        var path = PathOf(context, @namespace, pod.Name);
        _pods[path] = pod;
        Shown[path] = new Details(pod.Status, $"{pod.ReadyCount}/{pod.Containers.Count}", pod.Restarts, pod.Node);
        foreach (var container in pod.Containers)
        {
            Shown[path.Combine(container.Name)] = new Details(container.State, container.Ready ? "1/1" : "0/1", container.Restarts, pod.Node);
        }
    }

    private async Task<Pod> PodAsync(VfsPath directory, Location where, CancellationToken cancellationToken)
    {
        var output = await RunAsync(directory, Get(where.Context!, where.Namespace, "pod", where.Pod!), null, cancellationToken).ConfigureAwait(false);
        var pod = ParsePods(output).FirstOrDefault() ?? throw new DirectoryNotFoundException($"Not found in the cluster: {where.Pod}");
        Remember(where.Context!, where.Namespace!, pod);
        return pod;
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

    private static VfsEntry Folder(string name, VfsPath path, DateTime time) => new() { Name = name, Path = path, IsDirectory = true, LastWriteTime = time };

    public async Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken)
    {
        var where = LocationOf(directory);
        switch (where.Level)
        {
            case 0:
                var contexts = await ContextsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var context in contexts)
                {
                    Shown[PathOf(context.Name)] = new Details(context.IsCurrent ? "Current context" : string.Empty);
                }
                return [.. contexts.Select(c => Folder(Encode(c.Name), PathOf(c.Name), DateTime.MinValue))];
            case 1:
                return await NamespacesAsync(directory, where.Context!, cancellationToken).ConfigureAwait(false);
            case 2:
                var pods = await PodsAsync(directory, where, cancellationToken).ConfigureAwait(false);
                return [.. pods.Select(p => Folder(p.Name, directory.Combine(p.Name), p.Created))];
            case 3:
                var pod = await PodAsync(directory, where, cancellationToken).ConfigureAwait(false);
                return [.. pod.Containers.Select(c => Folder(c.Name, directory.Combine(c.Name), c.Started == DateTime.MinValue ? pod.Created : c.Started))];
        }
        var output = await ShellAsync(where, directory, ListCommand(where.Path), cancellationToken).ConfigureAwait(false);
        return [.. ParseListing(output).Select(item => EntryOf(directory.Combine(item.Name), item))];
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var where = LocationOf(path);
        switch (where.Level)
        {
            case 0:
                return Folder("Kubernetes", Root, DateTime.MinValue);
            case 1:
            case 2:
            case 3:
                return Folder(where.Level == 1 ? path.Authority : path.Name, path, DateTime.MinValue);
        }
        if (where.Path == "/")
        {
            return Folder(where.Container!, path, DateTime.MinValue);
        }
        try
        {
            var output = (await ShellAsync(where, path, StatCommand(where.Path), cancellationToken).ConfigureAwait(false)).Trim();
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
        var where = LocationOf(path);
        return Runner.OpenOutput(Exec(where, interactive: false, "cat", "--", where.Path));
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
        var where = LocationOf(path);
        await RunAsync(path, Exec(where, interactive: true, "sh", "-c", "cat > " + Quote(where.Path)), data, cancellationToken).ConfigureAwait(false);
        if (_uploadTimes.TryRemove(path, out var time) && time.Year >= 1980)
        {
            await ShellAsync(where, path, TouchCommand(where.Path, time), cancellationToken).ConfigureAwait(false);
        }
    }

    public void PrepareUpload(VfsPath path, DateTime lastWriteTime) => _uploadTimes[path] = lastWriteTime;

    // ---------------------------------------------------------------- changes

    /// <summary>Contexts, namespaces, pods and containers themselves are made and removed with kubectl; a container's files can be changed.</summary>
    private static void RequireContainer(VfsPath path)
    {
        var where = LocationOf(path);
        if (where.Level < 4 || where.Path == "/")
        {
            throw new NotSupportedException("Namespaces, pods and containers are made and removed with kubectl itself.");
        }
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        RequireContainer(path);
        var where = LocationOf(path);
        await ShellAsync(where, path, "mkdir -p -- " + Quote(where.Path), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        RequireContainer(path);
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}", path.Path);
        var where = LocationOf(path);
        var command = !entry.IsDirectory || entry.IsLink ? "rm -f -- " + Quote(where.Path)
            : recursive ? "rm -rf -- " + Quote(where.Path)
            : "rmdir -- " + Quote(where.Path);
        await ShellAsync(where, path, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        RequireContainer(source);
        var origin = LocationOf(source);
        var target = LocationOf(destination);
        if (source.Scheme != destination.Scheme || origin with { Path = "/" } != target with { Path = "/" } || target.Level < 4)
        {
            throw new CrossDeviceMoveException("Moving between containers is done by copying.");
        }
        if (await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && !string.Equals(source.Path, destination.Path, StringComparison.Ordinal)
            && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        await ShellAsync(origin, source, $"mv -f -- {Quote(origin.Path)} {Quote(target.Path)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var where = LocationOf(path);
        if (where.Level < 4 || where.Path == "/" || lastWriteTime is not { Year: >= 1980 } time)
        {
            return;
        }
        await ShellAsync(where, path, TouchCommand(where.Path, time), cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- the pods' actions (file list menu, Plugins menu)

    /// <summary>
    /// In a namespace's pod list: the selected pods' logs, description, deletion and the restart of their deployment;
    /// inside a pod (its containers, a container's files): the logs (of that container) and the description of the pod.
    /// </summary>
    public IReadOnlyList<PluginAction> ActionsFor(VfsPath folder, IReadOnlyList<VfsEntry> selected, IPluginDialogs dialogs)
    {
        if (folder.Scheme != KubernetesScheme)
        {
            return [];
        }
        var here = LocationOf(folder);
        if (here.Level < 2)
        {
            return [];
        }
        if (here.Level >= 3)
        {
            // inside a pod: its own logs (of the container the folder is in, or the one selected) and description
            var podPath = PathOf(here.Context!, here.Namespace!, here.Pod!);
            var container = here.Container ?? (selected.Count == 1 && LocationOf(selected[0].Path) is { Level: 4, Path: "/" } chosen ? chosen.Container : null);
            return
            [
                new PluginAction("Pod logs", PluginActionIcon.Info, ct => LogsAsync(podPath, container, dialogs, ct)),
                new PluginAction("Describe the pod", PluginActionIcon.Info, ct => DescribeAsync(podPath, dialogs, ct)),
            ];
        }
        var pods = selected.Select(e => e.Path).Where(p => p.Scheme == KubernetesScheme && LocationOf(p).Level == 3 && p.Parent == folder).ToList();
        if (pods.Count == 0)
        {
            return [];
        }
        var actions = new List<PluginAction>();
        if (pods.Count == 1)
        {
            var pod = pods[0];
            actions.Add(new PluginAction("Pod logs", PluginActionIcon.Info, ct => LogsAsync(pod, null, dialogs, ct)));
            actions.Add(new PluginAction("Describe the pod", PluginActionIcon.Info, ct => DescribeAsync(pod, dialogs, ct)));
            if (DeploymentOf(pod) is { } deployment)
            {
                actions.Add(new PluginAction("Restart the deployment", PluginActionIcon.Restart, ct => RestartAsync(pod, deployment, dialogs, ct)));
            }
        }
        actions.Add(new PluginAction(pods.Count == 1 ? "Delete the pod..." : "Delete the pods...", PluginActionIcon.Delete, ct => DeletePodsAsync(pods, dialogs, ct)));
        return actions;
    }

    /// <summary>The deployment a pod belongs to (pod → its replica set → the deployment), from the last listing; null when none.</summary>
    internal string? DeploymentOf(VfsPath pod)
    {
        var where = LocationOf(pod);
        if (!_pods.TryGetValue(pod, out var known))
        {
            return null;
        }
        return known.OwnerKind switch
        {
            "ReplicaSet" when known.OwnerName is { } set => _deployments.TryGetValue($"{where.Context}/{where.Namespace}/{set}", out var deployment) ? deployment : null,
            "Deployment" => known.OwnerName,
            _ => null,
        };
    }

    internal async Task<string?> LogsAsync(VfsPath pod, string? container, IPluginDialogs dialogs, CancellationToken ct)
    {
        var where = LocationOf(pod);
        var several = container is null && _pods.TryGetValue(pod, out var known) && known.Containers.Count > 1;
        // one container: its own lines; several: every container's, each line with the container's name
        string[] which = container is not null ? ["-c", container] : several ? ["--all-containers=true", "--prefix=true"] : [];
        var output = await RunAsync(pod, Kubectl(where.Context, where.Namespace, ["logs", where.Pod!, .. which, $"--tail={LogLines}"]), null, ct).ConfigureAwait(false);
        if (output.Trim().Length == 0)
        {
            return "The pod has written no log lines.";
        }
        await dialogs.ShowTextAsync((container is null ? where.Pod : $"{where.Pod}-{container}") + ".log", output).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> DescribeAsync(VfsPath pod, IPluginDialogs dialogs, CancellationToken ct)
    {
        var where = LocationOf(pod);
        var output = await RunAsync(pod, Kubectl(where.Context, where.Namespace, "describe", "pod", where.Pod!), null, ct).ConfigureAwait(false);
        await dialogs.ShowTextAsync(where.Pod + ".txt", output).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> DeletePodsAsync(IReadOnlyList<VfsPath> pods, IPluginDialogs dialogs, CancellationToken ct)
    {
        var question = pods.Count == 1
            ? $"Delete the pod {pods[0].Name}? A pod of a deployment (or of another controller) is made again by it."
            : $"Delete {pods.Count} pods? Pods of a deployment (or of another controller) are made again by it.";
        if (!await dialogs.ConfirmAsync("Kubernetes", question).ConfigureAwait(false))
        {
            return null;
        }
        var where = LocationOf(pods[0]);
        // without waiting for the pods to end: the list shows them "Terminating"
        await RunAsync(pods[0], Kubectl(where.Context, where.Namespace, ["delete", "pod", .. pods.Select(p => p.Name), "--wait=false"]), null, ct).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> RestartAsync(VfsPath pod, string deployment, IPluginDialogs dialogs, CancellationToken ct)
    {
        if (!await dialogs.ConfirmAsync("Kubernetes", $"Restart the deployment {deployment}? Its pods are replaced one by one.").ConfigureAwait(false))
        {
            return null;
        }
        var where = LocationOf(pod);
        await RunAsync(pod, Kubectl(where.Context, where.Namespace, "rollout", "restart", "deployment/" + deployment), null, ct).ConfigureAwait(false);
        return null;
    }

    // ---------------------------------------------------------------- settings (Configuration > Plugins > Kubernetes)

    internal const string ProgramKey = "program";
    internal const string KubeconfigKey = "kubeconfig";
    internal const string LogLinesKey = "logLines";

    public string SettingsTitle => "Kubernetes";

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new(ProgramKey, PluginSettingKind.Program, "kubectl program")
        {
            Description = "Empty: FM_KUBECTL, then kubectl on the PATH and in the usual folders.",
            Placeholder = KubectlTool.Find() ?? "kubectl",
        },
        new(KubeconfigKey, PluginSettingKind.File, "kubeconfig file")
        {
            Description = "Empty: the one kubectl reads itself (the KUBECONFIG environment variable, else .kube/config in the home folder).",
        },
        new(LogLinesKey, PluginSettingKind.Number, "Log lines shown")
        {
            Description = "How many of a pod's last log lines Pod logs shows.",
            Default = DefaultLogLines.ToString(CultureInfo.InvariantCulture),
            Min = 10,
            Max = 100_000,
        },
    ];

    public string? Validate(IPluginSettingValues values)
    {
        if (values.Get(ProgramKey) is { } program && !File.Exists(program))
        {
            return string.Format(CultureInfo.CurrentCulture, "The program was not found: {0}", program);
        }
        return values.Get(KubeconfigKey) is { } file && !File.Exists(file) ? string.Format(CultureInfo.CurrentCulture, "The file was not found: {0}", file) : null;
    }

    public void Apply(IPluginSettingValues values)
    {
        KubectlTool.ConfiguredPath = values.Get(ProgramKey);
        Kubeconfig = values.Get(KubeconfigKey);
        var lines = (int)values.GetNumber(LogLinesKey);
        LogLines = lines > 0 ? lines : DefaultLogLines;
        _contexts.Clear();
    }

    /// <summary>".." from a context's root goes back to the context list, onto the context.</summary>
    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root)
    {
        if (root.Scheme != KubernetesScheme || root.Authority.Length == 0 || root.Path != "/")
        {
            return null;
        }
        return (Root, root.Authority);
    }
}
