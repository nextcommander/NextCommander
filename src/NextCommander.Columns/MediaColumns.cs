using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace NextCommander.Columns;

/// <summary>Image columns: dimensions, EXIF date taken, camera, lens settings (MetadataExtractor; JPEG, PNG, TIFF, HEIF, RAW...).</summary>
public sealed class ImageColumns : IColumnProvider
{
    private static readonly HashSet<string> s_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif",
        "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "pef", "srw",
    };

    public string Id => "image";

    public string DisplayName => "Image";

    public IReadOnlyList<ColumnField> Fields { get; } =
    [
        new("image.dimensions", "Dimensions (W×H)"),
        new("image.width", "Width", ColumnKind.Number),
        new("image.height", "Height", ColumnKind.Number),
        new("image.taken", "Date taken", ColumnKind.Date),
        new("image.camera", "Camera"),
        new("image.exposure", "Exposure"),
    ];

    public bool Supports(VfsEntry entry) => !entry.IsDirectory && entry.Path.IsLocal && s_extensions.Contains(entry.Extension);

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<MetadataExtractor.Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(entry.Path.Path);
        }
        catch (Exception ex) when (ex is ImageProcessingException or MetadataException or IOException or UnauthorizedAccessException)
        {
            return (IReadOnlyDictionary<string, object?>)values;
        }
        var (width, height) = Dimensions(directories);
        if (width > 0 && height > 0)
        {
            values["image.width"] = (long)width;
            values["image.height"] = (long)height;
            values["image.dimensions"] = $"{width}×{height}";
        }
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken))
        {
            values["image.taken"] = taken;
        }
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        if (ifd0 is not null)
        {
            var make = ifd0.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            var model = ifd0.GetDescription(ExifDirectoryBase.TagModel)?.Trim();
            values["image.camera"] = model is null ? make
                : make is not null && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? make + " " + model : model;
        }
        if (sub is not null)
        {
            var parts = new[] { ExifDirectoryBase.TagExposureTime, ExifDirectoryBase.TagFNumber, ExifDirectoryBase.TagIsoEquivalent, ExifDirectoryBase.TagFocalLength }
                .Select(t => sub.GetDescription(t)).Where(d => !string.IsNullOrEmpty(d)).ToList();
            if (parts.Count > 0)
            {
                values["image.exposure"] = string.Join("  ", parts);
            }
        }
        return values;
    }, cancellationToken);

    private static (int Width, int Height) Dimensions(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        // The image's own header first (JPEG / PNG / GIF / BMP / WebP), then EXIF.
        foreach (var directory in directories)
        {
            int? w = null, h = null;
            foreach (var tag in directory.Tags)
            {
                if (tag.Name is "Image Width" && directory.TryGetInt32(tag.Type, out var width))
                {
                    w = width;
                }
                else if (tag.Name is "Image Height" && directory.TryGetInt32(tag.Type, out var height))
                {
                    h = height;
                }
            }
            if (w > 0 && h > 0)
            {
                return (w.Value, h.Value);
            }
        }
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        return sub is not null && sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var ew) && sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var eh)
            ? (ew, eh)
            : (0, 0);
    }
}

/// <summary>Audio / video columns via TagLib#: duration, title, artist, album, year, bit rate, video size.</summary>
public sealed class MediaColumns : IColumnProvider
{
    private static readonly HashSet<string> s_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "ogg", "oga", "opus", "m4a", "m4b", "aac", "wav", "wma", "aiff", "aif", "ape", "wv",
        "mp4", "m4v", "mkv", "webm", "avi", "mov", "wmv", "mpg", "mpeg",
    };

    public string Id => "media";

    public string DisplayName => "Audio / video";

    public IReadOnlyList<ColumnField> Fields { get; } =
    [
        new("media.duration", "Duration"),
        new("media.title", "Title"),
        new("media.artist", "Artist"),
        new("media.album", "Album"),
        new("media.year", "Year", ColumnKind.Number),
        new("media.track", "Part no.", ColumnKind.Number),
        new("media.bitrate", "Bitrate (kbps)", ColumnKind.Number),
        new("media.video", "Video size"),
    ];

    public bool Supports(VfsEntry entry) => !entry.IsDirectory && entry.Path.IsLocal && s_extensions.Contains(entry.Extension);

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var file = TagLib.File.Create(entry.Path.Path, TagLib.ReadStyle.Average);
            var p = file.Properties;
            var t = file.Tag;
            if (p is not null && p.Duration > TimeSpan.Zero)
            {
                values["media.duration"] = p.Duration;
            }
            values["media.title"] = string.IsNullOrWhiteSpace(t?.Title) ? null : t.Title;
            values["media.artist"] = t?.JoinedPerformers is { Length: > 0 } a ? a : null;
            values["media.album"] = string.IsNullOrWhiteSpace(t?.Album) ? null : t.Album;
            values["media.year"] = t?.Year is > 0 and var year ? (long)year : null;
            values["media.track"] = t?.Track is > 0 and var track ? (long)track : null;
            values["media.bitrate"] = p?.AudioBitrate is > 0 and var bitrate ? (long)bitrate : null;
            values["media.video"] = p?.VideoWidth > 0 ? $"{p.VideoWidth}×{p.VideoHeight}" : null;
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException
            or NotImplementedException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            // not a readable media file
        }
        return (IReadOnlyDictionary<string, object?>)values;
    }, cancellationToken);
}
