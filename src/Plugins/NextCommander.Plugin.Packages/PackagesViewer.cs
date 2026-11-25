using System.Text;
using NextCommander.Core.Plugins;
using NextCommander.Plugin.Packages.Formats;

namespace NextCommander.Plugin.Packages;

/// <summary>
/// The Lister (F3) on a package: a .deb's or .rpm's name, version, description and dependencies; a cabinet's folders
/// and their compression; an .msi's product and cabinets; a container image archive's images with their configuration
/// (entrypoint, environment, layers with sizes). A package that is not a file on this computer is read from a
/// temporary copy.
/// </summary>
public sealed class PackagesViewer : IViewerPlugin
{
    public string Name => "Package";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) => Package.KindOf(fileName) switch
    {
        PackageKind.Deb => ArReader.IsAr(header),
        PackageKind.Rpm => RpmHeader.IsRpm(header),
        PackageKind.Cab => Cabinet.IsCabinet(header),
        PackageKind.Msi => Core.Documents.CompoundFile.IsCompoundFile(header),
        PackageKind.Image => LooksLikeImageArchive(header),
        _ => false,
    };

    /// <summary>
    /// A tar whose first entry is what docker save or an OCI layout starts with (blobs/, oci-layout, manifest.json, index.json,
    /// or a layer folder named by its 64-digit id; pax and GNU long name records before it are passed over): only these
    /// tars are taken from the Lister's own view.
    /// </summary>
    internal static bool LooksLikeImageArchive(ReadOnlySpan<byte> header)
    {
        var offset = 0;
        while (offset + 512 <= header.Length)
        {
            var block = header.Slice(offset, 512);
            if (!block.Slice(257, 5).SequenceEqual("ustar"u8))
            {
                return false;
            }
            if (block[156] is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K')
            {
                // an extended header: the entry follows its data
                var size = 0L;
                foreach (var b in block.Slice(124, 12))
                {
                    if (b is >= (byte)'0' and <= (byte)'7')
                    {
                        size = (size * 8) + (b - '0');
                    }
                }
                offset += 512 + (int)Math.Min((size + 511) / 512 * 512, header.Length);
                continue;
            }
            var nameField = block[..100];
            var end = nameField.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(end >= 0 ? nameField[..end] : nameField).TrimStart('.', '/');
            var first = name.Split('/')[0];
            return first is "blobs" or "oci-layout" or "manifest.json" or "index.json" or "repositories"
                || (first.Length == 64 && first.All(char.IsAsciiHexDigit));
        }
        return false;
    }

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        if (content is FileStream { Name: { } path } && File.Exists(path))
        {
            return await Task.Run(() => Overview(path, dark), cancellationToken).ConfigureAwait(false);
        }
        // not on this computer: a temporary copy under the package's own name (its kind is told by the extension)
        var folder = Path.Combine(Path.GetTempPath(), "nc-pkg-view-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, string.Concat(Path.GetFileName(fileName).Split(Path.GetInvalidFileNameChars())));
        try
        {
            await using (var file = File.Create(copy))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            return await Task.Run(() => Overview(copy, dark), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // left in the temporary folder
            }
        }
    }

    internal static ViewerContent Overview(string path, bool dark)
    {
        using var package = Package.Open(path);
        var overview = new Overview(dark);
        package.Describe(overview);
        return new ViewerContent { Html = overview.Html, Status = overview.Status };
    }
}
