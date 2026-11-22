using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NextCommander.App.Views.Lister;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// A page's picture fills its frame whole (the user's report, 22 November 2026: with a screen scale of 150 % the page was cut
/// at the right and the bottom, as a bitmap of 144 dpi was drawn from its top-left two thirds only).
/// </summary>
public sealed class PdfPagePictureTests
{
    [AvaloniaFact]
    public async Task The_picture_drawn_for_a_scaled_screen_fills_its_frame()
    {
        // drawn for a 200 × 100 frame on a screen at 150 %: 300 × 150 pixels in three bands (dark, grey, light)
        const int width = 300, height = 150;
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = (byte)(x < width / 3 ? 10 : x < 2 * width / 3 ? 128 : 250);
                var i = (y * width + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        }
        var image = new Image { Source = PdfView.ToBitmap(bgra, width, height), Stretch = Stretch.Fill };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new Border { Width = 200, Height = 100, Child = image, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top },
        };
        window.Show();
        await SettleAsync(3);
        var frame = window.CaptureRenderedFrame()!;
        var copy = new WriteableBitmap(frame.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var pixels = copy.Lock();
        frame.CopyPixels(new PixelRect(frame.PixelSize), pixels.Address, pixels.RowBytes * frame.PixelSize.Height, pixels.RowBytes);
        byte At(int x, int y) => Marshal.ReadByte(pixels.Address + y * pixels.RowBytes + x * 4);
        // the right edge of the frame shows the right band of the picture, the left edge the left band
        Assert.InRange(At(190, 90), 240, 255);
        Assert.InRange(At(10, 10), 0, 20);
        window.Close();
    }
}
