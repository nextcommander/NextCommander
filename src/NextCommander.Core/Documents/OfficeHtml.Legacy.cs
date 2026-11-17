using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NextCommander.Core.Documents;

/// <summary>
/// The old binary Office formats (Word 97-2003 .doc, Excel 97-2003 .xls, PowerPoint 97-2003 .ppt) read without Office or
/// LibreOffice, at the level of their text: the main text of a document in paragraphs, the visible sheets of a workbook
/// as tables (text, numbers, dates, formula results), the texts of the slides. Protected files say so.
/// </summary>
public static partial class OfficeHtml
{
    private static readonly HashSet<string> s_legacyWord = new(StringComparer.OrdinalIgnoreCase) { "doc", "dot" };
    private static readonly HashSet<string> s_legacyExcel = new(StringComparer.OrdinalIgnoreCase) { "xls", "xlt" };
    private static readonly HashSet<string> s_legacyPowerPoint = new(StringComparer.OrdinalIgnoreCase) { "ppt", "pps", "pot" };

    /// <summary>Whether the extension is an old binary Office format.</summary>
    public static bool IsLegacy(string extension) => s_legacyWord.Contains(extension) || s_legacyExcel.Contains(extension) || s_legacyPowerPoint.Contains(extension);

    private static string Legacy(string extension, Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var file = CompoundFile.Open(memory.ToArray());
        if (file.Has("EncryptedPackage") || file.Has("EncryptedSummary"))
        {
            throw new InvalidDataException("The document is protected by a password.");
        }
        var w = new Writer(null);
        if (s_legacyWord.Contains(extension))
        {
            LegacyWord(w, file);
        }
        else if (s_legacyExcel.Contains(extension))
        {
            LegacyExcel(w, file);
        }
        else
        {
            LegacyPowerPoint(w, file);
        }
        return w.Result();
    }

    private static ushort U16(byte[] data, int offset) => offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : (ushort)0;

    private static uint U32(byte[] data, int offset) => offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : 0;

    private static readonly Encoding s_ansi = NextCommander.Core.Text.EncodingDetector.Get(1252) ?? Encoding.Latin1;

    // ---------------------------------------------------------------- Word 97-2003

