using System.Text;
using NextCommander.Core.Tools;

namespace NextCommander.Core.Tests;

public sealed class FilesMenuTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Uuencode_matches_the_classic_output()
    {
        var text = FileEncoding.Encode(Encoding.ASCII.GetBytes("Cat"), "cat.txt", TransferEncoding.Uue);
        Assert.Equal("begin 644 cat.txt\r\n#0V%T\r\n`\r\nend\r\n", text);
    }

    [Theory]
    [InlineData(TransferEncoding.Mime)]
    [InlineData(TransferEncoding.Uue)]
    [InlineData(TransferEncoding.Xxe)]
    public void Encode_decode_round_trip(TransferEncoding encoding)
    {
        foreach (var size in new[] { 0, 1, 2, 3, 44, 45, 46, 1000 })
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            var (name, decoded) = FileEncoding.Decode(FileEncoding.Encode(data, "rapor şubat.pdf", encoding));
            Assert.Equal(data, decoded);
            Assert.Equal("rapor şubat.pdf", name);
        }
        Assert.Throws<FormatException>(() => FileEncoding.Decode("bu bir kodlama değil!!"));
    }

    [Fact]
    public void Descriptions_read_write_quote_multiline_and_rename()
    {
        Descriptions.Write(_dir.Root, "a.txt", "ilk dosya");
        Descriptions.Write(_dir.Root, "boşluklu ad.txt", "iki\nsatır");
        var comments = Descriptions.Read(_dir.Root);
        Assert.Equal("ilk dosya", comments["A.TXT"]);
        Assert.Equal("iki\nsatır", comments["boşluklu ad.txt"]);
        var raw = File.ReadAllText(Descriptions.PathIn(_dir.Root));
        Assert.Contains("\"boşluklu ad.txt\" iki\\nsatır", raw, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(_dir.Root, "sub"));
        Descriptions.Rename(_dir.Root, "a.txt", Path.Combine(_dir.Root, "sub"), "b.txt");
        Assert.Equal("ilk dosya", Descriptions.Read(Path.Combine(_dir.Root, "sub"))["b.txt"]);
        Assert.False(Descriptions.Read(_dir.Root).ContainsKey("a.txt"));

        Descriptions.Write(_dir.Root, "boşluklu ad.txt", "");
        Assert.False(File.Exists(Descriptions.PathIn(_dir.Root))); // empty: file removed

        // A file written by another program: plain ASCII, multi-line marker 0x04 0xC2 at the end of the line.
        File.WriteAllBytes(Descriptions.PathIn(_dir.Root), [.. Encoding.ASCII.GetBytes("old.zip Old archive\r\n\"x y\" two\\nlines"), 0x04, 0xC2, 13, 10]);
        var legacy = Descriptions.Read(_dir.Root);
        Assert.Equal("Old archive", legacy["old.zip"]);
        Assert.Equal("two\nlines", legacy["x y"]);
    }
}
