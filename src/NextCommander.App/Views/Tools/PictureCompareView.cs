using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace NextCommander.App.Views.Tools;

/// <summary>Two pictures compared pixel by pixel: how many differ and a picture of where.</summary>
public sealed record PictureDifference(int LeftWidth, int LeftHeight, int RightWidth, int RightHeight, long Different, long Compared, byte[] DifferencePng)
{
    public bool SameSize => LeftWidth == RightWidth && LeftHeight == RightHeight;

    public double Percent => Compared == 0 ? 0 : 100.0 * Different / Compared;

    /// <summary>
    /// Compares two decoded pictures in their common area: a pixel differs when a channel differs by more than
    /// <paramref name="threshold"/> (0..255). The difference picture is the left one faded to gray with the differing
    /// pixels in red.
    /// </summary>
    public static PictureDifference Compute(SKBitmap left, SKBitmap right, int threshold)
    {
        using var a = left.Copy(SKColorType.Bgra8888);
        using var b = right.Copy(SKColorType.Bgra8888);
        var width = Math.Min(a.Width, b.Width);
        var height = Math.Min(a.Height, b.Height);
        using var difference = new SKBitmap(Math.Max(1, a.Width), Math.Max(1, a.Height), SKColorType.Bgra8888, SKAlphaType.Premul);
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        var pd = difference.GetPixelSpan();
        long different = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var ia = (y * a.RowBytes) + (x * 4);
                var id = (y * difference.RowBytes) + (x * 4);
                var outside = x >= width || y >= height;
                var differs = outside;
                if (!outside)
                {
                    var ib = (y * b.RowBytes) + (x * 4);
                    for (var c = 0; c < 4 && !differs; c++)
                    {
                        differs = Math.Abs(pa[ia + c] - pb[ib + c]) > threshold;
                    }
                    different += differs ? 1 : 0;
                }
                if (differs)
                {
                    pd[id] = 0x30; // B
                    pd[id + 1] = 0x30; // G
                    pd[id + 2] = 0xE0; // R
                    pd[id + 3] = 0xFF;
                }
                else
                {
                    // faded gray of the left picture
                    var luma = (byte)((pa[ia] * 0.11) + (pa[ia + 1] * 0.59) + (pa[ia + 2] * 0.3));
                    var faded = (byte)(200 + (luma * 55 / 255));
                    pd[id] = pd[id + 1] = pd[id + 2] = faded;
                    pd[id + 3] = 0xFF;
                }
            }
        }
        using var image = SKImage.FromBitmap(difference);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return new PictureDifference(left.Width, left.Height, right.Width, right.Height, different, (long)width * height, png.ToArray());
    }
}

/// <summary>
/// The picture compare of the compare window (the compare and merge plan, phase 7; 25 November 2026): the two pictures side by
/// side, overlaid (the right one over the left with a slider), or their difference (the differing pixels in red; how
/// much a color may change before it counts).
/// </summary>
public sealed class PictureCompareView : UserControl
{
    private readonly SKBitmap _left;
    private readonly SKBitmap _right;
    private readonly Bitmap _leftImage;
    private readonly Bitmap _rightImage;
    private readonly ToggleButton _sideBySide = new() { Content = "Side by side", IsChecked = true };
    private readonly ToggleButton _overlay = new() { Content = "Overlay" };
    private readonly ToggleButton _difference = new() { Content = "Difference" };
    private readonly Slider _blend = new() { Minimum = 0, Maximum = 100, Value = 50, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _threshold = new() { Minimum = 0, Maximum = 128, Value = 0, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _thresholdText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 32, Classes = { "noloc" } };
    private readonly StackPanel _blendRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly StackPanel _thresholdRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly Panel _stage = new();
    private Bitmap? _differenceImage;

    private PictureCompareView(SKBitmap left, SKBitmap right, Bitmap leftImage, Bitmap rightImage)
    {
        _left = left;
        _right = right;
        _leftImage = leftImage;
        _rightImage = rightImage;
        foreach (var (toggle, mode) in new[] { (_sideBySide, 0), (_overlay, 1), (_difference, 2) })
        {
            toggle.Click += (_, _) => Show(mode);
        }
        _blend.ValueChanged += (_, _) => Show(1);
        _threshold.ValueChanged += (_, _) =>
        {
            Recompute();
            Show(2);
        };
        _blendRow.Children.AddRange([new TextBlock { Text = "Left", VerticalAlignment = VerticalAlignment.Center }, _blend, new TextBlock { Text = "Right", VerticalAlignment = VerticalAlignment.Center }]);
        _thresholdRow.Children.AddRange([new TextBlock { Text = "Tolerance", VerticalAlignment = VerticalAlignment.Center }, _threshold, _thresholdText]);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 10) };
        bar.Children.AddRange([_sideBySide, _overlay, _difference, _blendRow, _thresholdRow]);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.AddRange([bar, _stage]);
        Content = root;
        Recompute();
        Show(0);
    }

    /// <summary>The comparison now (tests: the counts).</summary>
    public PictureDifference Result { get; private set; } = null!;

    /// <summary>The view: 0 side by side, 1 overlaid, 2 the difference.</summary>
    public int Mode { get; private set; }

    /// <summary>The view for two pictures; null when one of them is not a picture.</summary>
    public static PictureCompareView? TryCreate(byte[] left, byte[] right)
    {
        var a = SKBitmap.Decode(left);
        var b = SKBitmap.Decode(right);
        if (a is null || b is null)
        {
            a?.Dispose();
            b?.Dispose();
            return null;
        }
        return new PictureCompareView(a, b, new Bitmap(new MemoryStream(left)), new Bitmap(new MemoryStream(right)));
    }

    /// <summary>How much a channel may change before the pixel counts as different (0..128).</summary>
    public int Threshold
    {
        get => (int)_threshold.Value;
        set => _threshold.Value = value;
    }

    private void Recompute()
    {
        Result = PictureDifference.Compute(_left, _right, Threshold);
        _differenceImage = new Bitmap(new MemoryStream(Result.DifferencePng));
        _thresholdText.Text = Threshold.ToString(CultureInfo.CurrentCulture);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The comparison was made again (the window's counts follow).</summary>
    public event EventHandler? Changed;

    public void Show(int mode)
    {
        Mode = mode;
        _sideBySide.IsChecked = mode == 0;
        _overlay.IsChecked = mode == 1;
        _difference.IsChecked = mode == 2;
        _blendRow.IsVisible = mode == 1;
        _thresholdRow.IsVisible = mode == 2;
        _stage.Children.Clear();
        static Image Picture(Bitmap bitmap, double opacity = 1)
        {
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Opacity = opacity, Margin = new Thickness(8) };
            // enlarged small pictures keep their pixels sharp (a single differing pixel stays one)
            RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
            return image;
        }
        switch (mode)
        {
            case 1:
                var layers = new Panel();
                layers.Children.AddRange([Picture(_leftImage), Picture(_rightImage, _blend.Value / 100)]);
                _stage.Children.Add(layers);
                break;
            case 2:
                _stage.Children.Add(Picture(_differenceImage!));
                break;
            default:
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
                var right = Picture(_rightImage);
                Grid.SetColumn(right, 1);
                grid.Children.AddRange([Picture(_leftImage), right]);
                _stage.Children.Add(grid);
                break;
        }
    }
}
