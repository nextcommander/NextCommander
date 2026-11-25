using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.Plugin.Viewers;

namespace NextCommander.App.Tests;

/// <summary>The viewers plugin: each viewer with a file made here.</summary>
public sealed class ViewersPluginTests
{
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    [AvaloniaFact]
    public async Task Csv_and_tsv_are_a_sortable_table()
    {
        var csv = "name;size;note\r\n\"Smith; John\";10;\"says \"\"hi\"\"\"\r\nayşe;9;\"two\nlines\"\r\nb;100;\r\n";
        Assert.Equal(';', TableViewer.DetectSeparator(csv));
        Assert.Equal(',', TableViewer.DetectSeparator("a,b\n1,2\n"));
        var viewer = new TableViewer();
        Assert.True(viewer.CanView("data.CSV", []));
        Assert.False(viewer.CanView("data.txt", []));

        var content = await viewer.ViewAsync(Stream(csv), "data.csv", dark: false, CancellationToken.None);
        var grid = Assert.IsType<DataGrid>(content.Control);
        Assert.Equal(["name", "size", "note"], grid.Columns.Select(c => c.Header as string));
        var rows = ((IEnumerable<object>)grid.ItemsSource!).Cast<TableViewer.TableRow>().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Smith; John", rows[0][0]);
        Assert.Equal("says \"hi\"", rows[0][2]);
        Assert.Equal("two\nlines", rows[1][2]);
        Assert.Equal(string.Empty, rows[2][2]);
        Assert.Contains("Rows: 3", content.Status, StringComparison.Ordinal);

        // numbers sort as numbers (9 < 10 < 100)
        var comparer = grid.Columns[1].CustomSortComparer!;
        Assert.True(comparer.Compare(rows[1], rows[0]) < 0);
        Assert.True(comparer.Compare(rows[0], rows[2]) < 0);
    }

    [AvaloniaFact]
    public async Task Json_xml_and_yaml_are_a_tree()
    {
        var json = TreeViewer.FromJson(Encoding.UTF8.GetBytes("{ \"name\": \"x\", \"list\": [1, true, null, {\"a\": \"b\"}], // a comment\n}"));
        var root = Assert.Single(json.Children);
        Assert.Equal(["name", "list"], root.Children.Select(c => c.Name));
        Assert.Equal(["[0]", "[1]", "[2]", "[3]"], root.Children[1].Children.Select(c => c.Name));
        Assert.Equal("\"x\"", root.Children[0].Value);

        var xml = Assert.Single(TreeViewer.FromXml(Encoding.UTF8.GetBytes("<a id=\"1\"><b>text</b><c/></a>")).Children);
        Assert.Equal(["@id", "b", "c"], xml.Children.Select(c => c.Name));
        Assert.Equal("text", xml.Children[1].Value);

        var yaml = Assert.Single(TreeViewer.FromYaml("server:\n  port: 8080\n  hosts: [a, b]\n  tls: true\n").Children);
        var server = Assert.Single(yaml.Children);
        Assert.Equal(["port", "hosts", "tls"], server.Children.Select(c => c.Name));
        Assert.Equal("8080", server.Children[0].Value);

        var content = await new TreeViewer().ViewAsync(Stream("{\"a\": [1, 2]}"), "x.json", dark: true, CancellationToken.None);
        Assert.IsType<TreeView>(content.Control);
        Assert.Contains("Nodes: 4", content.Status, StringComparison.Ordinal);

        // not readable: says where, then the text
        var broken = await new TreeViewer().ViewAsync(Stream("{\"a\": "), "x.json", dark: false, CancellationToken.None);
        Assert.StartsWith("The file cannot be read as a tree", broken.Text, StringComparison.Ordinal);
        Assert.False(new TreeViewer().CanView("picture.svg", []));
    }

