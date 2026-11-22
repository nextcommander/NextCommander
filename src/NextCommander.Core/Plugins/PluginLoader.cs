using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Plugins;

/// <summary><c>plugin.json</c> in a plugin folder.</summary>
public sealed class PluginManifest
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Main assembly file name (relative to the folder).</summary>
    public string Assembly { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Version { get; set; }
}

/// <summary>A plugin folder and what was created from it.</summary>
public sealed class LoadedPlugin
{
    public required string Folder { get; init; }

    public PluginManifest? Manifest { get; init; }

    public List<IFileSystemProvider> FileSystems { get; } = [];

    public List<IColumnProvider> Columns { get; } = [];

    public List<IPackerPlugin> Packers { get; } = [];

    public List<IViewerPlugin> Viewers { get; } = [];

    /// <summary>The plugin's settings (<see cref="IConfigurablePlugin"/>), shown on the Plugins page of the configuration.</summary>
    public List<IConfigurablePlugin> Configurables { get; } = [];

    public string? Error { get; set; }

    public string Name => Manifest?.Name is { Length: > 0 } n ? n : Path.GetFileName(Folder);
}

/// <summary>
/// Loads plugins: every subfolder of a plugin root with a <c>plugin.json</c>. The plugin assembly gets its own load
/// context (its dependencies come from its folder) but shares <c>NextCommander.Core</c> with the application, so it
/// implements the same interfaces: <see cref="IFileSystemProvider"/> (+ <see cref="IContainerProvider"/>) for file
/// systems and archive reading, <see cref="IColumnProvider"/> for columns, <see cref="IPackerPlugin"/> for new archive
/// formats and <see cref="IViewerPlugin"/> for Lister views. Public classes with a parameterless constructor are instantiated.
/// </summary>
public static class PluginLoader
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The plugins in the roots as their plugin.json describes them, without loading anything (Configuration > Plugins).</summary>
    public static IReadOnlyList<(string Name, PluginManifest? Manifest, string Folder)> Discover(IEnumerable<string> roots)
    {
        var result = new List<(string, PluginManifest?, string)>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var folder in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var manifestFile = Path.Combine(folder, "plugin.json");
                if (!File.Exists(manifestFile))
                {
                    continue;
                }
                PluginManifest? manifest = null;
                try
                {
                    manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile), s_json);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // listed by its folder name
                }
                result.Add((manifest?.Name is { Length: > 0 } name ? name : Path.GetFileName(folder), manifest, folder));
            }
        }
        return result;
    }

    public static IReadOnlyList<LoadedPlugin> LoadAll(IEnumerable<string> roots, ISet<string>? disabled = null)
    {
        var result = new List<LoadedPlugin>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var folder in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var manifestFile = Path.Combine(folder, "plugin.json");
                if (!File.Exists(manifestFile))
                {
                    continue;
                }
                var plugin = Load(folder, manifestFile);
                if (disabled?.Contains(plugin.Name) == true)
                {
                    continue;
                }
                result.Add(plugin);
            }
        }
        return result;
    }

    private static LoadedPlugin Load(string folder, string manifestFile)
    {
        PluginManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestFile), s_json);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Assembly))
            {
                return new LoadedPlugin { Folder = folder, Manifest = manifest, Error = "plugin.json: \"Assembly\" is missing." };
            }
            var assemblyPath = Path.GetFullPath(Path.Combine(folder, manifest.Assembly));
            if (!assemblyPath.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(assemblyPath))
            {
                return new LoadedPlugin { Folder = folder, Manifest = manifest, Error = $"Assembly not found: {manifest.Assembly}" };
            }
            var context = new PluginLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var plugin = new LoadedPlugin { Folder = folder, Manifest = manifest };
            foreach (var type in ExportedTypes(assembly).Where(t => t is { IsClass: true, IsAbstract: false } && t.GetConstructor(Type.EmptyTypes) is not null))
            {
                var isFileSystem = typeof(IFileSystemProvider).IsAssignableFrom(type);
                var isColumns = typeof(IColumnProvider).IsAssignableFrom(type);
                var isPacker = typeof(IPackerPlugin).IsAssignableFrom(type);
                var isViewer = typeof(IViewerPlugin).IsAssignableFrom(type);
                var isConfigurable = typeof(IConfigurablePlugin).IsAssignableFrom(type);
                if (!isFileSystem && !isColumns && !isPacker && !isViewer && !isConfigurable)
                {
                    continue;
                }
                var instance = Activator.CreateInstance(type)!;
                if (isFileSystem)
                {
                    plugin.FileSystems.Add((IFileSystemProvider)instance);
                }
                if (isColumns)
                {
                    plugin.Columns.Add((IColumnProvider)instance);
                }
                if (isPacker)
                {
                    plugin.Packers.Add((IPackerPlugin)instance);
                }
                if (isViewer)
                {
                    plugin.Viewers.Add((IViewerPlugin)instance);
                }
                if (isConfigurable)
                {
                    // (one instance: a file system that is also the plugin's settings gets them itself)
                    plugin.Configurables.Add((IConfigurablePlugin)instance);
                }
            }
            if (plugin.FileSystems.Count + plugin.Columns.Count + plugin.Packers.Count + plugin.Viewers.Count == 0)
            {
                plugin.Error = "No file system, column, packer or viewer found in the plugin.";
            }
            return plugin;
        }
        catch (Exception ex) when (ex is JsonException or IOException or BadImageFormatException or FileLoadException or TypeLoadException
            or ReflectionTypeLoadException or TargetInvocationException or MissingMethodException or UnauthorizedAccessException)
        {
            return new LoadedPlugin { Folder = folder, Manifest = manifest, Error = ex.Message };
        }
    }

    private static IEnumerable<Type> ExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>().Where(t => t.IsPublic);
        }
    }

    /// <summary>Own dependencies from the plugin folder; the application's assemblies are shared.</summary>
    private sealed class PluginLoadContext(string mainAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssembly), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName))
                || assemblyName.Name is "NextCommander.Core" || assemblyName.Name?.StartsWith("System.", StringComparison.Ordinal) == true)
            {
                return null; // shared with the application
            }
            return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
