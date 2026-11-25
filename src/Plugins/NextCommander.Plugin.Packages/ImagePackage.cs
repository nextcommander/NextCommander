using System.Globalization;
using System.Text.Json;
using NextCommander.Plugin.Packages.Formats;

namespace NextCommander.Plugin.Packages;

/// <summary>
/// A container image archive: the tar "docker save" writes (manifest.json with each image's config and layer tars) or
/// an OCI image layout (index.json, oci-layout, blobs/sha256/...). Each image is a folder ("nginx_1.25" for
/// nginx:1.25) with its config.json, a folder per layer ("layer 1 (3f4ca61aafcd)", gzip / zstd layers unpacked when
/// opened) and "merged": the file system the layers make together, later layers winning, whiteouts (".wh.name")
/// removing a lower entry and ".wh..wh..opq" a lower folder's whole content.
/// </summary>
internal sealed class ImagePackage : Package
{
    public const string Merged = "merged";

    internal sealed record Layer(string Id, Blob Raw)
    {
        public Lazy<List<ArchiveItem>>? Items { get; set; }
    }

    internal sealed record Image(string FolderName, IReadOnlyList<string> Tags, string? Platform, Blob? Config, IReadOnlyList<Layer> Layers);

    private readonly List<Image> _images = [];

    public ImagePackage(string file)
        : base(file)
    {
    }

    public override string Format => "Container image archive";

    public IReadOnlyList<Image> Images => _images;

    /// <summary>Whether the tar's root holds manifest.json, or index.json with oci-layout (the names, read without unpacking anything).</summary>
    public static bool IsImageArchive(IEnumerable<string> rootNames)
    {
        var names = rootNames.ToHashSet(StringComparer.Ordinal);
        return names.Contains("manifest.json") || (names.Contains("index.json") && names.Contains("oci-layout"));
    }

    protected override void Load()
    {
        var outer = Compression.Unpacked(Blob, Owner);
        if (!TarReader.IsTar(outer))
        {
            throw new InvalidDataException("Not a tar archive.");
        }
        var files = new Dictionary<string, Blob>(StringComparer.Ordinal);
        foreach (var item in TarReader.Read(outer))
        {
            if (item.Content is { } content)
            {
                files[string.Join('/', PackageNode.Split(item.Name))] = content;
            }
        }
        if (files.GetValueOrDefault("manifest.json") is { } manifest)
        {
            ReadDockerManifest(manifest, files);
        }
        else if (files.GetValueOrDefault("index.json") is { } index && files.ContainsKey("oci-layout"))
        {
            ReadOciIndex(index, files, null, null, 0);
        }
        else
        {
            throw new InvalidDataException("Not a container image archive (no manifest.json or index.json).");
        }
        foreach (var image in _images)
        {
            foreach (var layer in image.Layers)
            {
                var raw = layer.Raw;
                layer.Items ??= new Lazy<List<ArchiveItem>>(() => TarReader.Read(Compression.Unpacked(raw, Owner)));
            }
            Root.Put(new PackageNode(image.FolderName, isDirectory: true) { Fill = folder => FillImage(folder, image) });
        }
    }

    // ---------------------------------------------------------------- docker save

