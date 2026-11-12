using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NextCommander.Core.Matching;
using NextCommander.Core.Vfs;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>
/// Configuration > Thumbnails: where thumbnails of files that are not decodable images come from — the first lines of text
/// files, an image viewer that converts to PNG (IrfanView, XnView nconvert), and on Windows Explorer's thumbnail handlers.
/// </summary>
public sealed class ThumbnailSources
{
    private readonly WildcardMatcher? _text;
    private readonly WildcardMatcher? _viewer;
    private readonly WildcardMatcher? _shell;

    public ThumbnailSources(string? textMask = null, string? viewerProgram = null, string? viewerMask = null, string? shellMask = null)
    {
        _text = Parse(textMask);
        ViewerProgram = string.IsNullOrWhiteSpace(viewerProgram) ? null : Environment.ExpandEnvironmentVariables(viewerProgram.Trim().Trim('"'));
        _viewer = ViewerProgram is null ? null : Parse(viewerMask);
        _shell = OperatingSystem.IsWindows() ? Parse(shellMask) : null;
    }

    public string? ViewerProgram { get; }

    private static WildcardMatcher? Parse(string? mask) => string.IsNullOrWhiteSpace(mask) ? null : WildcardMatcher.Parse(mask);

    public bool IsText(VfsEntry entry) => _text?.MatchesFile(entry.Name) == true;

    public bool IsForViewer(VfsEntry entry) => entry.Path.IsLocal && _viewer?.MatchesFile(entry.Name) == true;

    public bool IsForShell(VfsEntry entry) => entry.Path.IsLocal && _shell?.MatchesFile(entry.Name) == true;

    public bool Matches(VfsEntry entry) => IsText(entry) || IsForViewer(entry) || IsForShell(entry);

    /// <summary>The first lines of a text file drawn on a page (on the UI thread).</summary>
    public static async Task<Bitmap?> TextAsync(Stream source, int size, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var read = await source.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var encoding = Core.Text.EncodingDetector.Detect(buffer.AsSpan(0, read), out var bom);
        var text = encoding.GetString(buffer, bom, read - bom);
        var lines = string.Join('\n', text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Take(40));
        return await Dispatcher.UIThread.InvokeAsync<Bitmap?>(() =>
        {
            var page = new Border
            {
                Width = size,
                Height = size,
                Background = Brushes.White,
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(size / 24.0),
                Child = new TextBlock
                {
                    Text = lines,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
                    FontSize = Math.Max(4, size / 22.0),
                    Foreground = Brushes.Black,
                    TextWrapping = TextWrapping.NoWrap,
                    ClipToBounds = true,
                },
            };
            page.Measure(new Size(size, size));
            page.Arrange(new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            bitmap.Render(page);
            return bitmap;
        }, DispatcherPriority.Background);
    }

    /// <summary>IrfanView (/convert) or XnView nconvert writes a PNG of the file; null when it did not.</summary>
    public async Task<Bitmap?> ViewerAsync(string file, int size, CancellationToken ct)
    {
        if (ViewerProgram is null || !File.Exists(ViewerProgram))
        {
            return null;
        }
        var output = Path.Combine(Path.GetTempPath(), "fm-thumb-" + Guid.NewGuid().ToString("N")[..12] + ".png");
        var info = new ProcessStartInfo(ViewerProgram) { UseShellExecute = false, CreateNoWindow = true };
        var sizeText = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Path.GetFileNameWithoutExtension(ViewerProgram).Contains("nconvert", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var arg in new[] { "-quiet", "-out", "png", "-o", output, "-ratio", "-resize", sizeText, sizeText, file })
            {
                info.ArgumentList.Add(arg);
            }
        }
        else
        {
            // IrfanView: i_view64.exe file /resize=(w,h) /aspectratio /resample /convert=out.png /silent
            foreach (var arg in new[] { file, $"/resize=({sizeText},{sizeText})", "/aspectratio", "/resample", "/convert=" + output, "/silent" })
            {
                info.ArgumentList.Add(arg);
            }
        }
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (!File.Exists(output))
            {
                return null;
            }
            await using var png = File.OpenRead(output);
            return Bitmap.DecodeToWidth(png, size, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException or InvalidOperationException)
        {
            Log.Debug(ex, "Thumbnail program failed for {File}", file);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left in the temp folder
            }
        }
    }

    // ---------------------------------------------------------------- Windows Explorer thumbnails

    /// <summary>The thumbnail Explorer shows for a file (its thumbnail handler), not its icon; null when there is none.</summary>
    public static unsafe Bitmap? Shell(string path, int size)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            // IShellItemImageFactory through its vtable (Avalonia turns the runtime's built-in COM interop off)
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var getImage = (delegate* unmanaged[Stdcall]<IntPtr, NativeSize, int, IntPtr*, int>)(*(IntPtr**)factory)[3];
                IntPtr bitmap;
                if (getImage(factory, new NativeSize { Width = size, Height = size }, SiigbfThumbnailOnly | SiigbfBiggerSizeOk, &bitmap) != 0 || bitmap == IntPtr.Zero)
                {
                    return null;
                }
                hbitmap = bitmap;
            }
            finally
            {
                Marshal.Release(factory);
            }
            return FromHBitmap(hbitmap);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotSupportedException)
        {
            Log.Debug(ex, "Shell thumbnail failed for {Path}", path);
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }
        }
    }

    /// <summary>A 32-bit HBITMAP as an Avalonia bitmap (opaque when the bitmap carries no alpha).</summary>
    private static unsafe WriteableBitmap? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }
        var width = info.Width;
        var height = Math.Abs(info.Height);
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            fixed (byte* p = pixels)
            {
                if (GetDIBits(dc, hbitmap, 0, (uint)height, (IntPtr)p, ref header, 0) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            DeleteDC(dc);
        }
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(pixels, y * width * 4, buffer.Address + (y * buffer.RowBytes), width * 4);
        }
        return bitmap;
    }

    private const int SiigbfBiggerSizeOk = 0x01;
    private const int SiigbfThumbnailOnly = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }


