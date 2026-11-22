using System.Globalization;
using System.Text;
using NextCommander.Core.Documents;

namespace NextCommander.Core.Tests;

/// <summary>
/// A page is drawn whole whatever its boxes say (the user's report, 22 November 2026: a page was cut at the right and the
/// bottom in the Lister): a black box in each corner of what is shown must be in the picture.
/// </summary>
[Collection("Documents")]
public sealed class PdfPageBoxTests
{
    /// <summary>A one-page PDF with the given page dictionary entries; black boxes 20 points in from the corners of <paramref name="shown"/>.</summary>
    private static byte[] Pdf(string pageEntries, (double X0, double Y0, double X1, double Y1) shown)
    {
        var (x0, y0, x1, y1) = shown;
        string Box(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"{x} {y} 10 10 re f ");
        var content = "0 0 0 rg " + Box(x0 + 5, y0 + 5) + Box(x1 - 15, y0 + 5) + Box(x0 + 5, y1 - 15) + Box(x1 - 15, y1 - 15);
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R {pageEntries} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.6\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    public static TheoryData<string, string, double, double, double, double> Pages => new()
    {
        { "plain A4", "/MediaBox [0 0 595 842]", 0, 0, 595, 842 },
        { "media box not at the origin", "/MediaBox [100 200 695 1042]", 100, 200, 695, 1042 },
        { "crop box inside the media box", "/MediaBox [0 0 700 900] /CropBox [50 30 645 872]", 50, 30, 645, 872 },
        { "turned 90 degrees", "/MediaBox [0 0 595 842] /Rotate 90", 0, 0, 595, 842 },
        { "user unit 2", "/MediaBox [0 0 298 421] /UserUnit 2", 0, 0, 298, 421 },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void The_page_is_drawn_whole(string name, string entries, double x0, double y0, double x1, double y1)
    {
        using var document = PdfDocument.Open(Pdf(entries, (x0, y0, x1, y1)));
        var (width, height) = document.PageSize(0);
        var pixelWidth = 400;
        var pixelHeight = (int)Math.Round(pixelWidth * height / width);
        var bgra = document.Render(0, pixelWidth, pixelHeight);
        bool Dark(int x, int y) => bgra[((y * pixelWidth) + x) * 4] < 128;
        // the corners of the picture: each must hold its box (a few pixels in)
        var inset = (int)Math.Round(12 * pixelWidth / width);
        Assert.True(Dark(inset, inset), $"{name}: top left ({width} x {height})");
        Assert.True(Dark(pixelWidth - 1 - inset, inset), $"{name}: top right ({width} x {height})");
        Assert.True(Dark(inset, pixelHeight - 1 - inset), $"{name}: bottom left ({width} x {height})");
        Assert.True(Dark(pixelWidth - 1 - inset, pixelHeight - 1 - inset), $"{name}: bottom right ({width} x {height})");
    }
}