    private void ReadDockerManifest(Blob manifest, Dictionary<string, Blob> files)
    {
        using var json = JsonDocument.Parse(manifest.ToArray());
        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            var tags = entry.TryGetProperty("RepoTags", out var repoTags) && repoTags.ValueKind == JsonValueKind.Array
                ? repoTags.EnumerateArray().Select(t => t.GetString()).OfType<string>().ToList()
                : [];
            var configName = entry.TryGetProperty("Config", out var config) ? config.GetString() : null;
            var list = new List<Layer>();
            if (entry.TryGetProperty("Layers", out var layerNames))
            {
                foreach (var name in layerNames.EnumerateArray().Select(l => l.GetString()).OfType<string>())
                {
                    var key = string.Join('/', PackageNode.Split(name));
                    if (!layers.TryGetValue(key, out var layer))
                    {
                        layer = layers[key] = new Layer(LayerId(key), files.GetValueOrDefault(key) ?? throw new InvalidDataException("A layer is missing: " + name));
                    }
                    list.Add(layer);
                }
            }
            var configBlob = configName is null ? null : files.GetValueOrDefault(string.Join('/', PackageNode.Split(configName)));
            AddImage(tags, null, configBlob, configName, list);
        }
    }

    /// <summary>"blobs/sha256/3f4c..." or "3f4c.../layer.tar" → "3f4ca61aafcd".</summary>
    private static string LayerId(string path)
    {
        var parts = path.Split('/');
        var id = parts.Length >= 2 && parts[^1] is "layer.tar" ? parts[^2] : parts[^1];
        return id.Length > 12 && id.All(char.IsAsciiHexDigit) ? id[..12] : id;
    }

    // ---------------------------------------------------------------- OCI layout

    private static Blob? BlobOf(Dictionary<string, Blob> files, string? digest)
    {
        var colon = digest?.IndexOf(':', StringComparison.Ordinal) ?? -1;
        return colon <= 0 ? null : files.GetValueOrDefault($"blobs/{digest![..colon]}/{digest[(colon + 1)..]}");
    }

    private void ReadOciIndex(Blob index, Dictionary<string, Blob> files, string? name, string? platform, int depth)
    {
        if (depth > 4)
        {
            return;
        }
        using var json = JsonDocument.Parse(index.ToArray());
        if (!json.RootElement.TryGetProperty("manifests", out var manifests))
        {
            return;
        }
        foreach (var descriptor in manifests.EnumerateArray())
        {
            var digest = descriptor.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var refName = name;
            if (descriptor.TryGetProperty("annotations", out var annotations))
            {
                foreach (var key in new[] { "io.containerd.image.name", "org.opencontainers.image.ref.name" })
                {
                    if (refName is null && annotations.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } text)
                    {
                        refName = key == "org.opencontainers.image.ref.name" && !text.Contains(':', StringComparison.Ordinal) ? "image:" + text : text;
                    }
                }
            }
            var thisPlatform = platform;
            if (descriptor.TryGetProperty("platform", out var p))
            {
                var os = p.TryGetProperty("os", out var o) ? o.GetString() : null;
                var arch = p.TryGetProperty("architecture", out var a) ? a.GetString() : null;
                if (os == "unknown" || arch == "unknown")
                {
                    continue; // build attestations of a multi-platform image
                }
                thisPlatform = string.Join('/', new[] { os, arch }.OfType<string>());
            }
            if (BlobOf(files, digest) is not { } blob)
            {
                continue; // a platform not saved in the archive
            }
            using var manifest = JsonDocument.Parse(blob.ToArray());
            if (manifest.RootElement.TryGetProperty("manifests", out _))
            {
                ReadOciIndex(blob, files, refName, thisPlatform, depth + 1);
                continue;
            }
            var configDigest = manifest.RootElement.TryGetProperty("config", out var config) && config.TryGetProperty("digest", out var cd) ? cd.GetString() : null;
            var layers = new List<Layer>();
            if (manifest.RootElement.TryGetProperty("layers", out var layerList))
            {
                foreach (var layer in layerList.EnumerateArray())
                {
                    var layerDigest = (layer.TryGetProperty("digest", out var ld) ? ld.GetString() : null) ?? string.Empty;
                    var layerBlob = BlobOf(files, layerDigest) ?? throw new InvalidDataException("A layer is missing: " + layerDigest);
                    layers.Add(new Layer(LayerId(layerDigest[(layerDigest.IndexOf(':', StringComparison.Ordinal) + 1)..]), layerBlob));
                }
            }
            AddImage(refName is null ? [] : [refName], thisPlatform, BlobOf(files, configDigest), configDigest, layers);
        }
    }

    private void AddImage(List<string> tags, string? platform, Blob? config, string? configName, List<Layer> layers)
    {
        var baseName = tags.Count > 0 ? Sanitize(tags[0]) : configName is null ? "image" : LayerId(Path.GetFileNameWithoutExtension(configName.Replace(':', '/')));
        if (platform is not null)
        {
            baseName += " " + Sanitize(platform);
        }
        var folderName = baseName;
        for (var i = 2; _images.Exists(m => m.FolderName == folderName); i++)
        {
            folderName = $"{baseName} ({i})";
        }
        _images.Add(new Image(folderName, tags, platform, config, layers));
    }

    /// <summary>"registry:5000/team/app:1.0" → "registry_5000_team_app_1.0" (a name every file system takes).</summary>
    private static string Sanitize(string name) => string.Concat(name.Select(c => c is '/' or ':' or '\\' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c));

    // ---------------------------------------------------------------- the folders of an image

    public static string LayerFolder(int number, Layer layer) => $"layer {number} ({layer.Id})";

    private static void FillImage(PackageNode folder, Image image)
    {
        if (image.Config is { } config)
        {
            folder.Put(new PackageNode("config.json", isDirectory: false) { Size = config.Length, Content = config });
        }
        folder.Put(new PackageNode(Merged, isDirectory: true) { Fill = merged => Merge(merged, image.Layers) });
        for (var i = 0; i < image.Layers.Count; i++)
        {
            var layer = image.Layers[i];
            folder.Put(new PackageNode(LayerFolder(i + 1, layer), isDirectory: true) { Fill = node => ArchiveItem.Tree(layer.Items!.Value, node) });
        }
    }

    /// <summary>The layers one over the other: whiteouts of a layer remove from the layers below, then its entries go in.</summary>
    internal static void Merge(PackageNode root, IEnumerable<Layer> layers)
    {
        foreach (var layer in layers)
        {
            var items = layer.Items!.Value;
            foreach (var item in items)
            {
                var parts = PackageNode.Split(item.Name);
                if (parts.Length == 0 || !parts[^1].StartsWith(".wh.", StringComparison.Ordinal))
                {
                    continue;
                }
                var parent = root.Find(string.Join('/', parts[..^1]));
                if (parent is not { IsDirectory: true })
                {
                    continue;
                }
                if (parts[^1] == ".wh..wh..opq")
                {
                    parent.Clear();
                }
                else
                {
                    parent.Remove(parts[^1][4..]);
                }
            }
            foreach (var item in items)
            {
                var parts = PackageNode.Split(item.Name);
                if (parts.Length > 0 && !parts[^1].StartsWith(".wh.", StringComparison.Ordinal))
                {
                    root.Add(item.Name, item.ToNode());
                }
            }
        }
    }

    // ---------------------------------------------------------------- the Lister

    public override void Describe(Overview overview)
    {
        overview.Title(Path.GetFileName(File), Overview.T(Format), Overview.T($"{_images.Count} images"));
        foreach (var image in _images)
        {
            overview.Section(image.Tags.Count > 0 ? string.Join(", ", image.Tags) : image.FolderName);
            JsonDocument? config = null;
            try
            {
                if (image.Config is { } blob)
                {
                    config = JsonDocument.Parse(blob.ToArray());
                }
            }
            catch (JsonException)
            {
                // shown without its configuration
            }
            using (config)
            {
                Describe(overview, image, config?.RootElement);
            }
        }
        overview.Status = Overview.T(Format);
    }

    private static void Describe(Overview overview, Image image, JsonElement? config)
    {
        static string? Text(JsonElement? element, string name) =>
            element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Array => string.Join(' ', value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? Quote(v.GetString()!) : v.ToString())),
                    JsonValueKind.Object => string.Join(", ", value.EnumerateObject().Select(p => p.Name)),
                    JsonValueKind.Null => null,
                    _ => value.ToString(),
                }
                : null;
        var settings = config is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("config", out var c) ? c : (JsonElement?)null;
        var platform = image.Platform ?? string.Join('/', new[] { Text(config, "os"), Text(config, "architecture"), Text(config, "variant") }.OfType<string>());
        overview.Fields([
            ("Tags", string.Join(", ", image.Tags)), ("Platform", platform), ("Created", Text(config, "created")),
            ("Entrypoint", Text(settings, "Entrypoint")), ("Command", Text(settings, "Cmd")), ("Working folder", Text(settings, "WorkingDir")),
            ("User", Text(settings, "User")), ("Exposed ports", Text(settings, "ExposedPorts")),
        ]);
        if (settings is { } s && s.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array && env.GetArrayLength() > 0)
        {
            overview.Paragraph(Overview.T("Environment"));
            overview.List(env.EnumerateArray().Select(v => v.GetString()).OfType<string>());
        }
        if (settings is { } l && l.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object && labels.EnumerateObject().Any())
        {
            overview.Paragraph(Overview.T("Labels"));
            overview.Table(["Name", "Value"], labels.EnumerateObject().Select(p => (IReadOnlyList<string?>)[p.Name, p.Value.ToString()]));
        }
        // the history entries that made a layer, in order
        var history = config is { ValueKind: JsonValueKind.Object } h && h.TryGetProperty("history", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => !(e.TryGetProperty("empty_layer", out var empty) && empty.ValueKind == JsonValueKind.True))
                .Select(e => Text(e, "created_by")).ToList()
            : [];
        overview.Paragraph(Overview.T("Layers"));
        overview.Table(["#", "Layer", "Size", "Compression", "Created by"], image.Layers.Select((layer, i) => (IReadOnlyList<string?>)[
            (i + 1).ToString(CultureInfo.InvariantCulture), layer.Id, Overview.Size(layer.Raw.Length),
            Compression.NameOf(layer.Raw.Head(6)) ?? Overview.T("None"), i < history.Count ? history[i] : null]), 0, 2);
    }

    private static string Quote(string text) => text.Contains(' ', StringComparison.Ordinal) ? "\"" + text + "\"" : text;
}
