using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Services.Icons;

public enum FileKind
{
    Parent,
    Folder,
    File,
    Archive,
    Image,
    Audio,
    Video,
    Code,
    Document,
    Pdf,
    Executable,
    Error,
    /// <summary>The header of a group of the file list (Show > Group by).</summary>
    GroupHeader,
}

/// <summary>Built-in vector icons by file type (used on every OS; replaced by shell icons on Windows).</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> s_byExtension = Build();

    private static readonly Dictionary<FileKind, IBrush> s_brushes = new()
    {
        [FileKind.Parent] = Brush("#D4A02A"),
        [FileKind.Folder] = Brush("#E0A526"),
        [FileKind.File] = Brush("#8A96A3"),
        [FileKind.Archive] = Brush("#8E6CC8"),
        [FileKind.Image] = Brush("#26A69A"),
        [FileKind.Audio] = Brush("#EF6C00"),
        [FileKind.Video] = Brush("#E53935"),
        [FileKind.Code] = Brush("#3F7FDB"),
        [FileKind.Document] = Brush("#4A78C2"),
        [FileKind.Pdf] = Brush("#D32F2F"),
        [FileKind.Executable] = Brush("#43A047"),
        [FileKind.Error] = Brush("#D32F2F"),
    };

    /// <summary>The built-in color of a file type icon (the icon pack editor starts from it).</summary>
    public static Color DefaultColor(FileKind kind) => s_brushes[kind] is ISolidColorBrush brush ? brush.Color : Colors.Gray;

    private static readonly Dictionary<FileKind, string> s_geometryKeys = new()
    {
        [FileKind.Parent] = "FmUpIcon",
        [FileKind.Folder] = "FmFolderIcon",
        [FileKind.File] = "FmFileIcon",
        [FileKind.Archive] = "FmArchiveIcon",
        [FileKind.Image] = "FmImageIcon",
        [FileKind.Audio] = "FmAudioIcon",
        [FileKind.Video] = "FmVideoIcon",
        [FileKind.Code] = "FmCodeIcon",
        [FileKind.Document] = "FmDocumentIcon",
        [FileKind.Pdf] = "FmDocumentIcon",
        [FileKind.Executable] = "FmExecutableIcon",
        [FileKind.Error] = "FmErrorIcon",
    };

    private static readonly Dictionary<FileKind, Geometry?> s_geometries = [];

    public static FileKind Classify(VfsEntry entry)
    {
        if (entry.IsDirectory)
        {
            return FileKind.Folder;
        }
        if (s_byExtension.TryGetValue(entry.Extension, out var kind))
        {
            return kind;
        }
        if (entry.UnixMode is { } m && (m & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
        {
            return FileKind.Executable;
        }
        return FileKind.File;
    }

    private static readonly Dictionary<FileKind, IBrush> s_packBrushes = [];

    /// <summary>
    /// The fill of a type icon as the icon pack has it: its own color ("File.Archive"), one color (monochrome) or a light
    /// tint under the outline (<see cref="StrokeFor"/>).
    /// </summary>
    public static IBrush BrushFor(FileKind kind)
    {
        var pack = IconPacks.Current;
        if (pack.Style == Core.Config.IconPackStyle.Monochrome)
        {
            return IconPacks.MonochromeBrush(pack); // read each time: follows the color theme
        }
        if (!s_packBrushes.TryGetValue(kind, out var brush))
        {
            brush = IconPacks.Color("File." + kind, s_brushes[kind], pack);
            s_packBrushes[kind] = brush;
        }
        return pack.Style == Core.Config.IconPackStyle.Outline ? IconPacks.Tint(brush) : brush;
    }

    /// <summary>The outline of a type icon (outline packs), or null.</summary>
    public static IBrush? StrokeFor(FileKind kind) =>
        IconPacks.Current.Style == Core.Config.IconPackStyle.Outline
            ? IconPacks.Color("File." + kind, s_brushes[kind])
            : null;

    public static Geometry? GeometryFor(FileKind kind)
    {
        if (!s_geometries.TryGetValue(kind, out var geometry))
        {
            geometry = IconPacks.Glyph(s_geometryKeys[kind]);
            s_geometries[kind] = geometry;
        }
        return geometry;
    }

    /// <summary>Forgets the glyphs and colors taken from the previous icon pack.</summary>
    public static void Reset()
    {
        s_geometries.Clear();
        s_packBrushes.Clear();
    }

    private static ImmutableSolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string extensions)
        {
            foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                map[ext] = kind;
            }
        }
        Add(FileKind.Archive, "zip 7z rar tar gz tgz bz2 tbz xz txz zst tzst cab iso lzh arj jar war apk nupkg deb rpm dmg");
        Add(FileKind.Image, "jpg jpeg png gif bmp webp svg heic heif tif tiff ico raw cr2 cr3 nef dng arw psd avif");
        Add(FileKind.Audio, "mp3 flac wav ogg oga m4a aac wma opus aiff");
        Add(FileKind.Video, "mp4 mkv avi mov wmv webm m4v mpg mpeg flv 3gp ts");
        Add(FileKind.Code, "cs csproj sln slnx props targets xaml axaml js mjs ts tsx jsx json xml yml yaml toml py rb go rs java kt c h cpp hpp cc swift php sql sh ps1 psm1 bat cmd css scss less html htm vue svelte md ini cfg conf gradle dockerfile");
        Add(FileKind.Document, "doc docx odt rtf xls xlsx ods csv ppt pptx odp txt log epub pages numbers key");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Executable, "exe msi com scr app appimage run bin dll so dylib");
        return map;
    }
}
