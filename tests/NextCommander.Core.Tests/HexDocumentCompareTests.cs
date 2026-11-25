using NextCommander.Core.Compare;
using NextCommander.Core.Documents;
using NextCommander.Tests.Shared;

namespace NextCommander.Core.Tests;

/// <summary>Binary files as hex rows and documents as text (the compare and merge plan, phase 7; 25 November 2026).</summary>
public sealed class HexDocumentCompareTests
{
    [Fact]
    public void Binary_files_become_hex_rows_at_the_same_offsets()
    {
        var left = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var right = left.Take(36).ToArray();
        right[20] = 0xFF;
        var rows = HexCompare.Rows(left, right);
        Assert.Equal([DiffKind.Equal, DiffKind.Changed, DiffKind.Changed], rows.Select(r => r.Kind));
        Assert.Equal("00000000  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  |................|", rows[0].Left);
        Assert.StartsWith("00000010  10 11 12 13 FF", rows[1].Right, StringComparison.Ordinal);
        Assert.Equal((1L, true), HexCompare.Count(left, right));

        // the longer one's last row alone
        rows = HexCompare.Rows(left, left.Take(32).ToArray());
        Assert.Equal(DiffKind.Removed, rows[^1].Kind);
        Assert.Null(rows[^1].Right);
        Assert.EndsWith("|!\"#$%&'|", HexCompare.Line(32, "!\"#$%&'"u8), StringComparison.Ordinal);
    }

    [Fact]
    public void Documents_are_compared_by_their_text()
    {
        Assert.Equal(["Title", "First paragraph", "a\tb", "c\td"],
            DocumentText.FromHtml("<h1>Title</h1><p>First&nbsp;paragraph</p><style>p{}</style><table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>"));
        Assert.True(DocumentText.Supports("docx"));
        Assert.True(DocumentText.Supports("ods"));
        Assert.False(DocumentText.Supports("txt"));

        var doc = DocumentText.Lines("doc", TestDocuments.Doc("Dear customer,\rThank you.\r"));
        Assert.Equal(["Dear customer,", "Thank you."], doc);
        if (PdfDocument.IsAvailable)
        {
            var pdf = DocumentText.Lines("pdf", TestDocuments.Pdf(2, text: i => $"Page text {i + 1}"));
            Assert.Equal(["── 1 ──", "Page text 1", "── 2 ──", "Page text 2"], pdf);
        }
    }
}