#pragma warning disable SYSLIB1054 // COM out parameter: classic marshalling
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IntPtr factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
#pragma warning restore SYSLIB1054

    // ---------------------------------------------------------------- disk cache

    /// <summary>The index of the disk cache: "cache file\tsource path" per line, for removing thumbnails of deleted files.</summary>
    public const string IndexFile = "index.txt";

    private static readonly object s_indexLock = new();

    public static void Remember(string cacheDirectory, string cacheFile, string sourcePath)
    {
        try
        {
            lock (s_indexLock)
            {
                File.AppendAllText(Path.Combine(cacheDirectory, IndexFile), Path.GetRelativePath(cacheDirectory, cacheFile) + "\t" + sourcePath + "\n", Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Thumbnail index write failed");
        }
    }

    /// <summary>Deletes the cached thumbnails whose files are gone (Configuration > Thumbnails); returns how many.</summary>
    public static int RemoveStale(string cacheDirectory)
    {
        var index = Path.Combine(cacheDirectory, IndexFile);
        if (!File.Exists(index))
        {
            return 0;
        }
        lock (s_indexLock)
        {
            var kept = new List<string>();
            var removed = 0;
            foreach (var line in File.ReadAllLines(index, Encoding.UTF8).Distinct(StringComparer.Ordinal))
            {
                var tab = line.IndexOf('\t', StringComparison.Ordinal);
                if (tab <= 0)
                {
                    continue;
                }
                var cached = Path.Combine(cacheDirectory, line[..tab]);
                if (!File.Exists(cached))
                {
                    continue;
                }
                if (File.Exists(line[(tab + 1)..]))
                {
                    kept.Add(line);
                    continue;
                }
                try
                {
                    File.Delete(cached);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    kept.Add(line);
                }
            }
            File.WriteAllLines(index, kept, Encoding.UTF8);
            return removed;
        }
    }
}
