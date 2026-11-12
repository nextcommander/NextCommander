using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace NextCommander.App.Services;

/// <summary>
/// Icons for the items of Explorer's context menu that have none (cut, copy, paste, delete, rename, properties ...):
/// the application's own menu icons (as the icon pack draws them), rendered into Windows bitmaps.
/// </summary>
public static class ShellMenuIcons
{
    /// <summary>Shell verb → the command whose icon it gets.</summary>
    private static readonly Dictionary<string, string> s_verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = "nc_Open",
        ["opennewwindow"] = "nc_OpenDirInNewTab",
        ["opennewtab"] = "nc_OpenDirInNewTab",
        ["edit"] = "nc_Edit",
        ["openas"] = "nc_OpenWith",
        ["openwith"] = "nc_OpenWith",
        ["print"] = "nc_PrintFile",
        ["cut"] = "nc_CutToClipboard",
        ["copy"] = "nc_CopyToClipboard",
        ["paste"] = "nc_PasteFromClipboard",
        ["delete"] = "nc_Delete",
        ["rename"] = "nc_RenameOnly",
        ["properties"] = "nc_Properties",
        ["copyaspath"] = "nc_CopyFullNamesToClip",
        ["find"] = "nc_SearchFor",
        ["opencmd"] = "nc_ExecuteTerminal",
        ["openinterminal"] = "nc_ExecuteTerminal",
        ["powershell"] = "nc_ExecuteTerminal",
    };

    /// <summary>The command whose icon a shell verb gets, or null.</summary>
    public static string? CommandOf(string verb) =>
        verb.StartsWith("nc_", StringComparison.OrdinalIgnoreCase) ? verb : s_verbs.GetValueOrDefault(verb); // the application's own items name their command

    /// <summary>
    /// A function for <c>IPlatformService.ShowSystemContextMenu</c>: the icon of a verb as an HBITMAP (32-bit premultiplied
    /// DIB section, <paramref name="scale"/> × 16 pixels), or zero. Runs on the UI thread (the icons are Avalonia drawings).
    /// </summary>
    public static Func<string, IntPtr>? ForScale(double scale) =>
        OperatingSystem.IsWindows() ? verb => CommandOf(verb) is { } command ? Render(command, scale) : IntPtr.Zero : null;

    /// <summary>The icon of a command drawn at <paramref name="scale"/> × 16 pixels without the menu's frame, or null.</summary>
    public static RenderTargetBitmap? Draw(string command, double scale)
    {
        if (MenuIcons.Create(command) is not { } icon)
        {
            return null;
        }
        // the menu draws its own frame and pressed state: the bare drawing without the menu border
        Control drawing = icon is Border { Child: { } child } border ? Detach(border, child) : icon;
        var size = Math.Max(16, (int)Math.Round(16 * scale));
        var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96 * scale, 96 * scale));
        drawing.Measure(new Size(16, 16));
        drawing.Arrange(new Rect(0, 0, 16, 16));
        target.Render(drawing);
        return target;
    }

    private static IntPtr Render(string command, double scale)
    {
        try
        {
            using var target = Draw(command, scale);
            if (target is null)
            {
                return IntPtr.Zero;
            }
            var size = target.PixelSize.Width;
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size, // top-down rows, as Avalonia's pixels
                biPlanes = 1,
                biBitCount = 32,
            };
            var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            target.CopyPixels(new PixelRect(0, 0, size, size), bits, size * size * 4, size * 4); // BGRA, premultiplied
            return bitmap;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ExternalException)
        {
            Serilog.Log.Debug(ex, "Shell menu icon of {Command} failed", command);
            return IntPtr.Zero;
        }
    }

    private static Control Detach(Border border, Control child)
    {
        border.Child = null;
        return child;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

#pragma warning disable SYSLIB1054
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
#pragma warning restore SYSLIB1054
}
