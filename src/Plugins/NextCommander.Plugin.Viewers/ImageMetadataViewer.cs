using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Xmp;
using NextCommander.Core.Plugins;
using MetaDirectory = MetadataExtractor.Directory;

namespace NextCommander.Plugin.Viewers;

/// <summary>
/// The metadata of photos and pictures (JPEG, PNG, TIFF, HEIC / HEIF, WebP, GIF, BMP and camera RAW files) through
/// MetadataExtractor: a summary (camera, lens, date taken, exposure, picture size ...), the GPS location with an
/// OpenStreetMap address, the IPTC / XMP description (title, caption, keywords, creator, copyright, rating, label), the
/// other EXIF tags, and every directory and tag found. A view next to the picture: the Lister still opens a picture it can
/// show as the picture, this is its plugin view (key 6).
/// </summary>
public sealed class ImageMetadataViewer : IViewerPlugin
{
    private static readonly string[] s_extensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".heic", ".heif", ".webp", ".gif", ".bmp",
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".orf", ".rw2", ".dng", ".raf", ".pef", ".srw",
    ];

    /// <summary>The tags of the summary: not repeated under the camera details.</summary>
    private static readonly HashSet<int> s_summaryTags =
    [
        ExifDirectoryBase.TagMake, ExifDirectoryBase.TagModel, ExifDirectoryBase.TagOrientation, ExifDirectoryBase.TagLensMake,
        ExifDirectoryBase.TagLensModel, ExifDirectoryBase.TagDateTimeOriginal, ExifDirectoryBase.TagTimeZoneOriginal,
        ExifDirectoryBase.TagExposureTime, ExifDirectoryBase.TagFNumber, ExifDirectoryBase.TagIsoEquivalent, ExifDirectoryBase.TagFocalLength,
        ExifDirectoryBase.Tag35MMFilmEquivFocalLength, ExifDirectoryBase.TagFlash, ExifDirectoryBase.TagColorSpace,
        ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight,
    ];

    public string Name => "Picture metadata";

    public bool IsDefaultView => false;

    public bool CanView(string fileName, ReadOnlySpan<byte> header) =>
        s_extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public async Task<ViewerContent> ViewAsync(Stream content, string fileName, bool dark, CancellationToken cancellationToken)
    {
        // MetadataExtractor jumps around in the file: a stream that cannot seek is read first
        var stream = content.CanSeek ? content : new MemoryStream(await ViewerHtml.ReadAllAsync(content, 1024L * 1024 * 1024, cancellationToken).ConfigureAwait(false));
        var (html, status) = await Task.Run(() => Describe(stream, fileName, dark), cancellationToken).ConfigureAwait(false);
        return new ViewerContent { Html = html, Status = status };
    }

    internal static (string Html, string Status) Describe(Stream stream, string fileName, bool dark)
    {
        IReadOnlyList<MetaDirectory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(stream, fileName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            var failed = new ViewerHtml(fileName, null, dark).Note("The metadata of this file cannot be read.").Paragraph(ex.Message, "muted");
            return (failed.ToString(), ViewerHtml.T("Not readable"));
        }
        return Describe(directories, fileName, dark);
    }

    internal static (string Html, string Status) Describe(IReadOnlyList<MetaDirectory> directories, string fileName, bool dark)
    {
        var culture = CultureInfo.CurrentCulture;
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault();
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault()?.GetXmpProperties() ?? new Dictionary<string, string>();

        var camera = Camera(Text(ifd0, ExifDirectoryBase.TagMake) ?? Xmp(xmp, "tiff:Make"), Text(ifd0, ExifDirectoryBase.TagModel) ?? Xmp(xmp, "tiff:Model"));
        var lens = Camera(Text(sub, ExifDirectoryBase.TagLensMake), Text(sub, ExifDirectoryBase.TagLensModel) ?? Xmp(xmp, "aux:Lens") ?? Xmp(xmp, "exifEX:LensModel"));
        var taken = Taken(sub, xmp) is { } time
            ? time.ToString("F", culture) + (Text(sub, ExifDirectoryBase.TagTimeZoneOriginal) is { } zone ? $" ({zone})" : string.Empty)
            : null;
        var (width, height) = Dimensions(directories);
        var megapixels = width * (double)height / 1_000_000;
        var size = width <= 0 || height <= 0 ? null
            : megapixels < 0.1 ? ViewerHtml.T($"{width} × {height} pixels")
            : ViewerHtml.T($"{width} × {height} pixels ({megapixels.ToString("0.#", culture)} megapixels)");
        var colorSpace = Text(sub, ExifDirectoryBase.TagColorSpace)
            ?? directories.FirstOrDefault(d => d.Name == "ICC Profile")?.Tags.FirstOrDefault(t => t.Name == "Profile Description")?.Description;

        var page = new ViewerHtml(fileName, string.Join(" · ", new[] { camera, taken }.Where(s => s is not null)), dark);
        var hasMetadata = directories.Any(d => d is ExifDirectoryBase or IptcDirectory or XmpDirectory);
        if (!hasMetadata)
        {
            page.Note("The picture holds no metadata (no EXIF, IPTC or XMP).");
        }

        page.Heading("Summary").Facts(
        [
            ("Camera", camera),
            ("Lens", lens),
            ("Date taken", taken),
            ("Exposure", Text(sub, ExifDirectoryBase.TagExposureTime)),
            ("F-number", Text(sub, ExifDirectoryBase.TagFNumber)),
            ("ISO", Text(sub, ExifDirectoryBase.TagIsoEquivalent)),
            ("Focal length", Text(sub, ExifDirectoryBase.TagFocalLength)),
            ("Focal length (35 mm film)", Text(sub, ExifDirectoryBase.Tag35MMFilmEquivFocalLength)),
            ("Flash", Text(sub, ExifDirectoryBase.TagFlash)),
            ("Picture size", size),
            ("Orientation", Text(ifd0, ExifDirectoryBase.TagOrientation)),
            ("Color space", colorSpace),
        ]);

        string? position = null;
        if (gps is not null && gps.TryGetGeoLocation(out var location) && !location.IsZero)
        {
            position = $"{Degrees(location.Latitude)}, {Degrees(location.Longitude)}";
            page.Heading("Location").Facts(
            [
                ("Latitude", $"{Degrees(Math.Abs(location.Latitude))}° {(location.Latitude < 0 ? "S" : "N")} · {Dms(location.Latitude, "N", "S")}"),
                ("Longitude", $"{Degrees(Math.Abs(location.Longitude))}° {(location.Longitude < 0 ? "W" : "E")} · {Dms(location.Longitude, "E", "W")}"),
                ("Position (decimal degrees)", position),
                ("Altitude", Altitude(gps)),
                ("Direction", Direction(gps)),
                ("GPS time", gps.TryGetGpsDate(out var utc) ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("F", culture) : null),
            ]);
            page.FactLink("Map", MapAddress(location.Latitude, location.Longitude));
        }

        var description = new (string Name, string? Value)[]
        {
            ("Picture title", Text(iptc, IptcDirectory.TagObjectName) ?? Text(iptc, IptcDirectory.TagHeadline) ?? Xmp(xmp, "dc:title")),
            ("Caption", Text(iptc, IptcDirectory.TagCaption) ?? Xmp(xmp, "dc:description") ?? Text(ifd0, ExifDirectoryBase.TagImageDescription)),
            ("Keywords", iptc?.GetStringArray(IptcDirectory.TagKeywords) is { Length: > 0 } keywords ? string.Join("; ", keywords) : XmpList(xmp, "dc:subject")),
            ("Creator", Text(iptc, IptcDirectory.TagByLine) ?? XmpList(xmp, "dc:creator") ?? Text(ifd0, ExifDirectoryBase.TagArtist)),
            ("Copyright", Text(iptc, IptcDirectory.TagCopyrightNotice) ?? Xmp(xmp, "dc:rights") ?? Text(ifd0, ExifDirectoryBase.TagCopyright)),
            ("Rating", Rating(Xmp(xmp, "xmp:Rating") ?? Text(ifd0, ExifDirectoryBase.TagRating))),
            ("Label", Xmp(xmp, "xmp:Label")),
        };
        if (description.Any(d => !string.IsNullOrWhiteSpace(d.Value)))
        {
            page.Heading("Description").Facts(description);
        }

        var details = new MetaDirectory?[] { ifd0, sub }.OfType<MetaDirectory>()
            .SelectMany(d => d.Tags)
            .Where(t => !s_summaryTags.Contains(t.Type))
            .Select(t => (t.Name, Cut(t.Description)))
            .ToList();
        if (details.Count > 0)
        {
            page.Heading("Camera details").Facts(details);
        }

        page.Heading("All metadata").Note("Every directory and tag the file holds.");
        var count = 0;
        foreach (var directory in directories)
        {
            page.Subheading(directory.Name);
            foreach (var error in directory.Errors)
            {
                page.Paragraph(error, "bad");
            }
            if (directory.Tags.Count > 0)
            {
                page.Table(["Tag", "ID", "Value"], directory.Tags.Select(t => (IReadOnlyList<string?>)[t.Name, "0x" + t.Type.ToString("X4", CultureInfo.InvariantCulture), Cut(t.Description)]));
                count += directory.Tags.Count;
            }
        }

        var tags = ViewerHtml.T($"Tags: {count}");
        var status = string.Join(" · ", new[] { camera, position, tags }.Where(s => s is not null));
        return (page.ToString(), status);
    }

    /// <summary>The OpenStreetMap address of a place, zoomed in with a marker.</summary>
    internal static string MapAddress(double latitude, double longitude) =>
        $"https://www.openstreetmap.org/?mlat={Degrees(latitude)}&mlon={Degrees(longitude)}#map=16/{Degrees(latitude)}/{Degrees(longitude)}";

    /// <summary>Decimal degrees as maps and addresses take them (a point, whatever the language).</summary>
    private static string Degrees(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Degrees, minutes and seconds, e.g. 41° 0' 36" N.</summary>
    internal static string Dms(double value, string positive, string negative)
    {
        var abs = Math.Abs(value);
        var degrees = (int)Math.Floor(abs);
        var minutesFull = (abs - degrees) * 60;
        var minutes = (int)Math.Floor(minutesFull);
        var seconds = Math.Round((minutesFull - minutes) * 60, 2);
        if (seconds >= 60)
        {
            seconds -= 60;
            minutes++;
        }
        if (minutes >= 60)
        {
            minutes -= 60;
            degrees++;
        }
        return $"{degrees}° {minutes}' {seconds.ToString("0.##", CultureInfo.CurrentCulture)}\" {(value < 0 ? negative : positive)}";
    }

    private static string? Altitude(GpsDirectory gps)
    {
        if (!gps.TryGetRational(GpsDirectory.TagAltitude, out var altitude) || altitude.Denominator == 0)
        {
            return null;
        }
        var metres = altitude.ToDouble();
        if (gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var reference) && reference == 1)
        {
            metres = -metres; // below sea level
        }
        return metres.ToString("0.#", CultureInfo.CurrentCulture) + " m";
    }

    private static string? Direction(GpsDirectory gps)
    {
        if (!gps.TryGetRational(GpsDirectory.TagImgDirection, out var direction) || direction.Denominator == 0)
        {
            return null;
        }
        var degrees = direction.ToDouble().ToString("0.#", CultureInfo.CurrentCulture) + "°";
        return gps.GetString(GpsDirectory.TagImgDirectionRef)?.Trim() switch
        {
            "T" => $"{degrees} ({ViewerHtml.T("true north")})",
            "M" => $"{degrees} ({ViewerHtml.T("magnetic north")})",
            _ => degrees,
        };
    }

    /// <summary>When the picture was taken: EXIF (original, else digitized), else XMP.</summary>
    private static DateTime? Taken(ExifSubIfdDirectory? sub, IDictionary<string, string> xmp)
    {
        if (sub is not null && (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken) || sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out taken)))
        {
            return taken;
        }
        foreach (var key in new[] { "exif:DateTimeOriginal", "photoshop:DateCreated", "xmp:CreateDate" })
        {
            if (Xmp(xmp, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return time.DateTime;
            }
        }
        return null;
    }

    /// <summary>"Make Model", without the make twice (many cameras repeat it in the model).</summary>
    private static string? Camera(string? make, string? model) =>
        model is null ? make : make is not null && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? make + " " + model : model;

    private static string? Rating(string? rating) =>
        rating is null ? null
        : !double.TryParse(rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? rating
        : value < 0 ? ViewerHtml.T("Rejected")
        : value.ToString("0.#", CultureInfo.CurrentCulture) + " / 5";

    private static string? Text(MetaDirectory? directory, int tag) =>
        directory?.GetDescription(tag)?.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A single XMP value: the property itself or its first item (a language alternative such as dc:title).</summary>
    private static string? Xmp(IDictionary<string, string> xmp, string name) =>
        (xmp.TryGetValue(name, out var value) || xmp.TryGetValue(name + "[1]", out value)) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    /// <summary>The items of an XMP list (dc:subject[1], dc:subject[2] ...), joined.</summary>
    private static string? XmpList(IDictionary<string, string> xmp, string name)
    {
        var items = new List<(int Index, string Value)>();
        foreach (var (key, value) in xmp)
        {
            if (key.Length > name.Length + 2 && key.StartsWith(name + "[", StringComparison.Ordinal) && key.EndsWith(']')
                && int.TryParse(key.AsSpan(name.Length + 1, key.Length - name.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && !string.IsNullOrWhiteSpace(value))
            {
                items.Add((index, value.Trim()));
            }
        }
        return items.Count == 0 ? null : string.Join("; ", items.OrderBy(i => i.Index).Select(i => i.Value));
    }

    /// <summary>The picture's size: its own header first (JPEG / PNG / GIF / BMP / WebP ...), then EXIF.</summary>
    private static (int Width, int Height) Dimensions(IReadOnlyList<MetaDirectory> directories)
    {
        foreach (var directory in directories)
        {
            if (directory is ExifDirectoryBase)
            {
                continue; // the sizes in IFD0 of a RAW file are often its small preview's
            }
            int? width = null, height = null;
            foreach (var tag in directory.Tags)
            {
                if (tag.Name is "Image Width" && directory.TryGetInt32(tag.Type, out var w))
                {
                    width = w;
                }
                else if (tag.Name is "Image Height" && directory.TryGetInt32(tag.Type, out var h))
                {
                    height = h;
                }
            }
            if (width > 0 && height > 0)
            {
                return (width.Value, height.Value);
            }
        }
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh))
        {
            return (ew, eh);
        }
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        return ifd0 is not null && ifd0.TryGetInt32(ExifDirectoryBase.TagImageWidth, out var iw) && ifd0.TryGetInt32(ExifDirectoryBase.TagImageHeight, out var ih)
            ? (iw, ih)
            : (0, 0);
    }

    /// <summary>Long values (binary tags, maker notes) cut short.</summary>
    private static string? Cut(string? text) => text is { Length: > 400 } ? text[..400] + "…" : text;
}
