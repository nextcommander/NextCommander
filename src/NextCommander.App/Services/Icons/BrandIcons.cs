using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using NextCommander.Core.Config;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// Icons of the connection types: the product marks of the cloud services (Google Drive, OneDrive, Dropbox, Box, Azure,
/// Amazon S3) in their own colors, drawn as vectors; the protocols (SFTP, FTP, WebDAV, SMB) get the application's glyphs.
/// </summary>
public static class BrandIcons
{
    private sealed record Part(string Data, string Color);

    private static readonly Dictionary<RemoteProtocol, (double Width, double Height, Part[] Parts)> s_marks = new()
    {
        // the Google Drive triangle (87.3 × 78)
        [RemoteProtocol.GoogleDrive] = (87.3, 78,
        [
            new("m6.6 66.85 3.85 6.65c.8 1.4 1.95 2.5 3.3 3.3l13.75-23.8h-27.5c0 1.55.4 3.1 1.2 4.5z", "#0066DA"),
            new("m43.65 25-13.75-23.8c-1.35.8-2.5 1.9-3.3 3.3l-25.4 44c-.8 1.4-1.2 2.95-1.2 4.5h27.5z", "#00AC47"),
            new("m73.55 76.8c1.35-.8 2.5-1.9 3.3-3.3l1.6-2.75 7.65-13.25c.8-1.4 1.2-2.95 1.2-4.5h-27.502l5.852 11.5z", "#EA4335"),
            new("m43.65 25 13.75-23.8c-1.35-.8-2.9-1.2-4.5-1.2h-18.5c-1.6 0-3.15.45-4.5 1.2z", "#00832D"),
            new("m59.8 53h-32.3l-13.75 23.8c1.35.8 2.9 1.2 4.5 1.2h50.8c1.6 0 3.15-.45 4.5-1.2z", "#2684FC"),
            new("m73.4 26.5-12.7-22c-.8-1.4-1.95-2.5-3.3-3.3l-13.75 23.8 16.15 28h27.45c0-1.55-.4-3.1-1.2-4.5z", "#FFBA00"),
        ]),
        // the Dropbox open box (24 × 24)
        [RemoteProtocol.Dropbox] = (24, 24,
        [
            new("M6 1.807 0 5.629l6 3.822 6.001-3.822zM18 1.807l-6 3.822 6 3.822 6-3.822zM0 13.274l6 3.822 6.001-3.822L6 9.452zM18 9.452l-6 3.822 6 3.822 6-3.822zM6 18.371l6.001 3.822 6-3.822-6-3.822z", "#0061FF"),
        ]),
        // Box: a white "b" on the Box blue (24 × 24)
        [RemoteProtocol.Box] = (24, 24,
        [
            new("M5 2h14a3 3 0 0 1 3 3v14a3 3 0 0 1-3 3H5a3 3 0 0 1-3-3V5a3 3 0 0 1 3-3z", "#0061D5"),
            new("M6.3 4.8h2.1v12.9H6.3z", "#FFFFFF"),
            new("F0 M12.9 9.1a4.3 4.3 0 1 1 0 8.6a4.3 4.3 0 1 1 0-8.6z M12.9 11.2a2.2 2.2 0 1 0 0 4.4a2.2 2.2 0 1 0 0-4.4z", "#FFFFFF"),
        ]),
        // the OneDrive clouds (24 × 24): a darker cloud behind, the lighter one in front
        [RemoteProtocol.OneDrive] = (24, 24,
        [
            new("M9.5 7.2a5.6 5.6 0 0 1 9.6 2.3 4 4 0 0 1 .9.1L13 16.5H3.6A3.6 3.6 0 0 1 4.9 9.7a4.6 4.6 0 0 1 4.6-2.5z", "#0364B8"),
            new("M13 10.6a4.9 4.9 0 0 1 7.3 1.9 3.6 3.6 0 0 1 3.7 3.6 3.5 3.5 0 0 1-3.5 3.6H8.6a3.4 3.4 0 0 1-.6-6.7 4.6 4.6 0 0 1 5-2.4z", "#28A8EA"),
        ]),
        // the Azure "A" (24 × 24)
        [RemoteProtocol.AzureBlob] = (24, 24,
        [
            new("M5.483 21.3H24L14.025 4.013l-3.038 8.347 5.836 6.938zM13.23 2.7 6.105 8.677 0 19.253h5.505v.014z", "#0089D6"),
        ]),
        // Azure Files: the same "A"
        [RemoteProtocol.AzureFiles] = (24, 24,
        [
            new("M5.483 21.3H24L14.025 4.013l-3.038 8.347 5.836 6.938zM13.23 2.7 6.105 8.677 0 19.253h5.505v.014z", "#0089D6"),
        ]),
        // an S3 bucket (24 × 24) in the Amazon S3 red
        [RemoteProtocol.S3] = (24, 24,
        [
            new("M3 5.5C3 3.6 7 2 12 2s9 1.6 9 3.5l-2.3 14.3C18.5 21.2 15.6 22 12 22s-6.5-.8-6.7-2.2z", "#C8382E"),
            new("M12 3.6c-4 0-7 1-7 1.9s3 1.9 7 1.9 7-1 7-1.9-3-1.9-7-1.9z", "#F58B7F"),
        ]),
    };

    /// <summary>The application glyph of a protocol without a product mark.</summary>
    private static (string Glyph, string Color) GlyphOf(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => ("MiKey", "#3BA54A"),
        RemoteProtocol.Smb => ("MiNetwork", "#2F7FE0"),
        RemoteProtocol.Link => ("MiMonitor", "#1FA59C"),
        RemoteProtocol.WebDav or RemoteProtocol.WebDavs => ("MiGlobe", "#1FA59C"),
        _ => ("MiTransfer", "#2F7FE0"),
    };

    /// <summary>Whether the protocol has a product mark (as opposed to a generic glyph).</summary>
    public static bool HasMark(RemoteProtocol protocol) => s_marks.ContainsKey(protocol);

    /// <summary>An icon of <paramref name="size"/> pixels for a connection type.</summary>
    public static Control Create(RemoteProtocol protocol, double size = 16)
    {
        if (s_marks.TryGetValue(protocol, out var mark))
        {
            var canvas = new Canvas { Width = mark.Width, Height = mark.Height };
            foreach (var part in mark.Parts)
            {
                canvas.Children.Add(new Path { Data = StreamGeometry.Parse(part.Data), Fill = new SolidColorBrush(Color.Parse(part.Color)) });
            }
            return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
        }
        var (glyph, color) = GlyphOf(protocol);
        var geometry = Avalonia.Application.Current?.TryGetResource(glyph, null, out var value) == true ? value as Geometry : null;
        var shape = new Path { Data = geometry, Fill = new SolidColorBrush(Color.Parse(color)), Stretch = Stretch.Uniform };
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = new Border { Width = 16, Height = 16, Child = shape } };
    }

    /// <summary>The protocol of a path scheme ("gdrive", "s3" ...), or null.</summary>
    public static RemoteProtocol? ProtocolOf(string scheme) => RemoteProtocols.FromScheme(scheme)?.Protocol;
}