    private static void LegacyWord(Writer w, CompoundFile file)
    {
        var document = file.Read("WordDocument") ?? throw new InvalidDataException("Not a Word document.");
        if (U16(document, 0) != 0xA5EC)
        {
            throw new InvalidDataException("Not a Word 97-2003 document.");
        }
        var flags = U16(document, 0x0A);
        if ((flags & 0x0100) != 0)
        {
            throw new InvalidDataException("The document is protected by a password.");
        }
        var table = file.Read((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("The document's table stream is missing.");
        // FIB: base (32), csw + fibRgW, cslw + fibRgLw, cbRgFcLcb + fibRgFcLcb (fcClx / lcbClx are its 34th pair)
        var csw = U16(document, 32);
        var lw = 32 + 2 + csw * 2;
        var cslw = U16(document, lw);
        var ccpText = (int)U32(document, lw + 2 + 3 * 4); // the characters of the main text
        var fcLcb = lw + 2 + cslw * 4 + 2;
        var fcClx = (int)U32(document, fcLcb + 33 * 8);
        var lcbClx = (int)U32(document, fcLcb + 33 * 8 + 4);
        if (lcbClx <= 0 || fcClx < 0 || fcClx + lcbClx > table.Length)
        {
            throw new InvalidDataException("The document's text could not be found.");
        }
        // CLX: Prc entries (skipped), then the piece table (Pcdt)
        var at = fcClx;
        while (at < fcClx + lcbClx && table[at] == 0x01)
        {
            at += 3 + U16(table, at + 1);
        }
        if (at >= fcClx + lcbClx || table[at] != 0x02)
        {
            throw new InvalidDataException("The document's text could not be found.");
        }
        var lcb = (int)U32(table, at + 1);
        var plc = at + 5;
        var pieces = (lcb - 4) / 12;
        var text = new StringBuilder();
        for (var i = 0; i < pieces && text.Length < ccpText; i++)
        {
            var cpStart = (int)U32(table, plc + i * 4);
            var cpEnd = (int)U32(table, plc + (i + 1) * 4);
            var fc = U32(table, plc + (pieces + 1) * 4 + i * 8 + 2);
            var count = Math.Min(cpEnd - cpStart, ccpText - text.Length);
            if (count <= 0)
            {
                continue;
            }
            if ((fc & 0x40000000) != 0)
            {
                var offset = (int)((fc & ~0x40000000u) / 2);
                if (offset >= 0 && offset + count <= document.Length)
                {
                    text.Append(s_ansi.GetString(document, offset, count)); // 8-bit text (cp1252)
                }
            }
            else if (fc + count * 2 <= document.Length)
            {
                text.Append(Encoding.Unicode.GetString(document, (int)fc, count * 2));
            }
        }
        WordText(w, text.ToString());
    }

    /// <summary>Word's text with its marks: paragraphs (CR), cells (7), line breaks (11), fields (19 code 20 result 21).</summary>
    private static void WordText(Writer w, string text)
    {
        var paragraph = new StringBuilder();
        var fieldDepth = 0;
        var inCode = new Stack<bool>();
        void Flush()
        {
            var line = paragraph.ToString().Trim();
            if (line.Length > 0)
            {
                w.Raw("<p>").Escaped(line).Raw("</p>");
            }
            paragraph.Clear();
        }
        foreach (var ch in text)
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            switch (ch)
            {
                case '\x13':
                    fieldDepth++;
                    inCode.Push(true);
                    continue;
                case '\x14':
                    if (inCode.Count > 0)
                    {
                        inCode.Pop();
                        inCode.Push(false);
                    }
                    continue;
                case '\x15':
                    if (inCode.Count > 0)
                    {
                        inCode.Pop();
                    }
                    fieldDepth = Math.Max(0, fieldDepth - 1);
                    continue;
            }
            if (inCode.Count > 0 && inCode.Peek())
            {
                continue; // the field's code (HYPERLINK "...", PAGE ...): its result is shown
            }
            switch (ch)
            {
                case '\r':
                    Flush();
                    break;
                case '\x07':
                    paragraph.Append("   ·   "); // the end of a table cell
                    break;
                case '\x0B':
                    paragraph.Append(' ');
                    break;
                case '\x0C':
                    Flush();
                    break;
                case '\x1E':
                    paragraph.Append('-');
                    break;
                case '\x01' or '\x08' or '\x1F' or '\x05' or '\x02' or '\x03' or '\x04':
                    break; // pictures, drawn objects, optional hyphens, notes and their marks
                case '\t':
                    paragraph.Append("    ");
                    break;
                default:
                    if (!char.IsControl(ch))
                    {
                        paragraph.Append(ch);
                    }
                    break;
            }
        }
        Flush();
    }

    // ---------------------------------------------------------------- Excel 97-2003

    private static void LegacyExcel(Writer w, CompoundFile file)
    {
        var book = file.Read("Workbook") ?? file.Read("Book") ?? throw new InvalidDataException("Not an Excel workbook.");
        var records = ExcelRecords(book);
        var strings = new List<string>();
        var sheets = new List<(string Name, int Position, bool Visible)>();
        var formats = new Dictionary<int, string>();
        var cellFormats = new List<int>();
        var date1904 = false;
        // the workbook globals: up to the first EOF
        for (var i = 0; i < records.Count; i++)
        {
            var (type, start, length) = records[i];
            if (type == 0x000A)
            {
                break;
            }
            switch (type)
            {
                case 0x002F:
                    throw new InvalidDataException("The workbook is protected by a password.");
                case 0x0085 when length >= 8:
                    var visible = book[start + 4] == 0 && book[start + 5] == 0;
                    sheets.Add((XlString(book, start + 6, start + length, byteLength: true).Text, (int)U32(book, start), visible));
                    break;
                case 0x00FC:
                    strings = SharedStrings(book, records, i);
                    break;
                case 0x041E when length >= 3:
                    formats[U16(book, start)] = XlString(book, start + 2, start + length, byteLength: false).Text;
                    break;
                case 0x00E0 when length >= 4:
                    cellFormats.Add(U16(book, start + 2));
                    break;
                case 0x0022 when length >= 2:
                    date1904 = U16(book, start) == 1;
                    break;
            }
        }
        var dateStyles = new HashSet<int>();
        for (var xf = 0; xf < cellFormats.Count; xf++)
        {
            var id = cellFormats[xf];
            if (id is >= 14 and <= 22 or >= 45 and <= 47 || (formats.TryGetValue(id, out var code) && IsDateFormat(code)))
            {
                dateStyles.Add(xf);
            }
        }
        foreach (var (name, position, visible) in sheets)
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            if (!visible)
            {
                continue;
            }
            w.Raw("<h3>").Escaped(name).Raw("</h3>");
            var cells = ExcelSheet(book, records, position, strings, dateStyles, date1904, w);
            if (cells.Count == 0)
            {
                w.Raw("<p><i>(empty)</i></p>");
                continue;
            }
            Grid(w, cells.Keys.Max(k => k.Row), cells.Keys.Max(k => k.Column), (r, c) => cells.GetValueOrDefault((r, c)));
        }
    }

