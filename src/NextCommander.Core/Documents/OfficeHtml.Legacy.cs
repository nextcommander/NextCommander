using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NextCommander.Core.Documents;

/// <summary>
/// The old binary Office formats (Word 97-2003 .doc, Excel 97-2003 .xls, PowerPoint 97-2003 .ppt) read without Office or
/// LibreOffice, at the level of their text: the main text of a document in paragraphs, headings and tables with its
/// runs' bold / italic / underline, the visible sheets of a workbook as tables (text, numbers, dates, formula results,
/// bold cells), the texts of the slides. Protected files say so.
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
        var positions = new List<int>(); // each character's place in the WordDocument stream: its formatting is found by it
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
                    positions.AddRange(Enumerable.Range(offset, count));
                }
            }
            else if (fc + count * 2 <= document.Length)
            {
                text.Append(Encoding.Unicode.GetString(document, (int)fc, count * 2));
                positions.AddRange(Enumerable.Range(0, count).Select(k => (int)fc + k * 2));
            }
        }
        WordText(w, text.ToString(), positions, new DocFormats(document, table, fcLcb));
    }

    /// <summary>The formatting of a character run: what is drawn of it.</summary>
    private readonly record struct DocRun(bool Bold, bool Italic, bool Underline, bool Strike);

    /// <summary>The properties of a paragraph used here: its style (1-9: Heading 1-9), in a table, a table row's end.</summary>
    private readonly record struct DocParagraph(int Style, bool InTable, bool RowEnd);

    /// <summary>
    /// The direct formatting of a Word 97-2003 document: the character runs (bold, italic, underline, strikethrough) and
    /// the paragraphs (style, table cell, row end) from the formatted disk pages (FKPs) of the CHPX and PAPX bin tables,
    /// by place in the WordDocument stream.
    /// </summary>
    private sealed class DocFormats
    {
        private readonly List<(int Start, int End, DocRun Run)> _runs = [];
        private readonly List<(int Start, int End, DocParagraph Paragraph)> _paragraphs = [];

        public DocFormats(byte[] document, byte[] table, int fcLcb)
        {
            // FibRgFcLcb97: fcPlcfBteChpx / lcbPlcfBteChpx are its 13th pair, fcPlcfBtePapx / lcbPlcfBtePapx the 14th
            foreach (var (pages, characters) in new[] { (Pages(document, table, fcLcb + 12 * 8), true), (Pages(document, table, fcLcb + 13 * 8), false) })
            {
                foreach (var page in pages)
                {
                    if (characters)
                    {
                        ReadCharacters(document, page);
                    }
                    else
                    {
                        ReadParagraphs(document, page);
                    }
                }
            }
            _runs.Sort((a, b) => a.Start.CompareTo(b.Start));
            _paragraphs.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        /// <summary>Whether the document gave paragraph properties (else tables are guessed from the cell marks).</summary>
        public bool HasParagraphs => _paragraphs.Count > 0;

        /// <summary>The offsets of the 512-byte pages a bin table (PlcfBte) points to.</summary>
        private static IEnumerable<int> Pages(byte[] document, byte[] table, int pair)
        {
            var fc = (int)U32(document, pair);
            var lcb = (int)U32(document, pair + 4);
            if (lcb < 12 || fc < 0 || fc + lcb > table.Length)
            {
                yield break;
            }
            var count = (lcb - 4) / 8;
            for (var i = 0; i < count; i++)
            {
                var page = (int)(U32(table, fc + (count + 1) * 4 + i * 4) & 0x3FFFFF) * 512;
                if (page > 0 && page + 512 <= document.Length)
                {
                    yield return page;
                }
            }
        }

        private void ReadCharacters(byte[] document, int page)
        {
            var count = document[page + 511];
            for (var i = 0; i < count && (count + 1) * 4 + count <= 511; i++)
            {
                var (bold, italic, underline, strike) = (false, false, false, false);
                var offset = document[page + (count + 1) * 4 + i] * 2;
                if (offset > 0)
                {
                    var at = page + offset;
                    Sprms(document, at + 1, document[at], (sprm, operand) =>
                    {
                        switch (sprm)
                        {
                            case 0x0835: // sprmCFBold: 1 on, 0x81 the style's opposite (taken as on)
                                bold = document[operand] is 1 or 0x81;
                                break;
                            case 0x0836: // sprmCFItalic
                                italic = document[operand] is 1 or 0x81;
                                break;
                            case 0x0837: // sprmCFStrike
                                strike = document[operand] is 1 or 0x81;
                                break;
                            case 0x2A3E: // sprmCKul: the kind of underline, 0 none
                                underline = document[operand] != 0;
                                break;
                        }
                    });
                }
                _runs.Add(((int)U32(document, page + i * 4), (int)U32(document, page + (i + 1) * 4), new DocRun(bold, italic, underline, strike)));
            }
        }

        private void ReadParagraphs(byte[] document, int page)
        {
            var count = document[page + 511];
            for (var i = 0; i < count && (count + 1) * 4 + count * 13 <= 511; i++)
            {
                var offset = document[page + (count + 1) * 4 + i * 13] * 2;
                var (style, inTable, rowEnd) = (0, false, false);
                if (offset > 0)
                {
                    var at = page + offset;
                    var (start, length) = document[at] != 0 ? (at + 1, document[at] * 2 - 1) : (at + 2, document[at + 1] * 2);
                    style = U16(document, start);
                    Sprms(document, start + 2, length - 2, (sprm, operand) =>
                    {
                        switch (sprm)
                        {
                            case 0x2416: // sprmPFInTable
                                inTable |= document[operand] != 0;
                                break;
                            case 0x2417: // sprmPFTtp: the mark ending a table row
                                rowEnd |= document[operand] != 0;
                                break;
                            case 0x6649: // sprmPItap: the table depth
                                inTable |= U32(document, operand) > 0;
                                break;
                        }
                    });
                }
                _paragraphs.Add(((int)U32(document, page + i * 4), (int)U32(document, page + (i + 1) * 4), new DocParagraph(style, inTable, rowEnd)));
            }
        }

        /// <summary>The single property modifiers (sprms) of a grpprl: each one's code and where its operand starts.</summary>
        private static void Sprms(byte[] data, int at, int length, Action<ushort, int> each)
        {
            var end = Math.Min(data.Length, at + Math.Max(0, length));
            while (at + 2 <= end)
            {
                var sprm = U16(data, at);
                at += 2;
                var size = (sprm >> 13) switch
                {
                    0 or 1 => 1,
                    2 or 4 or 5 => 2,
                    3 => 4,
                    7 => 3,
                    _ when sprm == 0xD608 => U16(data, at) + 1, // sprmTDefTable: a 2-byte size
                    _ when sprm == 0xC615 && at < end && data[at] == 0xFF => -1, // sprmPChgTabs of the long kind: stop here
                    _ => at < end ? data[at] + 1 : -1, // variable: a size byte, then the operand
                };
                if (size < 0 || at + size > end)
                {
                    return;
                }
                each(sprm, (sprm >> 13) == 6 && sprm != 0xD608 ? at + 1 : at);
                at += size;
            }
        }

        public DocRun RunAt(int position) => Find(_runs, position)?.Value ?? default;

        public DocParagraph? ParagraphAt(int position) => Find(_paragraphs, position)?.Value;

        private static (int Start, int End, T Value)? Find<T>(List<(int Start, int End, T Value)> list, int position)
        {
            var (low, high) = (0, list.Count - 1);
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var (start, end, value) = list[middle];
                if (position < start)
                {
                    high = middle - 1;
                }
                else if (position >= end)
                {
                    low = middle + 1;
                }
                else
                {
                    return (start, end, value);
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Word's text with its marks: paragraphs (CR; a page break 12), table cells (7; the row's end mark is a paragraph of
    /// its own), line breaks (11), fields (19 code 20 result 21: the result is shown). Paragraphs of the heading styles
    /// become headings, the runs' bold / italic / underline / strikethrough is kept.
    /// </summary>
    private static void WordText(Writer w, string text, List<int> positions, DocFormats formats)
    {
        // the characters shown: the fields' codes left out
        var shown = new List<(char Char, int Position)>(text.Length);
        var inCode = new Stack<bool>();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\x13':
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
                    continue;
            }
            if (inCode.Count == 0 || !inCode.Peek())
            {
                shown.Add((ch, i < positions.Count ? positions[i] : -1));
            }
        }

        var (tableOpen, rowOpen, cellOpen, cellHasText) = (false, false, false, false);
        var previousMark = '\0';
        void CloseTable()
        {
            if (cellOpen)
            {
                w.Raw("</td>");
            }
            if (rowOpen)
            {
                w.Raw("</tr>");
            }
            if (tableOpen)
            {
                w.Raw("</table>");
            }
            (tableOpen, rowOpen, cellOpen, cellHasText) = (false, false, false, false);
        }

        var paragraph = new List<(char Char, int Position)>();
        for (var i = 0; i <= shown.Count; i++)
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            var (ch, position) = i < shown.Count ? shown[i] : ('\r', -1);
            if (ch is not ('\r' or '\x07' or '\x0C'))
            {
                paragraph.Add((ch, position));
                continue;
            }
            // a paragraph ends: its properties are those of its mark
            var properties = formats.ParagraphAt(position);
            var empty = paragraph.TrueForAll(c => char.IsWhiteSpace(c.Char) || char.IsControl(c.Char));
            bool inTable, rowEnd;
            if (formats.HasParagraphs && properties is { } known)
            {
                (inTable, rowEnd) = (known.InTable || known.RowEnd, known.RowEnd);
            }
            else
            {
                // no properties: a cell mark makes a table; a cell mark right after one ends the row
                rowEnd = ch == '\x07' && empty && previousMark == '\x07' && !cellOpen;
                inTable = ch == '\x07' || cellOpen;
            }
            previousMark = ch;
            if (rowEnd)
            {
                if (cellOpen)
                {
                    w.Raw("</td>");
                }
                if (rowOpen)
                {
                    w.Raw("</tr>");
                }
                (rowOpen, cellOpen) = (false, false);
            }
            else if (inTable)
            {
                if (!tableOpen)
                {
                    w.Raw("<table>");
                }
                if (!rowOpen)
                {
                    w.Raw("<tr>");
                }
                if (!cellOpen)
                {
                    w.Raw("<td>");
                    cellHasText = false;
                }
                else if (cellHasText && !empty)
                {
                    w.Raw("<br>"); // the next paragraph of the cell
                }
                (tableOpen, rowOpen, cellOpen) = (true, true, true);
                if (!empty)
                {
                    DocRuns(w, paragraph, formats);
                    cellHasText = true;
                }
                if (ch == '\x07')
                {
                    w.Raw("</td>");
                    cellOpen = false;
                }
            }
            else
            {
                CloseTable();
                if (!empty)
                {
                    var tag = properties is { Style: >= 1 and <= 9 } heading ? $"h{Math.Min(heading.Style, 6)}" : "p";
                    w.Raw($"<{tag}>");
                    DocRuns(w, paragraph, formats);
                    w.Raw($"</{tag}>");
                }
            }
            paragraph.Clear();
        }
        CloseTable();
    }

    /// <summary>The characters of a paragraph in runs of the same formatting, the spaces at its ends left out.</summary>
    private static void DocRuns(Writer w, List<(char Char, int Position)> paragraph, DocFormats formats)
    {
        var first = paragraph.FindIndex(c => !char.IsWhiteSpace(c.Char) && !char.IsControl(c.Char));
        var last = paragraph.FindLastIndex(c => !char.IsWhiteSpace(c.Char) && !char.IsControl(c.Char));
        DocRun? open = null;
        var run = new StringBuilder();
        void Close()
        {
            if (open is not { } r)
            {
                return;
            }
            w.Raw(r.Bold ? "<b>" : string.Empty).Raw(r.Italic ? "<i>" : string.Empty).Raw(r.Underline ? "<u>" : string.Empty).Raw(r.Strike ? "<s>" : string.Empty);
            w.Escaped(run.ToString());
            w.Raw(r.Strike ? "</s>" : string.Empty).Raw(r.Underline ? "</u>" : string.Empty).Raw(r.Italic ? "</i>" : string.Empty).Raw(r.Bold ? "</b>" : string.Empty);
            run.Clear();
            open = null;
        }
        for (var i = Math.Max(0, first); first >= 0 && i <= last; i++)
        {
            var (ch, position) = paragraph[i];
            var piece = ch switch
            {
                '\x0B' => "\n", // a line break
                '\t' => "    ",
                '\x1E' => "-", // a non-breaking hyphen
                _ when char.IsControl(ch) => null, // pictures, drawn objects, optional hyphens, notes and their marks
                _ => ch.ToString(),
            };
            if (piece is null)
            {
                continue;
            }
            var format = formats.RunAt(position);
            if (open != format)
            {
                Close();
                open = format;
            }
            if (piece == "\n")
            {
                Close();
                w.Raw("<br>");
                continue;
            }
            run.Append(piece);
        }
        Close();
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
        var fonts = new List<bool>(); // FONT records: bold or not
        var boldStyles = new HashSet<int>();
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
                case 0x0031 when length >= 8: // FONT: its weight (700 bold)
                    fonts.Add(U16(book, start + 6) >= 700);
                    if (fonts.Count == 4)
                    {
                        fonts.Add(false); // font index 4 is never used: the fifth record is font 5
                    }
                    break;
                case 0x00E0 when length >= 4: // XF: its font and number format
                    var font = U16(book, start);
                    if (font < fonts.Count && fonts[font])
                    {
                        boldStyles.Add(cellFormats.Count);
                    }
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
            var bold = new HashSet<(int Row, int Column)>();
            var cells = ExcelSheet(book, records, position, strings, dateStyles, date1904, w, (row, column, xf) =>
            {
                if (boldStyles.Contains(xf))
                {
                    bold.Add((row, column));
                }
            });
            if (cells.Count == 0)
            {
                w.Raw("<p><i>(empty)</i></p>");
                continue;
            }
            Grid(w, cells.Keys.Max(k => k.Row), cells.Keys.Max(k => k.Column), (r, c) => cells.GetValueOrDefault((r, c)), (r, c) => bold.Contains((r, c)));
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

    /// <summary>
    /// The non-empty cells of a sheet (its records from the BOF at <paramref name="position"/> to its EOF);
    /// <paramref name="style"/> is told each cell's XF (1-based row and column).
    /// </summary>
    private static Dictionary<(int Row, int Column), string> ExcelSheet(byte[] book, List<(int Type, int Start, int Length)> records, int position,
        List<string> strings, HashSet<int> dateStyles, bool date1904, Writer w, Action<int, int, int> style)
    {
        var cells = new Dictionary<(int Row, int Column), string>();
        var first = records.FindIndex(r => r.Start - 4 == position);
        if (first < 0)
        {
            return cells;
        }
        (int Row, int Column)? formulaCell = null;
        var xf = -1; // the XF of the cell being read
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
            if (xf >= 0)
            {
                style(row + 1, column + 1, xf);
            }
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
            xf = type is 0x00FD or 0x0203 or 0x027E or 0x0204 or 0x0205 or 0x0006 ? Xf() : -1;
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
                        xf = U16(book, start + 4 + p * 6);
                        Put(Row(), columnFirst + p, Number(Rk(U32(book, start + 4 + p * 6 + 2)), xf));
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
