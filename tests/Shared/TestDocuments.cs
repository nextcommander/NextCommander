using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NextCommander.Tests.Shared;

/// <summary>
/// Documents made for the tests (shared by the Core and App tests): PDFs (with RC4 encryption when a password is given),
/// compound files and the old Office formats in them (.doc, .xls, .ppt).
/// </summary>
public static class TestDocuments
{
    // ---------------------------------------------------------------- PDF

    private static readonly byte[] s_padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>
    /// A PDF of pages 200 × 100 points, each with a black box at (10, 10)-(60, 30) and a line of text ("Page n" unless
    /// <paramref name="text"/> says otherwise); with a password the pages are encrypted (standard security handler, R2).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "PDF's standard security handler (R2) is defined with MD5 and RC4; a test document")]
    public static byte[] Pdf(int pages, string? password = null, Func<int, string>? text = null)
    {
        var id = Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 3)).ToArray();
        byte[]? key = null;
        string? encrypt = null;
        if (password is not null)
        {
            var user = Pad(password);
            var ownerKey = MD5.HashData(Pad("owner-" + password))[..5];
            var owner = Rc4(ownerKey, user);
            const int permissions = -4;
            key = MD5.HashData([.. user, .. owner, .. BitConverter.GetBytes(permissions), .. id])[..5];
            var check = Rc4(key, s_padding);
            encrypt = $"<< /Filter /Standard /V 1 /R 2 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(check)}> /P {permissions} >>";
        }
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + i * 2} 0 R")) + $"] /Count {pages} >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };
        for (var i = 0; i < pages; i++)
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents {5 + i * 2} 0 R /Resources << /Font << /F1 3 0 R >> >> >>"));
            var content = Ascii($"0 0 0 rg 10 10 50 20 re f BT /F1 12 Tf 80 60 Td ({text?.Invoke(i) ?? $"Page {i + 1}"}) Tj ET");
            if (key is not null)
            {
                var number = 5 + i * 2;
                var objectKey = MD5.HashData([.. key, (byte)number, (byte)(number >> 8), (byte)(number >> 16), 0, 0])[..10];
                content = Rc4(objectKey, content);
            }
            objects.Add([.. Ascii($"<< /Length {content.Length} >>\nstream\n"), .. content, .. Ascii("\nendstream")]);
        }
        if (encrypt is not null)
        {
            objects.Add(Ascii(encrypt));
        }
        using var pdf = new MemoryStream();
        void Write(string s) => pdf.Write(Ascii(s));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write($"{i + 1} 0 obj\n");
            pdf.Write(objects[i]);
            Write("\nendobj\n");
        }
        var xref = pdf.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
        }
        var trailer = $"<< /Size {objects.Count + 1} /Root 1 0 R /ID [<{Convert.ToHexString(id)}> <{Convert.ToHexString(id)}>]"
            + (encrypt is not null ? $" /Encrypt {objects.Count} 0 R" : string.Empty) + " >>";
        Write($"trailer\n{trailer}\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Pad(string password) => [.. Encoding.ASCII.GetBytes(password).Take(32), .. s_padding.Take(32 - Math.Min(32, password.Length))];

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var result = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return result;
    }

    // ---------------------------------------------------------------- compound files

    /// <summary>A compound file (version 3, 512-byte sectors) of streams; those under 4096 bytes go into the mini stream.</summary>
    public static byte[] CompoundFile(params (string Name, byte[] Data)[] streams)
    {
        const int sector = 512;
        const uint end = 0xFFFFFFFE, fatMark = 0xFFFFFFFD, free = 0xFFFFFFFF;
        var big = streams.Where(s => s.Data.Length >= 4096).ToList();
        var small = streams.Where(s => s.Data.Length < 4096).ToList();
        // the mini stream: small streams in 64-byte pieces
        var mini = new MemoryStream();
        var miniFat = new List<uint>();
        var miniStart = new Dictionary<string, uint>();
        foreach (var (name, data) in small)
        {
            var count = Math.Max(1, (data.Length + 63) / 64);
            miniStart[name] = data.Length == 0 ? end : (uint)miniFat.Count;
            for (var i = 0; i < count; i++)
            {
                miniFat.Add(i == count - 1 ? end : (uint)(miniFat.Count + 1));
            }
            mini.Write(data);
            mini.Write(new byte[count * 64 - data.Length]);
        }
        // the regular sectors: big streams, the mini stream, the mini FAT, the directory, then the FAT
        var body = new List<byte[]>();
        var fat = new List<uint>();
        uint Chain(byte[] data)
        {
            var count = Math.Max(1, (data.Length + sector - 1) / sector);
            var first = (uint)fat.Count;
            for (var i = 0; i < count; i++)
            {
                var piece = new byte[sector];
                Array.Copy(data, i * sector, piece, 0, Math.Min(sector, data.Length - i * sector));
                body.Add(piece);
                fat.Add(i == count - 1 ? end : (uint)(fat.Count + 1));
            }
            return first;
        }
        var bigStart = big.ToDictionary(s => s.Name, s => Chain(s.Data));
        var miniStreamStart = mini.Length > 0 ? Chain(mini.ToArray()) : end;
        var miniFatBytes = miniFat.SelectMany(BitConverter.GetBytes).ToArray();
        var miniFatStart = miniFat.Count > 0 ? Chain(miniFatBytes) : end;
        var directory = new MemoryStream();
        void Entry(string name, byte type, uint start, long size, int right, int child)
        {
            var entry = new byte[128];
            var chars = Encoding.Unicode.GetBytes(name);
            Array.Copy(chars, entry, chars.Length);
            BitConverter.GetBytes((ushort)(chars.Length + 2)).CopyTo(entry, 64);
            entry[66] = type;
            entry[67] = 1; // black
            BitConverter.GetBytes(free).CopyTo(entry, 68); // left
            BitConverter.GetBytes(right < 0 ? free : (uint)right).CopyTo(entry, 72);
            BitConverter.GetBytes(child < 0 ? free : (uint)child).CopyTo(entry, 76);
            BitConverter.GetBytes(start).CopyTo(entry, 116);
            BitConverter.GetBytes((uint)size).CopyTo(entry, 120);
            directory.Write(entry);
        }
        Entry("Root Entry", 5, miniStreamStart, mini.Length, -1, streams.Length > 0 ? 1 : -1);
        for (var i = 0; i < streams.Length; i++)
        {
            var (name, data) = streams[i];
            Entry(name, 2, data.Length >= 4096 ? bigStart[name] : miniStart[name], data.Length, i + 2 <= streams.Length ? i + 2 : -1, -1);
        }
        while (directory.Length % sector != 0)
        {
            directory.Write(new byte[128]); // empty entries
        }
        var directoryStart = Chain(directory.ToArray());
        var fatSectors = 1;
        while ((fat.Count + fatSectors) > fatSectors * (sector / 4))
        {
            fatSectors++;
        }
        var fatStart = (uint)fat.Count;
        for (var i = 0; i < fatSectors; i++)
        {
            fat.Add(fatMark);
        }
        while (fat.Count % (sector / 4) != 0)
        {
            fat.Add(free);
        }
        var header = new byte[sector];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BitConverter.GetBytes((ushort)0x3E).CopyTo(header, 0x18);
        BitConverter.GetBytes((ushort)3).CopyTo(header, 0x1A);
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(header, 0x1C);
        BitConverter.GetBytes((ushort)9).CopyTo(header, 0x1E);
        BitConverter.GetBytes((ushort)6).CopyTo(header, 0x20);
        BitConverter.GetBytes((uint)fatSectors).CopyTo(header, 0x2C);
        BitConverter.GetBytes(directoryStart).CopyTo(header, 0x30);
        BitConverter.GetBytes(4096u).CopyTo(header, 0x38);
        BitConverter.GetBytes(miniFatStart).CopyTo(header, 0x3C);
        BitConverter.GetBytes((uint)(miniFat.Count + 127) / 128).CopyTo(header, 0x40);
        BitConverter.GetBytes(end).CopyTo(header, 0x44);
        for (var i = 0; i < 109; i++)
        {
            BitConverter.GetBytes(i < fatSectors ? fatStart + (uint)i : free).CopyTo(header, 0x4C + i * 4);
        }
        using var file = new MemoryStream();
        file.Write(header);
        foreach (var piece in body)
        {
            file.Write(piece);
        }
        file.Write(fat.SelectMany(BitConverter.GetBytes).ToArray());
        return file.ToArray();
    }

    // ---------------------------------------------------------------- old Office formats

    /// <summary>
    /// A Word 97-2003 document of a text (Word's marks: \r paragraphs, \u0007 cells, \u0013 \u0014 \u0015 fields), stored
    /// as UTF-16 in one piece.
    /// </summary>
    public static byte[] Doc(string text)
    {
        var document = new byte[0x800 + text.Length * 2 + 64];
        BitConverter.GetBytes((ushort)0xA5EC).CopyTo(document, 0);
        BitConverter.GetBytes((ushort)0x0200).CopyTo(document, 0x0A); // the table stream is 1Table
        BitConverter.GetBytes((ushort)14).CopyTo(document, 32); // csw
        BitConverter.GetBytes((ushort)22).CopyTo(document, 62); // cslw
        BitConverter.GetBytes(text.Length).CopyTo(document, 64 + 3 * 4); // ccpText
        BitConverter.GetBytes((ushort)93).CopyTo(document, 152); // cbRgFcLcb
        BitConverter.GetBytes(0).CopyTo(document, 154 + 33 * 8); // fcClx
        BitConverter.GetBytes(21).CopyTo(document, 154 + 33 * 8 + 4); // lcbClx
        Encoding.Unicode.GetBytes(text).CopyTo(document, 0x800);
        var table = new MemoryStream();
        table.WriteByte(0x02);
        table.Write(BitConverter.GetBytes(16)); // lcb: 2 CPs + 1 PCD
        table.Write(BitConverter.GetBytes(0));
        table.Write(BitConverter.GetBytes(text.Length));
        table.Write(new byte[2]);
        table.Write(BitConverter.GetBytes(0x800)); // fc: UTF-16 at 0x800
        table.Write(new byte[2]);
        return CompoundFile(("WordDocument", document), ("1Table", table.ToArray()));
    }

    private static byte[] Record(int type, byte[] data) => [.. BitConverter.GetBytes((ushort)type), .. BitConverter.GetBytes((ushort)data.Length), .. data];

    private static byte[] Cell(int row, int column, int xf) => [.. BitConverter.GetBytes((ushort)row), .. BitConverter.GetBytes((ushort)column), .. BitConverter.GetBytes((ushort)xf)];

    /// <summary>
    /// An Excel 97-2003 workbook: a visible sheet "Data" (A1 "Item", B1 3.5, A2 42 (an RK), B2 a long text whose shared
    /// string goes on in a CONTINUE record, A3 the date 2025-01-01, B3 a formula's text "ok") and a hidden sheet "Secret".
    /// </summary>
    public static byte[] Xls()
    {
        var longText = "Ünlü " + new string('x', 30);
        static byte[] Utf16String(string s) => [.. BitConverter.GetBytes((ushort)s.Length), 0x01, .. Encoding.Unicode.GetBytes(s)];
        // SST: "Item" (8-bit), then the long one: its first 10 characters in the SST, the rest in a CONTINUE (with an options byte)
        var sst = new List<byte>();
        sst.AddRange(BitConverter.GetBytes(2));
        sst.AddRange(BitConverter.GetBytes(2));
        sst.AddRange([.. BitConverter.GetBytes((ushort)4), 0x00, .. Encoding.ASCII.GetBytes("Item")]);
        sst.AddRange([.. BitConverter.GetBytes((ushort)longText.Length), 0x01, .. Encoding.Unicode.GetBytes(longText[..10])]);
        byte[] continued = [0x00, .. Encoding.Latin1.GetBytes(longText[10..])]; // the rest as 8-bit characters
        var xf = Enumerable.Range(0, 2).Select(i => Record(0x00E0, [0, 0, (byte)(i == 1 ? 14 : 0), 0, .. new byte[16]])).SelectMany(r => r).ToArray();
        byte[] Globals(int dataPosition, int secretPosition) =>
        [
            .. Record(0x0809, [0x00, 0x06, 0x05, 0x00, .. new byte[12]]),
            .. Record(0x0085, [.. BitConverter.GetBytes(dataPosition), 0, 0, 4, 0, .. Encoding.ASCII.GetBytes("Data")]),
            .. Record(0x0085, [.. BitConverter.GetBytes(secretPosition), 1, 0, 6, 0, .. Encoding.ASCII.GetBytes("Secret")]),
            .. Record(0x00FC, [.. sst]),
            .. Record(0x003C, continued),
            .. xf,
            .. Record(0x000A, []),
        ];
        var size = Globals(0, 0).Length;
        byte[] data =
        [
            .. Record(0x0809, [0x00, 0x06, 0x10, 0x00, .. new byte[12]]),
            .. Record(0x00FD, [.. Cell(0, 0, 0), .. BitConverter.GetBytes(0)]),
            .. Record(0x0203, [.. Cell(0, 1, 0), .. BitConverter.GetBytes(3.5)]),
            .. Record(0x027E, [.. Cell(1, 0, 0), .. BitConverter.GetBytes((42 << 2) | 2)]),
            .. Record(0x00FD, [.. Cell(1, 1, 0), .. BitConverter.GetBytes(1)]),
            .. Record(0x0203, [.. Cell(2, 0, 1), .. BitConverter.GetBytes(45658.0)]),
            .. Record(0x0006, [.. Cell(2, 1, 0), 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, .. new byte[6]]),
            .. Record(0x0207, Utf16String("ok")),
            .. Record(0x000A, []),
        ];
        byte[] secret = [.. Record(0x0809, [0x00, 0x06, 0x10, 0x00, .. new byte[12]]), .. Record(0x0204, [.. Cell(0, 0, 0), .. Utf16String("hidden")]), .. Record(0x000A, [])];
        return CompoundFile(("Workbook", [.. Globals(size, size + data.Length), .. data, .. secret]));
    }

    private static byte[] Atom(int type, byte[] data, int instance = 0) =>
        [.. BitConverter.GetBytes((ushort)(instance << 4)), .. BitConverter.GetBytes((ushort)type), .. BitConverter.GetBytes(data.Length), .. data];

    private static byte[] Container(int type, byte[] children, int instance = 0) =>
        [.. BitConverter.GetBytes((ushort)((instance << 4) | 0x0F)), .. BitConverter.GetBytes((ushort)type), .. BitConverter.GetBytes(children.Length), .. children];

    /// <summary>A PowerPoint 97-2003 presentation of one slide: title "Agenda", body "Point one\rPoint two".</summary>
    public static byte[] Ppt()
    {
        byte[] slideList =
        [
            .. Atom(0x03F3, new byte[20]),
            .. Atom(0x0F9F, BitConverter.GetBytes(0)),
            .. Atom(0x0FA0, Encoding.Unicode.GetBytes("Agenda")),
            .. Atom(0x0F9F, BitConverter.GetBytes(1)),
            .. Atom(0x0FA8, Encoding.Latin1.GetBytes("Point one\rPoint two")),
        ];
        return CompoundFile(("PowerPoint Document", Container(0x03E8, Container(0x0FF0, slideList))), ("Current User", new byte[20]));
    }
}