    /// <summary>The BIFF records of a stream: type, where its data starts, its length.</summary>
    private static List<(int Type, int Start, int Length)> ExcelRecords(byte[] book)
    {
        var records = new List<(int, int, int)>();
        for (var at = 0; at + 4 <= book.Length;)
        {
            var type = U16(book, at);
            var length = U16(book, at + 2);
            if (at + 4 + length > book.Length)
            {
                break;
            }
            records.Add((type, at + 4, length));
            at += 4 + length;
        }
        return records;
    }

    /// <summary>An XLUnicodeString (or its short form): the characters, and where it ends.</summary>
    private static (string Text, int End) XlString(byte[] data, int at, int limit, bool byteLength)
    {
        if (at >= limit)
        {
            return (string.Empty, at);
        }
        int count;
        if (byteLength)
        {
            count = data[at];
            at++;
        }
        else
        {
            count = U16(data, at);
            at += 2;
        }
        if (at >= limit)
        {
            return (string.Empty, at);
        }
        var wide = (data[at] & 0x01) != 0;
        at++;
        var bytes = Math.Min(count * (wide ? 2 : 1), limit - at);
        var text = wide ? Encoding.Unicode.GetString(data, at, bytes - bytes % 2) : Encoding.Latin1.GetString(data, at, bytes);
        return (text, at + bytes);
    }

    /// <summary>The shared strings of the SST record and its CONTINUE records (a string may go on in the next one, with a new options byte).</summary>
    private static List<string> SharedStrings(byte[] book, List<(int Type, int Start, int Length)> records, int index)
    {
        var parts = new List<(int Start, int Length)> { (records[index].Start, records[index].Length) };
        for (var i = index + 1; i < records.Count && records[i].Type == 0x003C; i++)
        {
            parts.Add((records[i].Start, records[i].Length));
        }
        var result = new List<string>();
        var part = 0;
        var at = parts[0].Start + 8;
        var unique = (int)U32(book, parts[0].Start + 4);
        int End() => parts[part].Start + parts[part].Length;
        bool Next()
        {
            if (part + 1 >= parts.Count)
            {
                return false;
            }
            part++;
            at = parts[part].Start;
            return true;
        }
        for (var n = 0; n < unique && n < 2_000_000; n++)
        {
            if (at >= End() && !Next())
            {
                break;
            }
            var count = U16(book, at);
            var options = book[at + 2];
            at += 3;
            var wide = (options & 0x01) != 0;
            var runs = (options & 0x08) != 0 ? U16(book, at) : 0;
            at += (options & 0x08) != 0 ? 2 : 0;
            var extended = (options & 0x04) != 0 ? (int)U32(book, at) : 0;
            at += (options & 0x04) != 0 ? 4 : 0;
            var text = new StringBuilder(count);
            while (text.Length < count)
            {
                if (at >= End())
                {
                    if (!Next())
                    {
                        break;
                    }
                    wide = (book[at] & 0x01) != 0; // a continued string starts its part with a new options byte
                    at++;
                }
                var available = (End() - at) / (wide ? 2 : 1);
                var take = Math.Min(count - text.Length, available);
                if (take <= 0)
                {
                    at = End();
                    continue;
                }
                text.Append(wide ? Encoding.Unicode.GetString(book, at, take * 2) : Encoding.Latin1.GetString(book, at, take));
                at += take * (wide ? 2 : 1);
            }
            result.Add(text.ToString());
            // the formatting runs and the extended data are skipped (they may cross into the next part)
            var skip = runs * 4 + extended;
            while (skip > 0)
            {
                var room = End() - at;
                if (room <= 0)
                {
                    if (!Next())
                    {
                        break;
                    }
                    continue;
                }
                var step = Math.Min(room, skip);
                at += step;
                skip -= step;
            }
        }
        return result;
    }

