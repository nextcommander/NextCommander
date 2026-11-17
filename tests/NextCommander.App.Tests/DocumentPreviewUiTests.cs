using System.Globalization;
using System.IO.Compression;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Views.Lister;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>PDF pages and Office documents in the Lister and the Quick View panel.</summary>
public sealed class DocumentPreviewUiTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>A PDF of pages 200 × 100 points with a black box and a line of text.</summary>
    private static byte[] SamplePdf(int pages)
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

    private static void WriteDocx(string file)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        writer.Write("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Minutes of the meeting</w:t></w:r></w:p></w:body></w:document>""");
    }

    private static void WriteXlsx(string file)
    {
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        void Part(string path, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open());
            writer.Write(xml);
        }
        Part("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Budget" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Part("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
        Part("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Item</t></is></c><c r="B1" t="inlineStr"><is><t>Amount</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>Rent</t></is></c><c r="B2"><v>1250</v></c></row></sheetData></worksheet>""");
    }

    private static async Task<VfsEntry> EntryAsync(string file) =>
        (await new NextCommander.Vfs.Local.LocalFileSystemProvider().GetEntryAsync(VfsPath.Local(file), CancellationToken.None))!;

    [AvaloniaFact]
    public async Task The_lister_shows_pdf_pages_and_office_documents()
    {
        var (_, vm) = await _fx.OpenAsync();
        var pdf = _fx.L("manual.pdf");
        File.WriteAllBytes(pdf, SamplePdf(40));
        var docx = _fx.L("minutes.docx");
        WriteDocx(docx);
        var window = _fx.Track(new ListerWindow(new ListerRequest([await EntryAsync(pdf), await EntryAsync(docx)], 0, vm.Tools)) { Width = 900, Height = 700 });
        window.Show();
        await WaitUntilAsync(() => window.View.Entry is not null && window.View.Loading is { IsCompleted: true });
        Assert.Equal(ListerMode.Preview, window.View.Mode);
        var pages = Assert.IsType<PdfView>(window.View.PdfView);
        Assert.Equal(40, pages.PageCount);
        await WaitUntilAsync(() => pages.DrawnPages > 0, 8000);
        Assert.InRange(pages.DrawnPages, 1, 30); // only the pages near the view are drawn
        Assert.Contains("Page 1/40", window.View.StatusText, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            await SettleAsync(6);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "lister-pdf.png"));
        }
        pages.Zoom(2);
        Assert.Contains("200", window.View.StatusText, StringComparison.Ordinal);

        await window.View.LoadAsync(await EntryAsync(docx));
        Assert.Equal(ListerMode.Preview, window.View.Mode);
        Assert.Null(window.View.PdfView);
        Assert.Contains("Minutes of the meeting", window.View.PreviewHtml, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            await SettleAsync(6);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(shots, "lister-docx.png"));
        }

        var xlsx = _fx.L("budget.xlsx");
        WriteXlsx(xlsx);
        await window.View.LoadAsync(await EntryAsync(xlsx));
        Assert.Contains("<th>B</th>", window.View.PreviewHtml, StringComparison.Ordinal);
        Assert.Contains("<td>Rent</td>", window.View.PreviewHtml, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } sheet)
        {
            await SettleAsync(6);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(sheet, "lister-xlsx.png"));
        }
    }

    [AvaloniaFact]
    public async Task A_pdf_is_searched_selected_and_its_password_asked()
    {
        var (_, vm) = await _fx.OpenAsync();
        var pdf = _fx.L("report.pdf");
        File.WriteAllBytes(pdf, NextCommander.Tests.Shared.TestDocuments.Pdf(30, text: i => i is 4 or 21 ? $"Total due {i}" : $"Page {i + 1}"));
        var window = _fx.Track(new ListerWindow(new ListerRequest([await EntryAsync(pdf)], 0, vm.Tools)) { Width = 900, Height = 700 });
        window.Show();
        await WaitUntilAsync(() => window.View.Loading is { IsCompleted: true } && window.View.PdfView is not null);
        var pages = window.View.PdfView!;

        // F7 / F3: the find bar searches the pages' text; the match is marked and its page scrolled to
        window.View.SetFindText("total DUE");
        Assert.True(await window.View.FindAsync(forward: true));
        Assert.Equal((5, "Total due"), pages.Match);
        await SettleAsync(2);
        Assert.Contains("Page 5/30", window.View.StatusText, StringComparison.Ordinal);
        Assert.True(await window.View.FindAsync(forward: true));
        Assert.Equal(22, pages.Match!.Value.Page);
        Assert.False(await window.View.FindAsync(forward: true)); // no more
        Assert.Contains("not found", window.View.StatusText, StringComparison.Ordinal);
        Assert.True(await window.View.FindAsync(forward: false));
        Assert.Equal(5, pages.Match!.Value.Page);

        // selecting text of a page
        pages.Select(4, 0, 9);
        Assert.Equal("Total due", pages.SelectedText);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            await SettleAsync(6);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "lister-pdf-find.png"));
        }

        // a protected PDF asks for its password: a wrong one asks again, cancelling shows why it is not shown
        var secret = _fx.L("secret.pdf");
        File.WriteAllBytes(secret, NextCommander.Tests.Shared.TestDocuments.Pdf(1, password: "s3cret"));
        _fx.Dialogs.Passwords.Enqueue("wrong");
        _fx.Dialogs.Passwords.Enqueue("s3cret");
        await window.View.LoadAsync(await EntryAsync(secret));
        Assert.Equal(1, window.View.PdfView!.PageCount);
        Assert.Empty(_fx.Dialogs.Passwords);
        await window.View.LoadAsync(await EntryAsync(secret)); // no password given this time
        Assert.Null(window.View.PdfView);
    }

    [AvaloniaFact]
    public async Task An_old_word_document_is_previewed_without_office()
    {
        var (_, vm) = await _fx.OpenAsync();
        var doc = _fx.L("letter.doc");
        File.WriteAllBytes(doc, NextCommander.Tests.Shared.TestDocuments.Doc("Dear customer,\rThank you.\r"));
        var window = _fx.Track(new ListerWindow(new ListerRequest([await EntryAsync(doc)], 0, vm.Tools)));
        window.Show();
        await WaitUntilAsync(() => window.View.Loading is { IsCompleted: true } && window.View.PreviewHtml is not null);
        Assert.Equal(ListerMode.Preview, window.View.Mode);
        Assert.Contains("<p>Dear customer,</p><p>Thank you.</p>", window.View.PreviewHtml, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_quick_view_panel_shows_a_pdf()
    {
        var (w, vm) = await _fx.OpenAsync();
        File.WriteAllBytes(_fx.L("invoice.pdf"), SamplePdf(1));
        await vm.Left.NavigateAsync(vm.Left.CurrentPath!);
        vm.Left.MoveCursorTo(vm.Left.Items.ToList().FindIndex(i => i.FullName == "invoice.pdf"));
        await vm.ExecuteAsync("nc_QuickView");
        await SettleAsync(4);
        await w.LoadQuickViewAsync();
        await w.QuickView!.Loading!;
        Assert.Equal("invoice.pdf", w.QuickView.Entry!.Name);
        var pages = Assert.IsType<PdfView>(w.QuickView.PdfView);
        Assert.Equal(1, pages.PageCount);
        await WaitUntilAsync(() => pages.DrawnPages == 1, 8000);
        await vm.ExecuteAsync("nc_QuickView");
    }
}
