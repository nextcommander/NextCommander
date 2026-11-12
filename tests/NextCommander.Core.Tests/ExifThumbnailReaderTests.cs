using System.Buffers.Binary;
using NextCommander.Core.Imaging;

namespace NextCommander.Core.Tests;

public class ExifThumbnailReaderTests
{
    private static readonly byte[] s_fakeJpeg = [0xFF, 0xD8, .. Enumerable.Repeat((byte)0x11, 300), 0xFF, 0xD9];

    /// <summary>Little-endian TIFF: IFD0 (orientation) → IFD1 (JPEG thumbnail at offset/length).</summary>
    private static byte[] BuildTiff(ushort orientation, byte[] thumbnail, bool bigEndian = false)
    {
        var ms = new MemoryStream();
        void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            }
            ms.Write(b);
        }
        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            }
            ms.Write(b);
        }
        ms.Write(bigEndian ? "MM"u8 : "II"u8);
        U16(42);
        U32(8);                       // IFD0 at 8
        U16(1);                       // 1 entry
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U32(26);                      // next IFD (IFD1) at 8 + 2 + 12 + 4 = 26
        U16(2);                       // IFD1: 2 entries
        U16(0x0201); U16(4); U32(1); U32(56); // thumbnail offset: 26 + 2 + 24 + 4 = 56
        U16(0x0202); U16(4); U32(1); U32((uint)thumbnail.Length);
        U32(0);
        ms.Write(thumbnail);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tiff_FindsThumbnailAndOrientation(bool bigEndian)
    {
        using var stream = new MemoryStream(BuildTiff(6, s_fakeJpeg, bigEndian));
        var info = ExifThumbnailReader.Read(stream);
        Assert.NotNull(info);
        Assert.Equal(6, info.Orientation);
        Assert.Equal(90, info.RotationDegrees);
        var preview = Assert.Single(info.Previews);
        var bytes = ExifThumbnailReader.ReadPreview(stream, info, preview);
        Assert.Equal(s_fakeJpeg, bytes);
    }

    [Fact]
    public void Jpeg_App1Exif_OffsetsAreRelativeToTiffHeader()
    {
        var tiff = BuildTiff(8, s_fakeJpeg);
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        ms.Write([0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00]); // APP0, skipped
        var app1Length = (ushort)(2 + 6 + tiff.Length);
        ms.Write([0xFF, 0xE1, (byte)(app1Length >> 8), (byte)app1Length]);
        ms.Write("Exif\0\0"u8);
        ms.Write(tiff);
        ms.Write([0xFF, 0xDA, 0x00, 0x02]);
        ms.Position = 0;

        var info = ExifThumbnailReader.Read(ms);
        Assert.NotNull(info);
        Assert.Equal(270, info.RotationDegrees);
        Assert.False(info.OffsetsRelativeToFile);
        Assert.Equal(s_fakeJpeg, ExifThumbnailReader.ReadPreview(ms, info, info.Previews[0]));
    }

    [Fact]
    public void NotAnImage_ReturnsNull()
    {
        using var ms = new MemoryStream("hello world"u8.ToArray());
        Assert.Null(ExifThumbnailReader.Read(ms));
    }

    [Fact]
    public void Choose_PicksSmallestBigEnough()
    {
        var list = new[] { new EmbeddedPreview(0, 5_000), new EmbeddedPreview(0, 80_000), new EmbeddedPreview(0, 2_000_000) };
        Assert.Equal(80_000, ExifThumbnailReader.Choose(list, 20_000)!.Value.Length);
        Assert.Equal(2_000_000, ExifThumbnailReader.Choose(list, 5_000_000)!.Value.Length);
        Assert.Equal(5_000, ExifThumbnailReader.Choose(list, 0)!.Value.Length);
    }
}
