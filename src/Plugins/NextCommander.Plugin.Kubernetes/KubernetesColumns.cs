using NextCommander.Core.Columns;
using NextCommander.Core.Localization;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Kubernetes;

/// <summary>
/// Kubernetes columns for the folders of the k8s file system: the status of a pod or container as kubectl shows it
/// ("Running", "CrashLoopBackOff" ...; "Current context" for the kubeconfig's current context), its ready containers,
/// restarts and node. The values come from the last listing of the folder (no command of their own).
/// </summary>
public sealed class KubernetesColumns : IColumnProvider
{
    public const string StatusField = "k8s.status";
    public const string ReadyField = "k8s.ready";
    public const string RestartsField = "k8s.restarts";
    public const string NodeField = "k8s.node";

    public string Id => "k8s";

    public string DisplayName => "Kubernetes";

    public IReadOnlyList<ColumnField> Fields { get; } =
    [
        new(StatusField, "Kubernetes status"),
        new(ReadyField, "Ready containers"),
        new(RestartsField, "Restarts", ColumnKind.Number),
        new(NodeField, "Node"),
    ];

    public bool Supports(VfsEntry entry) => entry.Path.Scheme == KubernetesFileSystemProvider.KubernetesScheme && entry.IsDirectory;

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>();
        if (KubernetesFileSystemProvider.Shown.TryGetValue(entry.Path, out var details))
        {
            values[StatusField] = details.Status.Length > 0 ? Localizer.T(details.Status) : null;
            values[ReadyField] = details.Ready;
            values[RestartsField] = details.Restarts;
            values[NodeField] = string.IsNullOrEmpty(details.Node) ? null : details.Node;
        }
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }
}