    /// <summary>The non-empty cells of a sheet (its records from the BOF at <paramref name="position"/> to its EOF).</summary>
    private static Dictionary<(int Row, int Column), string> ExcelSheet(byte[] book, List<(int Type, int Start, int Length)> records, int position,
        List<string> strings, HashSet<int> dateStyles, bool date1904, Writer w)
    {
        var cells = new Dictionary<(int Row, int Column), string>();
        var first = records.FindIndex(r => r.Start - 4 == position);
        if (first < 0)
        {
            return cells;
        }
        (int Row, int Column)? formulaCell = null;
        void Put(int row, int column, string text)
        {
            if (text.Length == 0)
            {
                return;
            }
            if (row >= MaxRows || column >= MaxColumns)
            {
                w.Cut = true;
                return;
            }
            cells[(row + 1, column + 1)] = text;
        }
        string Number(double value, int xf) =>
            dateStyles.Contains(xf) && value is > -657435 and < 2958466
                ? DateTime.FromOADate(value + (date1904 ? 1462 : 0)) is var date && date.TimeOfDay == TimeSpan.Zero
                    ? date.ToString("d", CultureInfo.CurrentCulture) : DateTime.FromOADate(value + (date1904 ? 1462 : 0)).ToString("g", CultureInfo.CurrentCulture)
                : value.ToString("G15", CultureInfo.CurrentCulture);
        for (var i = first + 1; i < records.Count; i++)
        {
            var (type, start, length) = records[i];
            if (type == 0x000A || type == 0x0809)
            {
                break; // EOF, or the next substream
            }
            int Row() => U16(book, start);
            int Column() => U16(book, start + 2);
            int Xf() => U16(book, start + 4);
            switch (type)
            {
                case 0x00FD when length >= 10: // LABELSST
                    var index = (int)U32(book, start + 6);
                    Put(Row(), Column(), index < strings.Count ? strings[index] : string.Empty);
                    break;
                case 0x0203 when length >= 14: // NUMBER
                    Put(Row(), Column(), Number(BitConverter.ToDouble(book, start + 6), Xf()));
                    break;
                case 0x027E when length >= 10: // RK
                    Put(Row(), Column(), Number(Rk(U32(book, start + 6)), Xf()));
                    break;
                case 0x00BD when length >= 6: // MULRK
                    var columnFirst = Column();
                    var pairs = (length - 6) / 6;
                    for (var p = 0; p < pairs; p++)
                    {
                        Put(Row(), columnFirst + p, Number(Rk(U32(book, start + 4 + p * 6 + 2)), U16(book, start + 4 + p * 6)));
                    }
                    break;
                case 0x0204 when length >= 8: // LABEL
                    Put(Row(), Column(), XlString(book, start + 6, start + length, byteLength: false).Text);
                    break;
                case 0x0205 when length >= 8: // BOOLERR
                    Put(Row(), Column(), book[start + 7] == 0 ? (book[start + 6] != 0 ? "TRUE" : "FALSE") : "#ERROR");
                    break;
                case 0x0006 when length >= 20: // FORMULA: its cached result
                    if (U16(book, start + 12) == 0xFFFF)
                    {
                        switch (book[start + 6])
                        {
                            case 0:
                                formulaCell = (Row(), Column()); // the text follows in a STRING record
                                break;
                            case 1:
                                Put(Row(), Column(), book[start + 8] != 0 ? "TRUE" : "FALSE");
                                break;
                            case 2:
                                Put(Row(), Column(), "#ERROR");
                                break;
                        }
                    }
                    else
                    {
                        Put(Row(), Column(), Number(BitConverter.ToDouble(book, start + 6), Xf()));
                    }
                    break;
                case 0x0207 when formulaCell is { } cell: // STRING
                    Put(cell.Row, cell.Column, XlString(book, start, start + length, byteLength: false).Text);
                    formulaCell = null;
                    break;
            }
        }
        return cells;
    }

