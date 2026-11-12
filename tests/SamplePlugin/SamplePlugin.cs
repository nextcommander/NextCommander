using System.Text;
using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

namespace SamplePlugin;

/// <summary>Column plugin: the name backwards.</summary>
public sealed class ReversedNameColumns : IColumnProvider
{
    public string Id => "sample";

    public string DisplayName => "Örnek eklenti";

    public IReadOnlyList<ColumnField> Fields { get; } = [new("sample.reversed", "Ters ad")];

    public bool Supports(VfsEntry entry) => true;

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["sample.reversed"] = new string(entry.Name.Reverse().ToArray()) });
}

/// <summary>File system plugin: hello://x/ with two read-only files.</summary>
public sealed class HelloFileSystem : IFileSystemProvider
{
    private static readonly string[] s_names = ["a.txt", "b.txt"];

    public string Scheme => "hello";

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    private static VfsEntry Entry(VfsPath dir, string name) => new()
    {
        Name = name,
        Path = dir.Combine(name),
        Size = Encoding.UTF8.GetByteCount("hello from " + name),
        LastWriteTime = new DateTime(2024, 1, 1),
    };

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VfsEntry>>(directory.Path == "/" ? s_names.Select(n => Entry(directory, n)).ToList() : throw new DirectoryNotFoundException());

    public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult(path.Path == "/" ? new VfsEntry { Name = "hello", Path = path, IsDirectory = true }
            : s_names.Contains(path.Name) ? Entry(path.Parent!, path.Name) : null);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("hello from " + path.Name)));

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Packer plugin: a ".lst" text archive with one "name<TAB>size<TAB>content as base64" line per file.</summary>
public sealed class ListingPacker : NextCommander.Core.Plugins.IPackerPlugin
{
    public string FormatName => "Liste";

    public string Extension => "lst";

    public bool SupportsPassword => false;

    public async Task PackAsync(IReadOnlyList<NextCommander.Core.Plugins.PackSource> sources, Stream output, string? password,
        IProgress<long> progress, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        foreach (var source in sources)
        {
            if (source.IsDirectory)
            {
                await writer.WriteLineAsync(source.Name + "/");
                continue;
            }
            await using var stream = await source.OpenAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            progress.Report(buffer.Length);
            await writer.WriteLineAsync($"{source.Name}\t{buffer.Length}\t{Convert.ToBase64String(buffer.ToArray())}");
        }
    }
}

/// <summary>Viewer plugin: files starting with "SAMPLE:" (or named *.sample) shown as an HTML card.</summary>
public sealed class SampleViewer : NextCommander.Core.Plugins.IViewerPlugin
{
    public string Name => "Örnek görüntüleyici";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        fileName.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) || header.StartsWith("SAMPLE:"u8);

    public async Task<NextCommander.Core.Plugins.ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        var text = await new StreamReader(content).ReadToEndAsync(cancellationToken);
        var color = dark ? "#4EC9B0" : "#0B7A6E";
        return new NextCommander.Core.Plugins.ViewerContent
        {
            Html = $"<html><body style='color:{color}'><h1>{System.Net.WebUtility.HtmlEncode(fileName)}</h1><p>{System.Net.WebUtility.HtmlEncode(text.ToUpperInvariant())}</p></body></html>",
            Status = $"{text.Length} karakter",
        };
    }
}
