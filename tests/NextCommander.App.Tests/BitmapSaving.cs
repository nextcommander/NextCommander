using Avalonia.Media.Imaging;

namespace NextCommander.App.Tests;

/// <summary>Screenshots of the tests as PNG files (Avalonia 12: the encoder options are given).</summary>
internal static class BitmapSaving
{
    public static void SavePng(this Bitmap bitmap, string path) => bitmap.Save(path, new PngBitmapEncoderOptions());
}
