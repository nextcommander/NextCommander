using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NextCommander.App.ViewModels;
using Serilog;

namespace NextCommander.App.Services.Icons;

/// <summary>
/// Windows shell icons via SHGetFileInfo on a dedicated STA thread. Icons are cached per extension
/// (no disk access); executables, shortcuts and .ico files are loaded per file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellIconService : IFileIconService, IDisposable
{
    private const int IconSize = 32; // drawn at 16 DIP: stays sharp up to 200% scaling

    private static readonly HashSet<string> s_perFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "ico", "lnk", "url", "msc", "cpl", "scr", "appref-ms",
    };

    private readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<FileItemViewModel>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Key, string Path, bool UseAttributes, bool IsDirectory)> _queue = new();
    private readonly Thread _worker;

    public WindowsShellIconService()
    {
        _worker = new Thread(Work) { IsBackground = true, Name = "Shell icons" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public void Request(IReadOnlyList<FileItemViewModel> items)
    {
        foreach (var item in items)
        {
            if (item.Entry is not { } entry)
            {
                continue;
            }
            string key;
            string path;
            bool useAttributes;
            var local = entry.Path.IsLocal;
            if (entry.IsDirectory && local && IconSettings.SpecialFolderIcons && (entry.Attributes & (Core.Vfs.EntryAttributes.ReadOnly | Core.Vfs.EntryAttributes.System)) != 0
                && File.Exists(Path.Combine(entry.Path.Path, "desktop.ini")))
            {
                // Customized folder (desktop.ini, marked read-only/system by Windows): its own icon
                key = entry.Path.Path;
                path = entry.Path.Path;
                useAttributes = false;
            }
            else if (entry.IsDirectory)
            {
                key = "<dir>";
                path = "folder";
                useAttributes = true;
            }
            else if (local && (s_perFileExtensions.Contains(entry.Extension) || IconSettings.OverlayIcons) && IconSettings.PerFileAllowed(entry.Path.Path))
            {
                key = entry.Path.Path;
                path = entry.Path.Path;
                useAttributes = false;
            }
            else
            {
                key = "." + entry.Extension;
                path = "file." + entry.Extension;
                useAttributes = true;
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                item.SystemIcon = cached;
                continue;
            }
            var isNew = false;
            var list = _pending.GetOrAdd(key, _ =>
            {
                isNew = true;
                return [];
            });
            lock (list)
            {
                list.Add(item);
            }
            if (isNew)
            {
                _queue.Add((key, path, useAttributes, entry.IsDirectory));
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Work()
    {
        foreach (var (key, path, useAttributes, isDirectory) in _queue.GetConsumingEnumerable())
        {
            Bitmap? bitmap = null;
            try
            {
                bitmap = Load(path, useAttributes, isDirectory);
            }
            catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
            {
                Log.Debug(ex, "Icon load failed for {Path}", path);
            }
            _cache[key] = bitmap;
            if (_pending.TryRemove(key, out var items))
            {
                FileItemViewModel[] targets;
                lock (items)
                {
                    targets = [.. items];
                }
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var item in targets)
                    {
                        item.SystemIcon = bitmap;
                    }
                }, DispatcherPriority.Background);
            }
        }
    }

    /// <summary>Number of icons in a file (exe, dll, icl, ico); 0 when it has none.</summary>
    public static int IconCount(string file)
    {
        try
        {
            return (int)ExtractIconEx(file, -1, null, null, 0);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>The icon at <paramref name="index"/> of an icon file (Change button bar: icon list); null when there is none.</summary>
    public static Bitmap? LoadIndex(string file, int index)
    {
        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(file, index, large, null, 1) == 0 || large[0] == IntPtr.Zero)
            {
                return null;
            }
            return Render(large[0]);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            Log.Debug(ex, "Icon {Index} of {File} could not be loaded", index, file);
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero)
            {
                DestroyIcon(large[0]);
            }
        }
    }

    /// <summary>Icon of an existing path, e.g. a drive root (drive buttons); null when it has none.</summary>
    public static Bitmap? LoadPath(string path)
    {
        try
        {
            return Load(path, useAttributes: false, isDirectory: true);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException)
        {
            Log.Debug(ex, "Icon load failed for {Path}", path);
            return null;
        }
    }

    private static Bitmap? Load(string path, bool useAttributes, bool isDirectory)
    {
        var info = default(SHFILEINFO);
        var flags = SHGFI_ICON | SHGFI_LARGEICON | (useAttributes ? SHGFI_USEFILEATTRIBUTES : 0) | (!useAttributes && IconSettings.OverlayIcons ? SHGFI_ADDOVERLAYS : 0);
        var attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            return Render(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Draws the icon on black and on white; the difference gives exact (premultiplied) alpha.</summary>
    private static unsafe Bitmap? Render(IntPtr icon)
    {
        const int size = IconSize;
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return null;
        }
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var dib = CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(dc);
            return null;
        }
        var old = SelectObject(dc, dib);
        try
        {
            var count = size * size * 4;
            var onBlack = new byte[count];
            var onWhite = new byte[count];

            PatBlt(dc, 0, 0, size, size, BLACKNESS);
            DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, DI_NORMAL);
            GdiFlush();
            Marshal.Copy(bits, onBlack, 0, count);

            PatBlt(dc, 0, 0, size, size, WHITENESS);
            DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, DI_NORMAL);
            GdiFlush();
            Marshal.Copy(bits, onWhite, 0, count);

            var result = new byte[count];
            for (var i = 0; i < count; i += 4)
            {
                // white - black = 255 * (1 - alpha) for every channel; average the three for robustness.
                var diff = (onWhite[i] - onBlack[i] + onWhite[i + 1] - onBlack[i + 1] + onWhite[i + 2] - onBlack[i + 2]) / 3;
                var alpha = (byte)Math.Clamp(255 - diff, 0, 255);
                result[i] = Math.Min(onBlack[i], alpha);
                result[i + 1] = Math.Min(onBlack[i + 1], alpha);
                result[i + 2] = Math.Min(onBlack[i + 2], alpha);
                result[i + 3] = alpha;
            }
            fixed (byte* p = result)
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (IntPtr)p, new PixelSize(size, size), new Vector(96, 96), size * 4);
            }
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    // ------------------------------------------------------------ interop

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_ADDOVERLAYS = 0x20;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint DI_NORMAL = 0x3;
    private const uint BLACKNESS = 0x00000042;
    private const uint WHITENESS = 0x00FF0062;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

#pragma warning disable SYSLIB1054 // classic marshalling (struct with strings) is intended
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, uint step, IntPtr brush, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
#pragma warning restore SYSLIB1054
}
