using System.Globalization;
using System.IO.Compression;
using System.Text;
using NextCommander.Core.Documents;
using NextCommander.Tests.Shared;

namespace NextCommander.Core.Tests;

/// <summary>The document previews: PDF pages drawn by PDFium, Office / OpenDocument files as HTML, LibreOffice's conversion.</summary>
[Collection("Documents")]
public sealed class DocumentPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nc-docs-" + Guid.NewGuid().ToString("N")[..8]);

    public DocumentPreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        LibreOffice.ProgramOverride = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A small PDF: pages of 200 × 100 points, each with a black box at (10, 10)-(60, 30) and a line of text.</summary>
    public static byte[] SamplePdf(int pages = 2)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + i * 2} 0 R")) + $"] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < pages; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents {5 + i * 2} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            var content = $"0 0 0 rg 10 10 50 20 re f BT /F1 12 Tf 80 60 Td (Page {i + 1}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
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

    /// <summary>A zip package of the given parts.</summary>
    public static byte[] Package(params (string Path, string Content)[] parts)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return memory.ToArray();
    }

    public static byte[] SampleDocx() => Package(
        ("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
              <w:p><w:pPr><w:pStyle w:val="Balk1"/></w:pPr><w:r><w:t>Quarterly report</w:t></w:r></w:p>
              <w:p><w:r><w:t xml:space="preserve">Sales were </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>up 12%</w:t></w:r><w:r><w:t> &amp; rising.</w:t></w:r></w:p>
              <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>First point</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Region</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Total</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """),
        ("word/styles.xml", """
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:style w:type="paragraph" w:styleId="Balk1"><w:name w:val="heading 1"/></w:style>
            </w:styles>
            """));

    public static byte[] SampleXlsx() => Package(
        ("xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Budget" sheetId="1" r:id="rId1"/><sheet name="Secret" sheetId="2" state="hidden" r:id="rId2"/></sheets>
            </workbook>
            """),
        ("xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="worksheet" Target="worksheets/sheet2.xml"/>
            </Relationships>
            """),
        ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Item</t></si><si><r><t>Ren</t></r><r><t>t</t></r></si></sst>"""),
        ("xl/styles.xml", """
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs>
            </styleSheet>
            """),
        ("xl/worksheets/sheet1.xml", """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
              <row r="1"><c r="A1" t="s"><v>0</v></c><c r="C1" t="inlineStr"><is><t>Due</t></is></c></row>
              <row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2"><v>1250.5</v></c><c r="C2" s="1"><v>45658</v></c></row>
            </sheetData></worksheet>
            """),
        ("xl/worksheets/sheet2.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>hidden</t></is></c></row></sheetData></worksheet>"""));

    [Fact]
    public void A_pdf_is_drawn_page_by_page()
    {
        Assert.True(PdfDocument.IsAvailable);
        using var pdf = PdfDocument.Open(SamplePdf(3));
        Assert.Equal(3, pdf.PageCount);
        Assert.Equal((200, 100), pdf.PageSize(1));
        var pixels = pdf.Render(0, 200, 100);
        Assert.Equal(200 * 100 * 4, pixels.Length);
        byte At(int x, int y) => pixels[(y * 200 + x) * 4]; // the blue byte of a pixel
        Assert.Equal(0, At(30, 80)); // inside the black box (PDF y 20 = 80 from the top)
        Assert.Equal(255, At(150, 90)); // white paper
        Assert.Throws<IOException>(() => PdfDocument.Open("not a pdf at all"u8));
    }

    [Fact]
    public void A_word_document_becomes_html_with_headings_emphasis_lists_and_tables()
    {
        var html = OfficeHtml.ToHtml("docx", new MemoryStream(SampleDocx()));
        Assert.Contains("<h1>Quarterly report</h1>", html, StringComparison.Ordinal); // a localized heading style by its name
        Assert.Contains("Sales were <b>up 12%</b> &amp; rising.", html, StringComparison.Ordinal);
        Assert.Contains("<ul><li>First point</li></ul>", html, StringComparison.Ordinal);
        Assert.Contains("<td><p>Region</p></td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workbook_becomes_tables_with_letters_numbers_and_dates()
    {
        var html = OfficeHtml.ToHtml("xlsx", new MemoryStream(SampleXlsx()));
        Assert.Contains("<h3>Budget</h3>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", html, StringComparison.Ordinal); // hidden sheets are left out
        Assert.Contains("<th>A</th><th>B</th><th>C</th>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Rent</td>", html, StringComparison.Ordinal); // a rich shared string
        Assert.Contains(1250.5.ToString("G15", CultureInfo.CurrentCulture), html, StringComparison.Ordinal);
        Assert.Contains(new DateTime(2025, 1, 1).ToString("d", CultureInfo.CurrentCulture), html, StringComparison.Ordinal); // serial 45658 with a date format
        Assert.Equal((12, 3), OfficeHtml.CellPosition("C12"));
        Assert.Equal("AA", OfficeHtml.ColumnName(27));
    }

    [Fact]
    public void Slides_and_opendocument_files_show_their_texts()
    {
        var pptx = Package(
            ("ppt/presentation.xml", """<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst></p:presentation>"""),
            ("ppt/_rels/presentation.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId2" Type="slide" Target="slides/slide1.xml"/></Relationships>"""),
            ("ppt/slides/slide1.xml", """
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><p:cSld><p:spTree>
                  <p:sp><p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Roadmap</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:sp><p:nvSpPr><p:nvPr/></p:nvSpPr><p:txBody><a:p><a:r><a:t>Ship in March</a:t></a:r></a:p></p:txBody></p:sp>
                </p:spTree></p:cSld></p:sld>
                """));
        var slides = OfficeHtml.ToHtml("pptx", new MemoryStream(pptx));
        Assert.Contains("1. Roadmap</h3>", slides, StringComparison.Ordinal);
        Assert.Contains("<p>Ship in March</p>", slides, StringComparison.Ordinal);

        var ods = Package(("content.xml", """
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body><office:spreadsheet><table:table table:name="Stock">
                <table:table-row><table:table-cell table:number-columns-repeated="2"><text:p>x</text:p></table:table-cell></table:table-row>
                <table:table-row table:number-rows-repeated="1048575"><table:table-cell table:number-columns-repeated="1024"/></table:table-row>
              </table:table></office:spreadsheet></office:body>
            </office:document-content>
            """));
        var sheet = OfficeHtml.ToHtml("ods", new MemoryStream(ods));
        Assert.Contains("<h3>Stock</h3>", sheet, StringComparison.Ordinal);
        Assert.Contains("<th>1</th><td>x</td><td>x</td></tr></table>", sheet, StringComparison.Ordinal); // the empty repeated rows are not shown

        Assert.Throws<InvalidDataException>(() => OfficeHtml.ToHtml("docx", new MemoryStream(Package(("x.xml", "<x/>")))));
        Assert.True(OfficeHtml.Supports("XLSM"));
        Assert.True(OfficeHtml.Supports("doc")); // the old binary formats too
    }

    [Fact]
    public void A_protected_pdf_opens_with_its_password()
    {
        var bytes = TestDocuments.Pdf(2, password: "geheim");
        var none = Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes));
        Assert.False(none.Wrong);
        Assert.True(Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(bytes, "wrong")).Wrong);
        using var pdf = PdfDocument.Open(bytes, "geheim");
        Assert.Equal(2, pdf.PageCount);
        Assert.Equal("Page 2", pdf.PageText(1).Trim());
    }

    [Fact]
    public void The_text_of_a_page_its_boxes_and_the_character_at_a_point()
    {
        using var pdf = PdfDocument.Open(TestDocuments.Pdf(1, text: _ => "Invoice 42"));
        var text = pdf.PageText(0);
        Assert.Contains("Invoice 42", text, StringComparison.Ordinal);
        var start = text.IndexOf("42", StringComparison.Ordinal);
        var box = Assert.Single(pdf.TextRectangles(0, start, 2));
        // the text starts at x 80, its baseline at 40 points from the top (60 from the bottom of a 100-point page)
        Assert.InRange(box.Left, 80, 160);
        Assert.InRange(box.Bottom, 35, 45);
        Assert.True(box.Top < box.Bottom);
        Assert.Equal(start, pdf.CharacterAt(0, (box.Left + box.Right) / 2 - 2, (box.Top + box.Bottom) / 2));
        Assert.Equal(-1, pdf.CharacterAt(0, 190, 95, 1)); // nothing near the bottom right corner
    }

    [Fact]
    public void Old_word_documents_show_their_text_without_field_codes()
    {
        var doc = TestDocuments.Doc("Quarterly report\rSee \u0013 HYPERLINK \"https://x\" \u0014the site\u0015 now.\rRegion\u0007Total\u0007\u0007\r");
        var html = OfficeHtml.ToHtml("doc", new MemoryStream(doc));
        Assert.Contains("<p>Quarterly report</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p>See the site now.</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("HYPERLINK", html, StringComparison.Ordinal);
        Assert.Contains("<table><tr><td>Region</td><td>Total</td></tr></table>", html, StringComparison.Ordinal); // no paragraph properties: the cell marks make the table
        Assert.True(OfficeHtml.Supports("DOC"));
    }

    [Fact]
    public void An_old_word_document_keeps_its_headings_bold_runs_and_tables()
    {
        //                      0      7                       29          36
        const string text = "Report\rPlain and bold words.\rA1\u0007B1\u0007\u0007After\r";
        byte[] bold = [0x35, 0x08, 0x01]; // sprmCFBold on
        byte[] italic = [0x36, 0x08, 0x01];
        byte[] heading = [0x01, 0x00]; // istd 1: Heading 1
        byte[] cell = [0x00, 0x00, 0x16, 0x24, 0x01]; // sprmPFInTable
        byte[] rowEnd = [0x00, 0x00, 0x16, 0x24, 0x01, 0x17, 0x24, 0x01]; // sprmPFInTable, sprmPFTtp
        var doc = TestDocuments.Doc(text,
            runs: [(17, null), (21, bold), (22, null), (27, italic), (text.Length, null)],
            paragraphs: [(7, heading), (29, null), (32, cell), (35, cell), (36, rowEnd), (text.Length, null)]);
        var html = OfficeHtml.ToHtml("doc", new MemoryStream(doc));
        Assert.Contains("<h1>Report</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Plain and <b>bold</b> <i>words</i>.</p>", html, StringComparison.Ordinal);
        Assert.Contains("<table><tr><td>A1</td><td>B1</td></tr></table><p>After</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_workbooks_show_their_visible_sheets()
    {
        var html = OfficeHtml.ToHtml("xls", new MemoryStream(TestDocuments.Xls()));
        Assert.Contains("<h3>Data</h3>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", html, StringComparison.Ordinal);
        Assert.Contains("<td><b>Item</b></td>", html, StringComparison.Ordinal); // its font is bold
        Assert.Contains(3.5.ToString("G15", CultureInfo.CurrentCulture), html, StringComparison.Ordinal);
        Assert.Contains(">42</td>", html, StringComparison.Ordinal); // an RK number
        Assert.Contains("Ünlü " + new string('x', 30), System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal); // a shared string that goes on in a CONTINUE record
        Assert.Contains(new DateTime(2025, 1, 1).ToString("d", CultureInfo.CurrentCulture), html, StringComparison.Ordinal);
        Assert.Contains("<td>ok</td>", html, StringComparison.Ordinal); // a formula's text
    }

    [Fact]
    public void Old_presentations_show_their_slides()
    {
        var html = OfficeHtml.ToHtml("ppt", new MemoryStream(TestDocuments.Ppt()));
        Assert.Contains("1. Agenda</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Point one</p><p>Point two</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_compound_file_reads_big_and_small_streams()
    {
        var big = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        var file = CompoundFile.Open(TestDocuments.CompoundFile(("Big", big), ("Small", "tiny"u8.ToArray())));
        Assert.Equal(big, file.Read("Big"));
        Assert.Equal("tiny"u8.ToArray(), file.Read("small")); // the mini stream; names in any case
        Assert.Null(file.Read("Missing"));
        Assert.Throws<InvalidDataException>(() => CompoundFile.Open(new byte[600]));
        Assert.Throws<InvalidDataException>(() => OfficeHtml.ToHtml("doc", new MemoryStream("not a document"u8.ToArray())));
    }

    [Fact]
    public async Task LibreOffice_converts_other_documents_to_pdf_once()
    {
        var sample = Path.Combine(_dir, "sample.pdf");
        File.WriteAllBytes(sample, SamplePdf(1));
        var count = Path.Combine(_dir, "count.txt");
        string program;
        if (OperatingSystem.IsWindows())
        {
            program = Path.Combine(_dir, "soffice.cmd");
            File.WriteAllText(program, $"""
                @echo off
                echo x>>"{count}"
                :loop
                if "%~1"=="--outdir" goto found
                shift
                goto loop
                :found
                copy /y "{sample}" "%~2\%~n3.pdf" >nul
                """);
        }
        else
        {
            program = Path.Combine(_dir, "soffice");
            File.WriteAllText(program, $$"""
                #!/bin/sh
                echo x >> "{{count}}"
                while [ "$1" != "--outdir" ]; do shift; done
                name=$(basename "$3"); cp "{{sample}}" "$2/${name%.*}.pdf"
                """);
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        LibreOffice.ProgramOverride = program;
        var document = Path.Combine(_dir, "old report.doc");
        File.WriteAllText(document, "binary word document " + Guid.NewGuid());
        Assert.True(LibreOffice.Supports("DOC"));

        var pdf = await LibreOffice.ToPdfAsync(document, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("old report.pdf", Path.GetFileName(pdf));
        using (var opened = PdfDocument.Open(File.ReadAllBytes(pdf)))
        {
            Assert.Equal(1, opened.PageCount);
        }
        Assert.Equal(pdf, await LibreOffice.ToPdfAsync(document, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Single(File.ReadAllLines(count)); // the second time from the cache
    }
}
