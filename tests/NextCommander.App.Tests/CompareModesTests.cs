using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Editor;
using NextCommander.App.Views.Tools;
using NextCommander.Core.Compare;
using NextCommander.Core.Vfs;
using NextCommander.Tests.Shared;
using SkiaSharp;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Hex, documents and pictures in the compare window (the compare and merge plan, phase 7; 25 November 2026).</summary>
public sealed class CompareModesTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static void Shot(Avalonia.Controls.Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }

    private async Task<CompareWindow> OpenAsync(string leftName, byte[] left, string rightName, byte[] right)
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllBytes(_fx.L(leftName), left);
        File.WriteAllBytes(_fx.R(rightName), right);
        var l = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.L(leftName))))!;
        var r = (await vm.Left.GetEntryAsync(VfsPath.Local(_fx.R(rightName))))!;
        var window = _fx.Track(new CompareWindow(l, r, vm.Tools) { Width = 1100, Height = 700 });
        window.AskSaveOnClose = _ => Task.FromResult(SaveChoice.Discard);
        window.Show();
        await WaitUntilAsync(() => window.Comparing is { IsCompleted: true });
        await SettleAsync(2);
        return window;
    }

    [AvaloniaFact]
    public async Task Binary_files_are_shown_as_hex_dumps()
    {
        var left = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var right = (byte[])left.Clone();
        right[5] = 0xAA;
        right[40] = 0xBB;
        var window = await OpenAsync("a.bin", left, "b.bin", right);
        Assert.Equal(CompareMode.Hex, window.Mode);
        Assert.True(window.IsBinary);
        Assert.Equal(2, window.BlockCount); // rows 0 and 2
        Assert.Equal(4, window.Rows.Count);
        Assert.Contains("0x00000005   05 ≠ AA", window.BinaryDifferences);
        Assert.True(window.LeftEditor.IsReadOnly && window.RightEditor.IsReadOnly);
        window.MoveToBlock(+1);
        Assert.Equal("2 / 2", window.CounterText);
        Shot(window, "compare-hex");
    }

    [AvaloniaFact]
    public async Task Documents_are_compared_by_their_text_read_only()
    {
        var window = await OpenAsync("letter.doc", TestDocuments.Doc("Dear customer,\rThank you.\r"), "letter2.doc", TestDocuments.Doc("Dear customer,\rThank you very much.\r"));
        Assert.Equal(CompareMode.Document, window.Mode);
        Assert.False(window.IsBinary);
        Assert.Equal(["Dear customer,", "Thank you."], window.LeftLines);
        Assert.Equal(1, window.BlockCount);
        Assert.True(window.RightEditor.IsReadOnly);
        Assert.Contains("text of the documents", window.StatusText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Pictures_are_compared_pixel_by_pixel()
    {
        static byte[] Png(Action<SKCanvas> draw)
        {
            using var bitmap = new SKBitmap(40, 30);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                draw(canvas);
            }
            using var image = SKImage.FromBitmap(bitmap);
            return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }
        var left = Png(c => c.DrawRect(5, 5, 10, 10, new SKPaint { Color = SKColors.Blue }));
        var right = Png(c =>
        {
            c.DrawRect(5, 5, 10, 10, new SKPaint { Color = SKColors.Blue });
            c.DrawPoint(30, 20, new SKColor(250, 250, 250)); // nearly white
            c.DrawPoint(31, 20, SKColors.Red);
        });
        var window = await OpenAsync("a.png", left, "b.png", right);
        Assert.Equal(CompareMode.Picture, window.Mode);
        var pictures = window.Pictures!;
        Assert.Equal(2, pictures.Result.Different);
        Assert.Equal(40 * 30, pictures.Result.Compared);
        pictures.Threshold = 10; // the nearly white pixel no longer counts
        Assert.Equal(1, pictures.Result.Different);
        pictures.Show(2);
        Assert.Contains("1 of 1,200 pixels differ", window.StatusText.Replace('.', ','), StringComparison.Ordinal);
        Shot(window, "compare-pictures");
    }
}
