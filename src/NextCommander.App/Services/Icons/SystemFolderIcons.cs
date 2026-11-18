using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using NextCommander.Platform;
using Serilog;
using SkiaSharp;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// The icon the operating system gives a user folder (home, documents, downloads, desktop, pictures, music, videos), used
/// everywhere the application shows such a folder: tabs, file list, tree, drive bar and drive box. Windows: the shell
/// icon (SHGetFileInfo); Linux: the folder icon of the desktop's icon theme (freedesktop names such as folder-download);
/// macOS: NSWorkspace iconForFile. Loaded once per folder and cached; null when the system gives none (the built-in
/// glyph is drawn then).
/// </summary>
public static class SystemFolderIcons
{
    private const int Size = 32; // drawn at 16 DIP: stays sharp up to 200% scaling

    private static readonly ConcurrentDictionary<UserFolder, Bitmap?> s_cache = new();

    /// <summary>The system icon of a user folder; null when the system gives none.</summary>
    public static Bitmap? Icon(UserFolder folder) => s_cache.GetOrAdd(folder, Load);

    /// <summary>The system icon of a local folder when it is a user folder; null for any other folder.</summary>
    public static Bitmap? ForPath(string? path) => KnownFolders.KindOf(path) is { } folder ? Icon(folder) : null;

    /// <summary>The built-in glyph of a user folder (an icon pack can replace it), drawn when the system gives no icon.</summary>
    public static string GlyphKey(UserFolder folder) => folder switch
    {
        UserFolder.Home => "MiHome",
        UserFolder.Desktop => "MiDesktop",
        UserFolder.Documents => "MiDoc",
        UserFolder.Downloads => "MiArrowDown",
        _ => "MiFolderOpen",
    };

    /// <summary>Freedesktop icon names of a user folder, the most specific first.</summary>
    public static IReadOnlyList<string> FreedesktopNames(UserFolder folder) => folder switch
    {
        UserFolder.Home => ["user-home", "folder-home"],
        UserFolder.Desktop => ["user-desktop", "folder-desktop"],
        UserFolder.Documents => ["folder-documents"],
        UserFolder.Downloads => ["folder-download", "folder-downloads"],
        UserFolder.Pictures => ["folder-pictures", "folder-images"],
        UserFolder.Music => ["folder-music"],
        UserFolder.Videos => ["folder-videos", "folder-video"],
        _ => [],
    };

