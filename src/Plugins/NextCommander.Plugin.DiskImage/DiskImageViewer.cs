using System.Globalization;
using System.Net;
using System.Text;
using NextCommander.Core.Localization;
using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.DiskImage;

/// <summary>
/// The Lister (F3) on a virtual disk on this computer: its format, sizes, partition table, the disk files of a
/// differencing chain, and each volume with its type, file system, label, size and how full it is.
/// </summary>
public sealed class DiskImageViewer : IViewerPlugin
{
    public string Name => "Virtual disk";

    public bool CanView(string fileName, ReadOnlySpan<byte> header) => VirtualDiskImage.Formats.ContainsKey(Path.GetExtension(fileName));

    public Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        // a disk is read in place: its parent disks lie next to it, and it can be far too big to copy
        if (content is not FileStream { Name: { } path } || !File.Exists(path))
        {
            return Task.FromResult(new ViewerContent { Text = T("Virtual disks are shown only when they are on this computer.") });
        }
        return Task.Run(() => Overview(path, dark), cancellationToken);
    }

    internal static ViewerContent Overview(string path, bool dark)
    {
        using var image = DiskImageFileSystemProvider.OpenImage(path);
        lock (image.Gate)
        {
            var (text, muted, line, head, accent) = dark
                ? ("#e6e6e6", "#9a9a9a", "#3c3c3c", "#2a2a2a", "#6cb6ff")
                : ("#1f1f1f", "#6b6b6b", "#dcdcdc", "#f3f3f3", "#0b6bcb");
            var html = new StringBuilder();
            html.Append(CultureInfo.InvariantCulture, $"""
                <html><head><style>
                body {"{"} font-family: Segoe UI, Ubuntu, Helvetica, Arial, sans-serif; font-size: 10pt; color: {text}; margin: 12px; {"}"}
                h1 {"{"} font-size: 15pt; margin: 0 0 4px 0; {"}"}
                h2 {"{"} font-size: 12pt; margin: 18px 0 4px 0; color: {accent}; {"}"}
                .muted {"{"} color: {muted}; {"}"}
                table {"{"} border-collapse: collapse; margin: 4px 0 8px 0; {"}"}
                th, td {"{"} border: 1px solid {line}; padding: 2px 6px; text-align: left; vertical-align: top; {"}"}
                th {"{"} background-color: {head}; {"}"}
                td.num {"{"} text-align: right; {"}"}
                </style></head><body>
                """);
            var actual = new FileInfo(path).Length;
            html.Append("<h1>").Append(E(Path.GetFileName(path))).Append("</h1><div class=\"muted\">")
                .Append(E(image.Format)).Append(" · ").Append(E(T($"Disk size: {VirtualDiskImage.Size(image.Capacity)}"))).Append(" · ")
                .Append(E(T($"Size of the file: {VirtualDiskImage.Size(actual)}"))).Append(" · ")
                .Append(E(image.PartitionScheme ?? T("No partition table"))).Append("</div>");

            if (image.Chain.Count > 1)
            {
                html.Append("<h2>").Append(E(T("Differencing chain"))).Append("</h2><table>");
                foreach (var file in image.Chain)
                {
                    html.Append("<tr><td>").Append(E(file)).Append("</td></tr>");
                }
                html.Append("</table>");
            }

            html.Append("<h2>").Append(E(T("Volumes"))).Append("</h2>");
            if (image.Volumes.Count == 0)
            {
                html.Append("<p>").Append(E(T("The disk has no volumes."))).Append("</p>");
            }
            else
            {
                html.Append("<table><tr><th>#</th><th>").Append(E(T("Label"))).Append("</th><th>").Append(E(T("Type"))).Append("</th><th>")
                    .Append(E(T("File system"))).Append("</th><th>").Append(E(T("Size"))).Append("</th><th>").Append(E(T("Used"))).Append("</th></tr>");
                foreach (var volume in image.Volumes)
                {
                    string used;
                    try
                    {
                        used = volume.FileSystem is { } fs && fs.Size > 0
                            ? $"{VirtualDiskImage.Size(fs.UsedSpace)} ({fs.UsedSpace * 100 / fs.Size} %)"
                            : string.Empty;
                    }
                    catch (Exception ex) when (ex is IOException or NotSupportedException or NotImplementedException)
                    {
                        used = string.Empty;
                    }
                    html.Append("<tr><td class=\"num\">").Append(volume.Number).Append("</td><td>").Append(E(volume.Label ?? string.Empty))
                        .Append("</td><td>").Append(E(volume.Type)).Append("</td><td>")
                        .Append(E(volume.FileSystemName ?? T("Cannot be read")))
                        .Append("</td><td class=\"num\">").Append(E(VirtualDiskImage.Size(volume.Length)))
                        .Append("</td><td class=\"num\">").Append(E(used)).Append("</td></tr>");
                }
                html.Append("</table>");
            }
            html.Append("</body></html>");
            return new ViewerContent { Html = html.ToString(), Status = image.Format };
        }
    }

    private static string T(string text) => Localizer.T(text);

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