    /// <summary>An RK number: a 30-bit integer or the top of a double, maybe divided by 100.</summary>
    private static double Rk(uint rk)
    {
        var value = (rk & 0x02) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 0x01) != 0 ? value / 100 : value;
    }

    // ---------------------------------------------------------------- PowerPoint 97-2003

    private static void LegacyPowerPoint(Writer w, CompoundFile file)
    {
        var document = file.Read("PowerPoint Document") ?? throw new InvalidDataException("Not a PowerPoint presentation.");
        var listed = new List<List<(int Kind, string Text)>>();
        var drawn = new List<List<(int Kind, string Text)>>();
        PowerPointRecords(document, 0, document.Length, listed, drawn, inSlideList: false, inSlide: false, 0);
        // the texts of the slide list (older files), else those drawn on the slides themselves (newer ones)
        var slides = listed.Any(s => s.Count > 0) ? listed : drawn;
        var number = 0;
        foreach (var slide in slides)
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            number++;
            var title = slide.FirstOrDefault(t => t.Kind is 0 or 6).Text;
            w.Raw("<div style=\"border: 1px solid #8888; padding: 8px 14px; margin-bottom: 14px\"><h3>")
                .Escaped(title is null ? $"{number}" : $"{number}. {Line(title)}").Raw("</h3>");
            foreach (var (kind, text) in slide)
            {
                if (text == title && kind is 0 or 6)
                {
                    continue;
                }
                foreach (var paragraph in text.Split('\r', StringSplitOptions.RemoveEmptyEntries))
                {
                    w.Raw("<p>").Escaped(Line(paragraph)).Raw("</p>");
                }
            }
            w.Raw("</div>");
        }
    }

    private static string Line(string text) => text.Replace('\v', ' ').Trim();

    /// <summary>
    /// Walks the records: in the slide list (SlideListWithText, instance 0) a SlidePersistAtom starts a slide and a
    /// TextHeaderAtom tells the kind of the next text; a SlideContainer starts a slide of the drawn texts.
    /// </summary>
    private static int PowerPointRecords(byte[] data, int at, int end, List<List<(int, string)>> listed, List<List<(int, string)>> drawn,
        bool inSlideList, bool inSlide, int kind)
    {
        while (at + 8 <= end)
        {
            var versionInstance = U16(data, at);
            var type = U16(data, at + 2);
            var length = (int)Math.Min(U32(data, at + 4), (uint)(end - at - 8));
            var body = at + 8;
            if ((versionInstance & 0x0F) == 0x0F)
            {
                var slideList = type == 0x0FF0 && versionInstance >> 4 == 0;
                var slide = type == 0x03EE;
                if (slide)
                {
                    drawn.Add([]);
                }
                kind = PowerPointRecords(data, body, body + length, listed, drawn, inSlideList || slideList, (inSlide || slide) && !slideList, kind);
            }
            else
            {
                switch (type)
                {
                    case 0x03F3 when inSlideList: // SlidePersistAtom
                        listed.Add([]);
                        break;
                    case 0x0F9F when length >= 4: // TextHeaderAtom
                        kind = (int)U32(data, body);
                        break;
                    case 0x0FA0 or 0x0FA8: // TextCharsAtom (UTF-16) / TextBytesAtom (8-bit)
                        var text = type == 0x0FA0 ? Encoding.Unicode.GetString(data, body, length - length % 2) : Encoding.Latin1.GetString(data, body, length);
                        if (inSlideList && listed.Count > 0)
                        {
                            listed[^1].Add((kind, text));
                        }
                        else if (inSlide && drawn.Count > 0)
                        {
                            drawn[^1].Add((kind, text));
                        }
                        break;
                }
            }
            at = body + length;
        }
        return kind;
    }
}