    private static Bitmap? Load(UserFolder folder)
    {
        var path = KnownFolders.PathOf(folder);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return null;
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsShellIconService.LoadPath(path);
            }
            if (OperatingSystem.IsMacOS())
            {
                return MacIcon(path);
            }
            if (OperatingSystem.IsLinux())
            {
                return FreedesktopIconTheme.Find(FreedesktopNames(folder), Size) is { } file ? LoadFile(file, Size) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException
                                       or InvalidOperationException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Debug(ex, "No system icon for {Folder}", folder);
        }
        return null;
    }

    /// <summary>A PNG or SVG icon file drawn at <paramref name="size"/> pixels.</summary>
    public static Bitmap? LoadFile(string file, int size)
    {
        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            using var svg = new Svg.Skia.SKSvg();
            if (svg.Load(file) is not { } picture)
            {
                return null;
            }
            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var scale = Math.Min(size / bounds.Width, size / bounds.Height);
            canvas.Translate((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = data.AsStream();
            return new Bitmap(stream);
        }
        using var file1 = File.OpenRead(file);
        return Bitmap.DecodeToWidth(file1, size);
    }

    // ------------------------------------------------------------ macOS

    /// <summary>NSWorkspace iconForFile: the Finder icon of the folder as PNG.</summary>
    internal static Bitmap? MacIcon(string path) => MacImage(() =>
    {
        var workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
        var nsPath = WithUtf8(path, p => Send(Class("NSString"), Sel("stringWithUTF8String:"), p));
        return Send(workspace, Sel("iconForFile:"), nsPath);
    });

    /// <summary>
    /// A named image of AppKit (<c>[NSImage imageNamed:]</c>, e.g. "NSTrashEmpty" / "NSTrashFull": the Dock's and
    /// Finder's Trash) as PNG; the Trash folder itself (~/.Trash) only has a plain folder icon.
    /// </summary>
    internal static Bitmap? MacNamedIcon(string name) => MacImage(() =>
    {
        var nsName = WithUtf8(name, p => Send(Class("NSString"), Sel("stringWithUTF8String:"), p));
        var image = Send(Class("NSImage"), Sel("imageNamed:"), nsName);
        // a shared image: draw a copy so setSize: does not change the system's one
        return image == IntPtr.Zero ? IntPtr.Zero : Send(Send(image, Sel("copy")), Sel("autorelease"));
    });

    private static Bitmap? MacImage(Func<IntPtr> imageOf)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var image = imageOf();
            if (image == IntPtr.Zero)
            {
                return null;
            }
            SendSize(image, Sel("setSize:"), new NSSize(Size, Size));
            var rect = new NSRect(0, 0, Size, Size);
            var cgImage = SendRect(image, Sel("CGImageForProposedRect:context:hints:"), ref rect, IntPtr.Zero, IntPtr.Zero);
            if (cgImage == IntPtr.Zero)
            {
                return null;
            }
            var rep = Send(Send(Class("NSBitmapImageRep"), Sel("alloc")), Sel("initWithCGImage:"), cgImage);
            if (rep == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var properties = Send(Class("NSDictionary"), Sel("dictionary"));
                var png = SendType(rep, Sel("representationUsingType:properties:"), 4 /* NSBitmapImageFileTypePNG */, properties);
                if (png == IntPtr.Zero)
                {
                    return null;
                }
                var length = (int)SendLength(png, Sel("length"));
                var bytes = Send(png, Sel("bytes"));
                if (length <= 0 || bytes == IntPtr.Zero)
                {
                    return null;
                }
                var buffer = new byte[length];
                Marshal.Copy(bytes, buffer, 0, length);
                using var stream = new MemoryStream(buffer);
                return new Bitmap(stream);
            }
            finally
            {
                Send(rep, Sel("release"));
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr Class(string name) => WithUtf8(name, objc_getClass);

    private static IntPtr Sel(string name) => WithUtf8(name, sel_registerName);

    /// <summary>Calls <paramref name="call"/> with a NUL-terminated UTF-8 copy of <paramref name="text"/>.</summary>
    private static IntPtr WithUtf8(string text, Func<IntPtr, IntPtr> call)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return call(utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NSSize(double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NSRect(double X, double Y, double Width, double Height);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

#pragma warning disable SYSLIB1054 // DllImport: a few calls of the Objective-C runtime
    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(IntPtr name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(IntPtr name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSize(IntPtr receiver, IntPtr selector, NSSize size);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendRect(IntPtr receiver, IntPtr selector, ref NSRect rect, IntPtr context, IntPtr hints);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendType(IntPtr receiver, IntPtr selector, nuint type, IntPtr properties);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nuint SendLength(IntPtr receiver, IntPtr selector);
#pragma warning restore SYSLIB1054
}

/// <summary>
/// Finds an icon of the desktop's icon theme (freedesktop Icon Theme Specification): the theme the desktop uses
/// (GNOME gsettings, KDE kdeglobals, GTK settings.ini), its parent themes and hicolor, in the XDG icon folders.
/// </summary>
public static class FreedesktopIconTheme
{
    private static readonly Lazy<IReadOnlyList<string>> s_themeDirs = new(ThemeDirectories);

    /// <summary>The icon file (PNG or SVG) of the first name found, of the size nearest to <paramref name="size"/>; null when none.</summary>
    public static string? Find(IReadOnlyList<string> names, int size)
    {
        foreach (var name in names)
        {
            foreach (var themeDir in s_themeDirs.Value)
            {
                if (Best(themeDir, name, size) is { } file)
                {
                    return file;
                }
            }
        }
        return null;
    }

    /// <summary>The best file of an icon in one theme folder: scalable SVG, else the PNG nearest in size (larger preferred).</summary>
    private static string? Best(string themeDir, string name, int size)
    {
        string? best = null;
        var bestScore = int.MaxValue;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(themeDir, name + ".*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".png" or ".svg") || file.Contains("symbolic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var score = extension == ".svg" ? 0 : SizeScore(file, size);
            if (score < bestScore)
            {
                best = file;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>How far a PNG's size (from a folder like 48x48 or 24x24@2x) is from the wanted size; a smaller one counts double.</summary>
    private static int SizeScore(string file, int size)
    {
        foreach (var part in file.Split('/'))
        {
            var x = part.IndexOf('x', StringComparison.Ordinal);
            if (x > 0 && int.TryParse(part[..x], out var pixels))
            {
                var at = part.IndexOf('@', StringComparison.Ordinal);
                if (at > 0 && int.TryParse(part[(at + 1)..].TrimEnd('x'), out var scale))
                {
                    pixels *= scale;
                }
                return pixels >= size ? 1 + pixels - size : 1 + 2 * (size - pixels);
            }
        }
        return 1000;
    }

    /// <summary>The folders of the desktop's theme and the themes it inherits, then hicolor.</summary>
    private static IReadOnlyList<string> ThemeDirectories()
    {
        var bases = IconBases();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var theme in new[] { CurrentTheme(), "Adwaita", "hicolor" })
        {
            if (!string.IsNullOrEmpty(theme))
            {
                queue.Enqueue(theme);
            }
        }
        while (queue.Count > 0)
        {
            var theme = queue.Dequeue();
            if (!seen.Add(theme))
            {
                continue;
            }
            foreach (var root in bases)
            {
                var dir = Path.Combine(root, theme);
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                result.Add(dir);
                var index = Path.Combine(dir, "index.theme");
                if (File.Exists(index) && IniValue(index, "Icon Theme", "Inherits") is { } inherits)
                {
                    foreach (var parent in inherits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        queue.Enqueue(parent);
                    }
                }
            }
        }
        return result;
    }

    private static List<string> IconBases()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>();
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");
        list.Add(Path.Combine(home, ".icons"));
        list.Add(Path.Combine(dataHome, "icons"));
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
        foreach (var dir in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            list.Add(Path.Combine(dir, "icons"));
        }
        return list;
    }

    /// <summary>The icon theme of the desktop; null when it cannot be found.</summary>
    public static string? CurrentTheme()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase)
            && IniValue(Path.Combine(home, ".config", "kdeglobals"), "Icons", "Theme") is { Length: > 0 } kde)
        {
            return kde;
        }
        if (Gsettings("org.gnome.desktop.interface", "icon-theme") is { Length: > 0 } gnome)
        {
            return gnome;
        }
        foreach (var gtk in new[] { "gtk-4.0", "gtk-3.0" })
        {
            if (IniValue(Path.Combine(home, ".config", gtk, "settings.ini"), "Settings", "gtk-icon-theme-name") is { Length: > 0 } name)
            {
                return name;
            }
        }
        return IniValue(Path.Combine(home, ".config", "kdeglobals"), "Icons", "Theme");
    }

    private static string? Gsettings(string schema, string key)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gsettings", ["get", schema, key])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000) || process.ExitCode != 0)
            {
                return null;
            }
            return output.Result.Trim().Trim('\'');
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>A value of an INI-style file (index.theme, kdeglobals, settings.ini); null when missing.</summary>
    public static string? IniValue(string file, string section, string key)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }
            var inSection = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSection = line == "[" + section + "]";
                    continue;
                }
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (inSection && equals > 0 && line[..equals].Trim() == key)
                {
                    return line[(equals + 1)..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