    [AvaloniaFact]
    public async Task An_eml_mail_shows_its_headers_body_and_attachments()
    {
        var picture = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]);
        var eml = $"""
            From: =?utf-8?B?QXnFn2UgWcSxbG1heg==?= <ayse@example.com>
            To: team@example.com
            Subject: =?iso-8859-9?Q?Toplant=FD?= =?utf-8?Q?_notlar=C4=B1?=
            Date: Sun, 22 Nov 2026 10:15:30 +0300
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="outer"

            --outer
            Content-Type: multipart/related; boundary="inner"

            --inner
            Content-Type: text/html; charset=utf-8
            Content-Transfer-Encoding: quoted-printable

            <html><body><p onclick=3D"x()">Merhaba =C3=A7ocuklar</p><script>alert(1)</script><img src=3D"cid:logo@x"><img src=3D"https://tracker.example.com/p.gif"></body></html>
            --inner
            Content-Type: image/png
            Content-ID: <logo@x>
            Content-Transfer-Encoding: base64

            {picture}
            --inner--
            --outer
            Content-Type: application/pdf; name="rapor.pdf"
            Content-Disposition: attachment; filename*=utf-8''rapor%20%C5%9Fubat.pdf
            Content-Transfer-Encoding: base64

            JVBERi0xLjQK
            --outer--
            """;
        var mail = MailViewer.ReadEml(Encoding.UTF8.GetBytes(eml));
        Assert.Equal("Ayşe Yılmaz <ayse@example.com>", mail.From);
        Assert.Equal("Toplantı notları", mail.Subject);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 15, 30, TimeSpan.FromHours(3)), mail.Date);
        Assert.Contains("Merhaba çocuklar", mail.Html, StringComparison.Ordinal);
        var attachment = Assert.Single(mail.Attachments, a => !a.Inline);
        Assert.Equal("rapor şubat.pdf", attachment.Name);
        Assert.Equal("%PDF-1.4\n", Encoding.ASCII.GetString(attachment.Data));

        var page = MailViewer.Page(mail, "x.eml", dark: false);
        Assert.Contains("Toplantı notları", page, StringComparison.Ordinal);
        Assert.Contains("rapor şubat.pdf", page, StringComparison.Ordinal);
        Assert.Contains($"data:image/png;base64,{picture}", page, StringComparison.Ordinal); // the mail's own picture
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"https://tracker", page, StringComparison.Ordinal); // not loaded
        Assert.Contains("Pictures from the internet are not loaded.", page, StringComparison.Ordinal);

        var viewer = new MailViewer();
        Assert.True(viewer.CanView("a.eml", []));
        Assert.False(viewer.CanView("a.msg", "plain text"u8)); // a .msg that is not Outlook's
        Assert.Contains("Attachments: 1", (await viewer.ViewAsync(new MemoryStream(Encoding.UTF8.GetBytes(eml)), "x.eml", false, CancellationToken.None)).Status,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_outlook_msg_shows_its_properties_and_attachments()
    {
        static byte[] U(string text) => Encoding.Unicode.GetBytes(text);
        var properties = new byte[32 + 16];
        BitConverter.GetBytes(0x00390040u).CopyTo(properties, 32); // PR_CLIENT_SUBMIT_TIME, a FILETIME
        BitConverter.GetBytes(new DateTime(2026, 11, 22, 7, 15, 30, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(properties, 40);
        var msg = NextCommander.Tests.Shared.TestDocuments.CompoundFile(
            ("__substg1.0_0037001F", U("Bütçe toplantısı")),
            ("__substg1.0_0C1A001F", U("Ayşe Yılmaz")),
            ("__substg1.0_5D01001F", U("ayse@example.com")),
            ("__substg1.0_0E04001F", U("Ekip")),
            ("__substg1.0_1000001F", U("Merhaba,\nnotlar ekte.")),
            ("__properties_version1.0", properties),
            ("__recip_version1.0_#00000000/__substg1.0_3001001F", U("Ekip")),
            ("__attach_version1.0_#00000000/__substg1.0_3707001F", U("notlar.pdf")),
            ("__attach_version1.0_#00000000/__substg1.0_37010102", Encoding.ASCII.GetBytes("%PDF-1.4")),
            ("__attach_version1.0_#00000001/__substg1.0_3707001F", U("tablo.xlsx")),
            ("__attach_version1.0_#00000001/__substg1.0_37010102", new byte[5000]));
        Assert.True(new MailViewer().CanView("a.msg", msg.AsSpan(0, 512)));

        var mail = MailViewer.ReadMsg(msg);
        Assert.Equal("Bütçe toplantısı", mail.Subject);
        Assert.Equal("Ayşe Yılmaz <ayse@example.com>", mail.From);
        Assert.Equal("Ekip", mail.To);
        Assert.Equal("Merhaba,\nnotlar ekte.", mail.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 7, 15, 30, TimeSpan.Zero), mail.Date);
        Assert.Equal(["notlar.pdf", "tablo.xlsx"], mail.Attachments.Select(a => a.Name));
        Assert.Equal("%PDF-1.4", Encoding.ASCII.GetString(mail.Attachments[0].Data));
        Assert.Equal(5000, mail.Attachments[1].Data.Length);
        var page = MailViewer.Page(mail, "a.msg", dark: false);
        Assert.Contains("Bütçe toplantısı", page, StringComparison.Ordinal);
        Assert.Contains("tablo.xlsx", page, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_epub_book_shows_its_cover_contents_and_chapters()
    {
        using var book = new MemoryStream();
        using (var zip = new ZipArchive(book, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) => Add2(name, Encoding.UTF8.GetBytes(text));
            void Add2(string name, byte[] data)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", """<container><rootfiles><rootfile full-path="OEBPS/content.opf"/></rootfiles></container>""");
            Add("OEBPS/content.opf", """
                <package xmlns="http://www.idpf.org/2007/opf"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Küçük Kitap</dc:title><dc:creator>Ayşe</dc:creator><dc:language>tr</dc:language></metadata>
                <manifest><item id="c1" href="Text/one.xhtml" media-type="application/xhtml+xml"/><item id="c2" href="Text/two.xhtml" media-type="application/xhtml+xml"/>
                <item id="cover" href="Images/cover.png" media-type="image/png" properties="cover-image"/></manifest>
                <spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>
                """);
            Add("OEBPS/Text/one.xhtml", """<html><body><h1>Birinci bölüm</h1><p>Metin</p><img src="../Images/cover.png"/><script>x()</script></body></html>""");
            Add("OEBPS/Text/two.xhtml", """<html><head><title>İkinci</title></head><body><p>Son</p></body></html>""");
            Add2("OEBPS/Images/cover.png", [0x89, 0x50, 0x4E, 0x47]);
        }
        var read = EbookViewer.Read(book.ToArray());
        Assert.Equal("Küçük Kitap", read.Title);
        Assert.Equal("Ayşe", read.Author);
        Assert.Equal(["Birinci bölüm", "İkinci"], read.Chapters.Select(c => c.Title));
        Assert.StartsWith("data:image/png;base64,", read.CoverDataUri, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", read.Chapters[0].Html, StringComparison.Ordinal); // the chapter's own picture
        Assert.DoesNotContain("<script", read.Chapters[0].Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Birinci bölüm", EbookViewer.Page(read, "k.epub", dark: true), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_notebook_shows_its_cells_and_outputs()
    {
        var notebook = """
            {"metadata": {"kernelspec": {"display_name": "Python 3", "language": "python"}},
             "cells": [
              {"cell_type": "markdown", "source": ["# Başlık\n", "Metin"]},
              {"cell_type": "code", "execution_count": 3, "source": "print(1)", "outputs": [
                {"output_type": "stream", "text": ["1\n"]},
                {"output_type": "display_data", "data": {"image/png": "iVBORw0KGgo=\n"}},
                {"output_type": "error", "ename": "ValueError", "evalue": "bad", "traceback": ["\u001b[0;31mValueError\u001b[0m: bad"]}]}
             ], "nbformat": 4}
            """;
        var (html, cells) = NotebookViewer.Render(Encoding.UTF8.GetBytes(notebook), "analiz.ipynb", dark: false);
        Assert.Equal(2, cells);
        Assert.Contains("Python 3", html, StringComparison.Ordinal);
        Assert.Contains("Başlık</h1>", html, StringComparison.Ordinal);
        Assert.Contains("In [3]:", html, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", html, StringComparison.Ordinal);
        Assert.Contains("ValueError: bad", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", html, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Certificates_show_who_and_until_when_in_every_form()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=test.example.com, O=Örnek", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("test.example.com");
        names.AddDnsName("www.example.com");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        var viewer = new CertificateViewer();

        foreach (var (bytes, extension) in new[]
        {
            (certificate.Export(X509ContentType.Cert), ".cer"),
            (Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()), ".pem"),
            (certificate.Export(X509ContentType.Pkcs12), ".pfx"),
        })
        {
            var content = await viewer.ViewAsync(new MemoryStream(bytes), "site" + extension, dark: false, CancellationToken.None);
            Assert.Contains("test.example.com", content.Html, StringComparison.Ordinal);
            Assert.Contains("www.example.com", content.Html, StringComparison.Ordinal);
            Assert.Contains("Self-signed", content.Html, StringComparison.Ordinal);
            Assert.Contains(">Valid<", content.Html, StringComparison.Ordinal);
            Assert.Contains("RSA 2048 bit", content.Html, StringComparison.Ordinal);
            Assert.Contains(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)), content.Html, StringComparison.Ordinal);
        }
        var locked = await viewer.ViewAsync(new MemoryStream(certificate.Export(X509ContentType.Pkcs12, "secret")), "locked.pfx", false, CancellationToken.None);
        Assert.Contains("protected by a password", locked.Html, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_program_shows_its_version_architecture_and_signer()
    {
        // the runtime's own library: version information, a Microsoft Authenticode signature, a .NET assembly
        var program = typeof(object).Assembly.Location;
        var (html, status) = ExecutableViewer.Describe(program, Path.GetFileName(program), dark: false);
        Assert.Contains("Microsoft", html, StringComparison.Ordinal);
        Assert.Contains(".text", html, StringComparison.Ordinal);
        Assert.Contains("System.Private.CoreLib", html, StringComparison.Ordinal);
        Assert.Contains("Signed by", html, StringComparison.Ordinal);
        Assert.Contains("The signature is shown, not checked.", html, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            // (elsewhere a ReadyToRun image marks its machine for its system)
            Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64",
                status, StringComparison.Ordinal);
        }

        // a .NET library: its assembly
        var (library, _) = ExecutableViewer.Describe(typeof(ViewersPluginTests).Assembly.Location, "tests.dll", dark: true);
        Assert.Contains("NextCommander.App.Tests", library, StringComparison.Ordinal);
        Assert.True(new ExecutableViewer().CanView("a.dll", "MZ"u8));
        Assert.False(new ExecutableViewer().CanView("a.dll", "PK"u8));
    }

    [AvaloniaFact]
    public void A_font_shows_its_family_samples_and_characters()
    {
        var font = new[] { @"C:\Windows\Fonts\arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf" }
            .FirstOrDefault(File.Exists);
        if (font is null)
        {
            return;
        }
        var content = FontViewer.Describe(File.ReadAllBytes(font), Path.GetFileName(font), dark: false);
        Assert.Matches("<h1>(Arial|DejaVu Sans)</h1>", content.Html);
        Assert.True(System.Text.RegularExpressions.Regex.Count(content.Html!, "data:image/png;base64,") >= 8);
        var broken = FontViewer.Describe([1, 2, 3], "broken.woff2", dark: true);
        Assert.Contains("cannot be shown", broken.Html, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Contacts_and_calendars_are_cards_in_order()
    {
        var vcf = """
            BEGIN:VCARD
            VERSION:2.1
            N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Y=C4=B1lmaz;Ay=C5=9Fe;;;
            TEL;CELL:+90 555 000 00 00
            EMAIL;INTERNET:ayse@example.com
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:John Smith
            ORG:Example\, Inc.;Sales
            ADR;TYPE=work:;;Main St 1;Springfield;;12345;USA
            NOTE:first line\nsecond
              line
            PHOTO;ENCODING=b;TYPE=PNG:iVBORw0KGgo=
            END:VCARD
            """;
        var cards = await new ContactViewer().ViewAsync(Stream(vcf), "people.vcf", false, CancellationToken.None);
        Assert.Contains("Ayşe Yılmaz", cards.Html, StringComparison.Ordinal);
        Assert.Contains("+90 555 000 00 00 (cell)", cards.Html, StringComparison.Ordinal);
        Assert.Contains("Example, Inc. Sales", cards.Html, StringComparison.Ordinal);
        Assert.Contains("Main St 1, Springfield, 12345, USA (work)", cards.Html, StringComparison.Ordinal);
        Assert.Contains("second line", cards.Html, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", cards.Html, StringComparison.Ordinal);
        Assert.Contains("Contacts: 2", cards.Status, StringComparison.Ordinal);

        var ics = """
            BEGIN:VCALENDAR
            X-WR-CALNAME:Ekip
            BEGIN:VEVENT
            SUMMARY:Later
            DTSTART;TZID=Europe/Istanbul:20261201T090000
            LOCATION:Oda 3
            ATTENDEE;CN=Ayşe:mailto:ayse@example.com
            END:VEVENT
            BEGIN:VEVENT
            SUMMARY:Earlier
            DTSTART:20261122T070000Z
            RRULE:FREQ=WEEKLY
            END:VEVENT
            BEGIN:VEVENT
            SUMMARY:All day
            DTSTART;VALUE=DATE:20261123
            END:VEVENT
            END:VCALENDAR
            """;
        var calendar = await new ContactViewer().ViewAsync(Stream(ics), "team.ics", false, CancellationToken.None);
        var html = calendar.Html!;
        Assert.True(html.IndexOf("Earlier", StringComparison.Ordinal) < html.IndexOf("All day", StringComparison.Ordinal));
        Assert.True(html.IndexOf("All day", StringComparison.Ordinal) < html.IndexOf("Later", StringComparison.Ordinal));
        Assert.Contains("Ayşe (ayse@example.com)", html, StringComparison.Ordinal);
        Assert.Contains("FREQ=WEEKLY", html, StringComparison.Ordinal);
        Assert.Contains("Events: 3", calendar.Status, StringComparison.Ordinal);
        var earlier = ContactViewer.ParseLine("DTSTART:20261122T070000Z")!;
        Assert.Equal(new DateTime(2026, 11, 22, 7, 0, 0, DateTimeKind.Utc).ToLocalTime(), ContactViewer.Time(earlier));
    }

    /// <summary>A TIFF field: tag, type (1 byte, 2 ASCII, 3 short, 4 long, 5 rational), count and its bytes (little-endian).</summary>
    private sealed record TiffField(ushort Tag, ushort Type, uint Count, byte[] Value);

    private static TiffField Ascii(ushort tag, string text) => new(tag, 2, (uint)text.Length + 1, Encoding.ASCII.GetBytes(text + "\0"));

    private static TiffField Short(ushort tag, ushort value) => new(tag, 3, 1, BitConverter.GetBytes(value));

    private static TiffField Long(ushort tag, uint value) => new(tag, 4, 1, BitConverter.GetBytes(value));

    private static TiffField Rationals(ushort tag, params (uint Numerator, uint Denominator)[] values) =>
        new(tag, 5, (uint)values.Length, [.. values.SelectMany(v => BitConverter.GetBytes(v.Numerator).Concat(BitConverter.GetBytes(v.Denominator)))]);

    private static int IfdSize(List<TiffField> fields) => 2 + 12 * fields.Count + 4 + fields.Where(f => f.Value.Length > 4).Sum(f => (f.Value.Length + 1) & ~1);

    /// <summary>One IFD at <paramref name="offset"/> (from the TIFF header): its entries, no next IFD, then the values that do not fit in an entry.</summary>
    private static void WriteIfd(BinaryWriter writer, int offset, List<TiffField> fields)
    {
        var data = offset + 2 + 12 * fields.Count + 4;
        using var extra = new MemoryStream();
        writer.Write((ushort)fields.Count);
        foreach (var field in fields.OrderBy(f => f.Tag))
        {
            writer.Write(field.Tag);
            writer.Write(field.Type);
            writer.Write(field.Count);
            if (field.Value.Length <= 4)
            {
                var inline = new byte[4];
                field.Value.CopyTo(inline, 0);
                writer.Write(inline);
            }
            else
            {
                writer.Write((uint)(data + extra.Length));
                extra.Write(field.Value);
                if (field.Value.Length % 2 == 1)
                {
                    extra.WriteByte(0);
                }
            }
        }
        writer.Write(0u);
        writer.Write(extra.ToArray());
    }

    private static byte[] Segment(byte marker, byte[] content) =>
        [0xFF, marker, (byte)((content.Length + 2) >> 8), (byte)(content.Length + 2), .. content];

    /// <summary>
    /// A real JPEG (8 × 6, Skia) with an Exif APP1 segment written here: camera, lens, date taken, exposure, GPS at
    /// 41° 0' 36" N, 28° 58' 30" E, 100 m; and an XMP segment with a title, keywords, a rating and a label.
    /// </summary>
    internal static byte[] PhotoWithExif()
    {
        List<TiffField> ifd0 = [Ascii(0x010F, "Canon"), Ascii(0x0110, "Canon EOS R5"), Short(0x0112, 1)];
        List<TiffField> exif =
        [
            Ascii(0x9003, "2026:07:14 15:30:45"), Rationals(0x829A, (1, 250)), Rationals(0x829D, (28, 10)), Short(0x8827, 200),
            Rationals(0x920A, (50, 1)), Ascii(0xA434, "RF24-105mm F4 L IS USM"), Short(0xA001, 1),
        ];
        List<TiffField> gps =
        [
            Ascii(0x0001, "N"), Rationals(0x0002, (41, 1), (0, 1), (3600, 100)), Ascii(0x0003, "E"), Rationals(0x0004, (28, 1), (58, 1), (30, 1)),
            new(0x0005, 1, 1, [0]), Rationals(0x0006, (100, 1)), Ascii(0x0010, "T"), Rationals(0x0011, (2705, 10)),
        ];
        // IFD0 (with its two pointers, 24 bytes) is followed by the Exif IFD, then the GPS IFD
        var exifOffset = 8 + IfdSize(ifd0) + 24;
        var gpsOffset = exifOffset + IfdSize(exif);
        ifd0.Add(Long(0x8769, (uint)exifOffset));
        ifd0.Add(Long(0x8825, (uint)gpsOffset));
        using var tiff = new MemoryStream();
        using (var writer = new BinaryWriter(tiff, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("II"u8);
            writer.Write((ushort)42);
            writer.Write(8u);
            WriteIfd(writer, 8, ifd0);
            WriteIfd(writer, exifOffset, exif);
            WriteIfd(writer, gpsOffset, gps);
        }

        using var bitmap = new SkiaSharp.SKBitmap(8, 6);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        var jpeg = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray();
        var packet = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmp:Rating="4" xmp:Label="Red">
            <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Galata Kulesi</rdf:li></rdf:Alt></dc:title>
            <dc:subject><rdf:Bag><rdf:li>istanbul</rdf:li><rdf:li>kule</rdf:li></rdf:Bag></dc:subject>
            </rdf:Description></rdf:RDF></x:xmpmeta>
            """;
        return
        [
            .. jpeg[..2], // SOI
            .. Segment(0xE1, [.. "Exif\0\0"u8, .. tiff.ToArray()]),
            .. Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.UTF8.GetBytes(packet)]),
            .. jpeg[2..],
        ];
    }

    [AvaloniaFact]
    public async Task A_photo_shows_its_camera_date_location_and_every_tag()
    {
        var viewer = new ImageMetadataViewer();
        Assert.True(viewer.CanView("IMG_0001.JPG", []));
        Assert.True(viewer.CanView("raw.cr3", []));
        Assert.False(viewer.CanView("notes.txt", []));
        Assert.False(((NextCommander.Core.Plugins.IViewerPlugin)viewer).IsDefaultView); // next to the picture, not instead of it

        var content = await viewer.ViewAsync(new MemoryStream(PhotoWithExif()), "galata.jpg", dark: false, CancellationToken.None);
        var html = content.Html!;
        Assert.Contains("Canon EOS R5", html, StringComparison.Ordinal); // the make is not repeated
        Assert.DoesNotContain("Canon Canon", html, StringComparison.Ordinal);
        Assert.Contains("RF24-105mm F4 L IS USM", html, StringComparison.Ordinal);
        Assert.Contains(ViewerHtml.E(new DateTime(2026, 7, 14, 15, 30, 45).ToString("F", System.Globalization.CultureInfo.CurrentCulture)), html, StringComparison.Ordinal);
        Assert.Contains("1/250 sec", html, StringComparison.Ordinal);
        Assert.Contains("8 × 6 pixels", html, StringComparison.Ordinal);

        // the place: decimal degrees, degrees / minutes / seconds, altitude, direction and the map's address
        Assert.Contains("41.01, 28.975", html, StringComparison.Ordinal);
        Assert.Contains(ViewerHtml.E("41° 0' 36\" N"), html, StringComparison.Ordinal);
        Assert.Contains(ViewerHtml.E("28° 58' 30\" E"), html, StringComparison.Ordinal);
        Assert.Contains("100 m", html, StringComparison.Ordinal);
        Assert.Contains("(true north)", html, StringComparison.Ordinal);
        var map = "https://www.openstreetmap.org/?mlat=41.01&mlon=28.975#map=16/41.01/28.975";
        Assert.Equal(map, ImageMetadataViewer.MapAddress(41.01, 28.975));
        Assert.Contains($"<a href=\"{map}\">", html, StringComparison.Ordinal);

        // XMP: title, keywords, rating, label
        Assert.Contains("Galata Kulesi", html, StringComparison.Ordinal);
        Assert.Contains("istanbul; kule", html, StringComparison.Ordinal);
        Assert.Contains("4 / 5", html, StringComparison.Ordinal);
        Assert.Contains(">Red<", html, StringComparison.Ordinal);

        // the full list: every directory with its tags, e.g. the ISO speed under its id
        Assert.Contains("All metadata", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Exif SubIFD</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>GPS</h3>", html, StringComparison.Ordinal);
        Assert.Contains("0x8827", html, StringComparison.Ordinal);
        Assert.Contains("Canon EOS R5", content.Status, StringComparison.Ordinal);
        Assert.Contains("41.01, 28.975", content.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("holds no metadata", html, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_picture_without_metadata_says_so()
    {
        using var bitmap = new SkiaSharp.SKBitmap(5, 4);
        bitmap.Erase(SkiaSharp.SKColors.Orange);
        var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
        var (html, _) = ImageMetadataViewer.Describe(new MemoryStream(png), "plain.png", dark: true);
        Assert.Contains("The picture holds no metadata (no EXIF, IPTC or XMP).", html, StringComparison.Ordinal);
        Assert.Contains("5 × 4 pixels", html, StringComparison.Ordinal); // from its own header
        Assert.DoesNotContain("Location", html, StringComparison.Ordinal);

        // not a picture at all: says it cannot be read
        var (broken, status) = ImageMetadataViewer.Describe(new MemoryStream([1, 2, 3, 4]), "broken.jpg", dark: false);
        Assert.Contains("cannot be read", broken, StringComparison.Ordinal);
        Assert.Equal("Not readable", status);
    }
}
