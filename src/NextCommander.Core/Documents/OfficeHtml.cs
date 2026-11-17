using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace NextCommander.Core.Documents;

/// <summary>
/// A preview of Office Open XML (docx, xlsx, pptx and their macro / template variants) and OpenDocument (odt, ods, odp)
/// files as HTML, without Office: they are zip files of XML. Text with headings, emphasis, lists and tables, the
/// sheets of a workbook as tables (with column letters and row numbers), the slides of a presentation with their
/// texts; PNG / JPEG / GIF / BMP pictures inline. Long documents are cut (the end says so). Not a faithful layout:
/// fonts, positions and page breaks are left out.
/// </summary>
public static partial class OfficeHtml
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>The most rows and columns of a sheet shown.</summary>
    public const int MaxRows = 500;

    public const int MaxColumns = 40;

    /// <summary>The most bytes of pictures put into one preview.</summary>
    private const long MaxPictureBytes = 12L * 1024 * 1024;

    /// <summary>The most characters of HTML (beyond this the rest is left out).</summary>
    private const int MaxHtml = 4 * 1024 * 1024;

    private static readonly HashSet<string> s_word = new(StringComparer.OrdinalIgnoreCase) { "docx", "docm", "dotx", "dotm" };
    private static readonly HashSet<string> s_excel = new(StringComparer.OrdinalIgnoreCase) { "xlsx", "xlsm", "xltx", "xltm" };
    private static readonly HashSet<string> s_powerPoint = new(StringComparer.OrdinalIgnoreCase) { "pptx", "pptm", "potx", "potm", "ppsx", "ppsm" };
    private static readonly HashSet<string> s_openDocument = new(StringComparer.OrdinalIgnoreCase) { "odt", "ott", "ods", "ots", "odp", "otp" };

    /// <summary>Whether a file of this extension can be shown.</summary>
    public static bool Supports(string extension) =>
        IsLegacy(extension) || s_word.Contains(extension) || s_excel.Contains(extension) || s_powerPoint.Contains(extension) || s_openDocument.Contains(extension);

    /// <summary>The HTML body of a document (without html / head); an <see cref="InvalidDataException"/> when it is not one.</summary>
    public static string ToHtml(string extension, Stream stream)
    {
        if (IsLegacy(extension))
        {
            return Legacy(extension, stream);
        }
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var writer = new Writer(zip);
        if (s_word.Contains(extension))
        {
            Word(writer);
        }
        else if (s_excel.Contains(extension))
        {
            Excel(writer);
        }
        else if (s_powerPoint.Contains(extension))
        {
            PowerPoint(writer);
        }
        else
        {
            OpenDocument(writer);
        }
        return writer.Result();
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The HTML being built, the pictures put in so far, and whether it was cut.</summary>
    private sealed class Writer(ZipArchive? zip)
    {
        private readonly StringBuilder _html = new();
        private long _pictures;

        /// <summary>The package of an Office Open XML / OpenDocument file (none for the old binary formats).</summary>
        public ZipArchive Zip => Package!;

        private ZipArchive? Package { get; } = zip;

        public bool Full => _html.Length > MaxHtml;

        public bool Cut { get; set; }

        public Writer Raw(string html)
        {
            if (!Full)
            {
                _html.Append(html);
            }
            else
            {
                Cut = true;
            }
            return this;
        }

        public Writer Escaped(string? text) => Raw(WebUtility.HtmlEncode(text ?? string.Empty));

        /// <summary>A picture of the package as an inline image (when it is a kind the renderer shows and there is room).</summary>
        public void Picture(string? path)
        {
            if (path is null || Package is null || Zip.GetEntry(path.TrimStart('/')) is not { } entry)
            {
                return;
            }
            var mime = Path.GetExtension(entry.Name).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => null, // emf, wmf, svg ...: not shown
            };
            if (mime is null || _pictures + entry.Length > MaxPictureBytes)
            {
                return;
            }
            _pictures += entry.Length;
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Raw($"<img src=\"data:{mime};base64,{Convert.ToBase64String(memory.GetBuffer(), 0, (int)memory.Length)}\" style=\"max-width: 100%\" />");
        }

        public string Result()
        {
            if (Cut)
            {
                _html.Append("<p><i>(…)</i></p>");
            }
            return _html.ToString();
        }
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path.TrimStart('/')) is not { } entry)
        {
            return null;
        }
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    /// <summary>The relationships of a part: id → the target's path in the package.</summary>
    private static Dictionary<string, string> Relationships(ZipArchive zip, string part)
    {
        var folder = part.Contains('/', StringComparison.Ordinal) ? part[..part.LastIndexOf('/')] : string.Empty;
        var rels = Load(zip, (folder.Length > 0 ? folder + "/" : string.Empty) + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in rels?.Root?.Elements(Rel + "Relationship") ?? [])
        {
            if ((string?)r.Attribute("Id") is { } id && (string?)r.Attribute("Target") is { } target && (string?)r.Attribute("TargetMode") != "External")
            {
                result[id] = Resolve(folder, target);
            }
        }
        return result;
    }

    /// <summary>"word" + "../media/a.png" → "media/a.png"; "/xl/x.xml" stays absolute.</summary>
    private static string Resolve(string folder, string target)
    {
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }
        var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in target.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }
        return string.Join('/', parts);
    }

    // ---------------------------------------------------------------- Word

    private static void Word(Writer w)
    {
        var document = Load(w.Zip, "word/document.xml") ?? throw new InvalidDataException("Not a Word document.");
        var rels = Relationships(w.Zip, "word/document.xml");
        var headings = WordHeadings(w.Zip);
        var body = document.Root?.Element(W + "body");
        if (body is not null)
        {
            WordBlocks(w, body.Elements(), rels, headings);
        }
    }

    /// <summary>Style id → heading level (1-6; "Title" is 1), from the style names ("heading 1", localized ids too).</summary>
    private static Dictionary<string, int> WordHeadings(ZipArchive zip)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in Load(zip, "word/styles.xml")?.Root?.Elements(W + "style") ?? [])
        {
            var id = (string?)style.Attribute(W + "styleId");
            var name = ((string?)style.Element(W + "name")?.Attribute(W + "val"))?.ToLowerInvariant();
            if (id is null || name is null)
            {
                continue;
            }
            if (name == "title")
            {
                result[id] = 1;
            }
            else if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
            {
                result[id] = Math.Clamp(level, 1, 6);
            }
        }
        return result;
    }

    private static void WordBlocks(Writer w, IEnumerable<XElement> blocks, Dictionary<string, string> rels, Dictionary<string, int> headings)
    {
        var inList = false;
        foreach (var block in blocks)
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            var isList = block.Name == W + "p" && block.Element(W + "pPr")?.Element(W + "numPr") is not null;
            if (inList && !isList)
            {
                w.Raw("</ul>");
                inList = false;
            }
            if (block.Name == W + "p")
            {
                if (isList && !inList)
                {
                    w.Raw("<ul>");
                    inList = true;
                }
                WordParagraph(w, block, rels, headings, isList);
            }
            else if (block.Name == W + "tbl")
            {
                w.Raw("<table>");
                foreach (var row in block.Elements(W + "tr"))
                {
                    w.Raw("<tr>");
                    foreach (var cell in row.Elements(W + "tc"))
                    {
                        var span = (int?)cell.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val") ?? 1;
                        w.Raw(span > 1 ? $"<td colspan=\"{span}\">" : "<td>");
                        WordBlocks(w, cell.Elements(), rels, headings);
                        w.Raw("</td>");
                    }
                    w.Raw("</tr>");
                }
                w.Raw("</table>");
            }
            else if (block.Name == W + "sdt")
            {
                WordBlocks(w, block.Element(W + "sdtContent")?.Elements() ?? [], rels, headings);
            }
        }
        if (inList)
        {
            w.Raw("</ul>");
        }
    }

    private static void WordParagraph(Writer w, XElement paragraph, Dictionary<string, string> rels, Dictionary<string, int> headings, bool listItem)
    {
        var properties = paragraph.Element(W + "pPr");
        var style = (string?)properties?.Element(W + "pStyle")?.Attribute(W + "val");
        var level = style is not null && headings.TryGetValue(style, out var l) ? l : 0;
        var align = (string?)properties?.Element(W + "jc")?.Attribute(W + "val") switch
        {
            "center" => " style=\"text-align: center\"",
            "right" or "end" => " style=\"text-align: right\"",
            _ => string.Empty,
        };
        var tag = listItem ? "li" : level > 0 ? "h" + level : "p";
        w.Raw($"<{tag}{align}>");
        foreach (var child in paragraph.Elements())
        {
            if (child.Name == W + "r")
            {
                WordRun(w, child, rels);
            }
            else if (child.Name == W + "hyperlink" || child.Name == W + "ins" || child.Name == W + "smartTag" || child.Name == W + "fldSimple")
            {
                foreach (var run in child.Descendants(W + "r"))
                {
                    WordRun(w, run, rels);
                }
            }
        }
        w.Raw($"</{tag}>");
    }

    private static void WordRun(Writer w, XElement run, Dictionary<string, string> rels)
    {
        var p = run.Element(W + "rPr");
        bool On(string name) => p?.Element(W + name) is { } e && (string?)e.Attribute(W + "val") is not ("0" or "false" or "none");
        var (bold, italic, underline, strike) = (On("b"), On("i"), On("u"), On("strike"));
        w.Raw(bold ? "<b>" : string.Empty).Raw(italic ? "<i>" : string.Empty).Raw(underline ? "<u>" : string.Empty).Raw(strike ? "<s>" : string.Empty);
        foreach (var part in run.Elements())
        {
            if (part.Name == W + "t")
            {
                w.Escaped(part.Value);
            }
            else if (part.Name == W + "tab")
            {
                w.Raw("&nbsp;&nbsp;&nbsp;&nbsp;");
            }
            else if (part.Name == W + "br" || part.Name == W + "cr")
            {
                w.Raw("<br/>");
            }
            else if (part.Name == W + "drawing" || part.Name == W + "pict")
            {
                foreach (var blip in part.Descendants(A + "blip"))
                {
                    if ((string?)blip.Attribute(R + "embed") is { } id && rels.TryGetValue(id, out var target))
                    {
                        w.Picture(target);
                    }
                }
            }
        }
        w.Raw(strike ? "</s>" : string.Empty).Raw(underline ? "</u>" : string.Empty).Raw(italic ? "</i>" : string.Empty).Raw(bold ? "</b>" : string.Empty);
    }

    // ---------------------------------------------------------------- Excel

    private static void Excel(Writer w)
    {
        var workbook = Load(w.Zip, "xl/workbook.xml") ?? throw new InvalidDataException("Not an Excel workbook.");
        var rels = Relationships(w.Zip, "xl/workbook.xml");
        var strings = (Load(w.Zip, "xl/sharedStrings.xml")?.Root?.Elements(S + "si") ?? [])
            .Select(si => string.Concat(si.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value))).ToList();
        var dateStyles = ExcelDateStyles(w.Zip);
        var date1904 = (string?)workbook.Root?.Element(S + "workbookPr")?.Attribute("date1904") is "1" or "true";
        foreach (var sheet in workbook.Root?.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            if ((string?)sheet.Attribute("state") is "hidden" or "veryHidden"
                || (string?)sheet.Attribute(R + "id") is not { } id || !rels.TryGetValue(id, out var part) || Load(w.Zip, part) is not { } data)
            {
                continue;
            }
            w.Raw("<h3>").Escaped((string?)sheet.Attribute("name")).Raw("</h3>");
            var cells = new Dictionary<(int Row, int Column), string>();
            var (rows, columns) = (0, 0);
            foreach (var row in data.Root?.Element(S + "sheetData")?.Elements(S + "row") ?? [])
            {
                foreach (var cell in row.Elements(S + "c"))
                {
                    if (CellPosition((string?)cell.Attribute("r")) is not { } at || at.Row > MaxRows || at.Column > MaxColumns)
                    {
                        if (CellPosition((string?)cell.Attribute("r")) is { } beyond && (beyond.Row > MaxRows || beyond.Column > MaxColumns))
                        {
                            w.Cut = true;
                        }
                        continue;
                    }
                    var value = ExcelValue(cell, strings, dateStyles, date1904);
                    if (value.Length > 0)
                    {
                        cells[at] = value;
                        rows = Math.Max(rows, at.Row);
                        columns = Math.Max(columns, at.Column);
                    }
                }
            }
            if (cells.Count == 0)
            {
                w.Raw("<p><i>(empty)</i></p>");
                continue;
            }
            Grid(w, rows, columns, (r, c) => cells.GetValueOrDefault((r, c)));
        }
    }

    /// <summary>A sheet as an HTML table with the column letters above and the row numbers on the left.</summary>
    private static void Grid(Writer w, int rows, int columns, Func<int, int, string?> value)
    {
        w.Raw("<table><tr><th></th>");
        for (var c = 1; c <= columns; c++)
        {
            w.Raw("<th>").Raw(ColumnName(c)).Raw("</th>");
        }
        w.Raw("</tr>");
        for (var r = 1; r <= rows; r++)
        {
            w.Raw("<tr><th>").Raw(r.ToString(CultureInfo.InvariantCulture)).Raw("</th>");
            for (var c = 1; c <= columns; c++)
            {
                var text = value(r, c);
                var number = text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out _);
                w.Raw(number ? "<td style=\"text-align: right\">" : "<td>").Escaped(text).Raw("</td>");
            }
            w.Raw("</tr>");
        }
        w.Raw("</table>");
    }

    /// <summary>1 → A, 27 → AA.</summary>
    public static string ColumnName(int column)
    {
        var name = string.Empty;
        for (; column > 0; column = (column - 1) / 26)
        {
            name = (char)('A' + ((column - 1) % 26)) + name;
        }
        return name;
    }

    /// <summary>"C12" → (12, 3).</summary>
    public static (int Row, int Column)? CellPosition(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }
        var column = 0;
        var i = 0;
        for (; i < reference.Length && char.IsAsciiLetter(reference[i]); i++)
        {
            column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        }
        return column > 0 && int.TryParse(reference.AsSpan(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var row) ? (row, column) : null;
    }

    /// <summary>The style indexes whose number format is a date or a time (the built-in ones and custom formats with d/m/y/h codes).</summary>
    private static HashSet<int> ExcelDateStyles(ZipArchive zip)
    {
        var styles = Load(zip, "xl/styles.xml")?.Root;
        var custom = (styles?.Element(S + "numFmts")?.Elements(S + "numFmt") ?? [])
            .Where(f => IsDateFormat((string?)f.Attribute("formatCode")))
            .Select(f => (int?)f.Attribute("numFmtId") ?? -1).ToHashSet();
        var result = new HashSet<int>();
        var index = 0;
        foreach (var xf in styles?.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            if (id is >= 14 and <= 22 or >= 45 and <= 47 || custom.Contains(id))
            {
                result.Add(index);
            }
            index++;
        }
        return result;
    }

    private static bool IsDateFormat(string? format)
    {
        if (format is null)
        {
            return false;
        }
        // without the quoted texts and [colors]: day, month, year or hour codes
        var plain = new StringBuilder();
        var quoted = false;
        var bracket = false;
        foreach (var ch in format)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && ch == '[')
            {
                bracket = true;
            }
            else if (!quoted && ch == ']')
            {
                bracket = false;
            }
            else if (!quoted && !bracket)
            {
                plain.Append(char.ToLowerInvariant(ch));
            }
        }
        var text = plain.ToString();
        return text.IndexOfAny(['d', 'y', 'h']) >= 0 || (text.Contains('m', StringComparison.Ordinal) && !text.Contains('0', StringComparison.Ordinal) && !text.Contains('#', StringComparison.Ordinal));
    }

    private static string ExcelValue(XElement cell, List<string> strings, HashSet<int> dateStyles, bool date1904)
    {
        var type = (string?)cell.Attribute("t");
        var raw = (string?)cell.Element(S + "v");
        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < strings.Count ? strings[index] : string.Empty;
            case "inlineStr":
                return string.Concat(cell.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []);
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            case "str" or "e":
                return raw ?? string.Empty;
        }
        if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return raw ?? string.Empty;
        }
        if ((int?)cell.Attribute("s") is { } style && dateStyles.Contains(style) && number is > -657435 and < 2958466)
        {
            var date = DateTime.FromOADate(number + (date1904 ? 1462 : 0));
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("d", CultureInfo.CurrentCulture)
                : number < 1 ? date.ToString("T", CultureInfo.CurrentCulture) : date.ToString("g", CultureInfo.CurrentCulture);
        }
        return number.ToString("G15", CultureInfo.CurrentCulture);
    }

    // ---------------------------------------------------------------- PowerPoint

    private static void PowerPoint(Writer w)
    {
        var presentation = Load(w.Zip, "ppt/presentation.xml") ?? throw new InvalidDataException("Not a PowerPoint presentation.");
        var rels = Relationships(w.Zip, "ppt/presentation.xml");
        var number = 0;
        foreach (var slideId in presentation.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            number++;
            if ((string?)slideId.Attribute(R + "id") is not { } id || !rels.TryGetValue(id, out var part) || Load(w.Zip, part) is not { } slide)
            {
                continue;
            }
            var slideRels = Relationships(w.Zip, part);
            var shapes = slide.Root?.Element(P + "cSld")?.Element(P + "spTree")?.Descendants().Where(e => e.Name == P + "sp" || e.Name == P + "pic" || e.Name == P + "graphicFrame").ToList() ?? [];
            var title = shapes.FirstOrDefault(s => (string?)s.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type") is "title" or "ctrTitle");
            w.Raw("<div style=\"border: 1px solid #8888; padding: 8px 14px; margin-bottom: 14px\"><h3>")
                .Escaped(title is null ? $"{number}" : $"{number}. {SlideText(title)}").Raw("</h3>");
            foreach (var shape in shapes.Where(s => s != title))
            {
                if (shape.Name == P + "pic")
                {
                    foreach (var blip in shape.Descendants(A + "blip"))
                    {
                        if ((string?)blip.Attribute(R + "embed") is { } embed && slideRels.TryGetValue(embed, out var target))
                        {
                            w.Picture(target);
                        }
                    }
                }
                else if (shape.Descendants(A + "tbl").FirstOrDefault() is { } table)
                {
                    w.Raw("<table>");
                    foreach (var row in table.Elements(A + "tr"))
                    {
                        w.Raw("<tr>");
                        foreach (var cell in row.Elements(A + "tc"))
                        {
                            w.Raw("<td>").Escaped(SlideText(cell)).Raw("</td>");
                        }
                        w.Raw("</tr>");
                    }
                    w.Raw("</table>");
                }
                else if (shape.Element(P + "txBody") is { } body)
                {
                    foreach (var paragraph in body.Elements(A + "p"))
                    {
                        var text = string.Concat(paragraph.Elements().Select(e => e.Name == A + "br" ? "\n" : string.Concat(e.Descendants(A + "t").Select(t => t.Value))));
                        if (text.Length == 0)
                        {
                            continue;
                        }
                        var level = (int?)paragraph.Element(A + "pPr")?.Attribute("lvl") ?? 0;
                        w.Raw(level > 0 ? $"<p style=\"margin-left: {level * 18}px\">" : "<p>").Escaped(text).Raw("</p>");
                    }
                }
            }
            w.Raw("</div>");
        }
    }

    private static string SlideText(XElement element) =>
        string.Join(" ", element.Descendants(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value))).Where(t => t.Length > 0));

    // ---------------------------------------------------------------- OpenDocument

    private static void OpenDocument(Writer w)
    {
        var content = Load(w.Zip, "content.xml") ?? throw new InvalidDataException("Not an OpenDocument file.");
        var body = content.Root?.Element(Office + "body");
        if (body?.Element(Office + "text") is { } text)
        {
            OdfBlocks(w, text.Elements());
        }
        else if (body?.Element(Office + "spreadsheet") is { } spreadsheet)
        {
            foreach (var table in spreadsheet.Elements(Table + "table"))
            {
                if (w.Full)
                {
                    w.Cut = true;
                    return;
                }
                w.Raw("<h3>").Escaped((string?)table.Attribute(Table + "name")).Raw("</h3>");
                var cells = OdfCells(w, table);
                if (cells.Count == 0)
                {
                    w.Raw("<p><i>(empty)</i></p>");
                    continue;
                }
                Grid(w, cells.Keys.Max(k => k.Row), cells.Keys.Max(k => k.Column), (r, c) => cells.GetValueOrDefault((r, c)));
            }
        }
        else if (body?.Element(Office + "presentation") is { } presentation)
        {
            var number = 0;
            foreach (var page in presentation.Elements(Draw + "page"))
            {
                if (w.Full)
                {
                    w.Cut = true;
                    return;
                }
                number++;
                w.Raw("<div style=\"border: 1px solid #8888; padding: 8px 14px; margin-bottom: 14px\"><h3>").Escaped($"{number}").Raw("</h3>");
                foreach (var frame in page.Elements(Draw + "frame"))
                {
                    if (frame.Element(Draw + "text-box") is { } box)
                    {
                        OdfBlocks(w, box.Elements());
                    }
                    else if (frame.Element(Draw + "image") is { } image)
                    {
                        w.Picture((string?)image.Attribute(XLink + "href"));
                    }
                    else if (frame.Element(Table + "table") is { } table)
                    {
                        OdfBlocks(w, [table]);
                    }
                }
                w.Raw("</div>");
            }
        }
    }

    private static void OdfBlocks(Writer w, IEnumerable<XElement> blocks)
    {
        foreach (var block in blocks)
        {
            if (w.Full)
            {
                w.Cut = true;
                return;
            }
            if (block.Name == Text + "p")
            {
                w.Raw("<p>");
                OdfInline(w, block);
                w.Raw("</p>");
            }
            else if (block.Name == Text + "h")
            {
                var level = Math.Clamp((int?)block.Attribute(Text + "outline-level") ?? 1, 1, 6);
                w.Raw($"<h{level}>");
                OdfInline(w, block);
                w.Raw($"</h{level}>");
            }
            else if (block.Name == Text + "list")
            {
                w.Raw("<ul>");
                foreach (var item in block.Elements(Text + "list-item"))
                {
                    w.Raw("<li>");
                    OdfBlocks(w, item.Elements());
                    w.Raw("</li>");
                }
                w.Raw("</ul>");
            }
            else if (block.Name == Table + "table")
            {
                var cells = OdfCells(w, block);
                if (cells.Count > 0)
                {
                    var (rows, columns) = (cells.Keys.Max(k => k.Row), cells.Keys.Max(k => k.Column));
                    w.Raw("<table>");
                    for (var r = 1; r <= rows; r++)
                    {
                        w.Raw("<tr>");
                        for (var c = 1; c <= columns; c++)
                        {
                            w.Raw("<td>").Escaped(cells.GetValueOrDefault((r, c))).Raw("</td>");
                        }
                        w.Raw("</tr>");
                    }
                    w.Raw("</table>");
                }
            }
            else if (block.Name == Text + "section" || block.Name == Text + "table-of-content" || block.Name == Text + "index-body")
            {
                OdfBlocks(w, block.Elements());
            }
        }
    }

    private static void OdfInline(Writer w, XElement element)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    w.Escaped(text.Value);
                    break;
                case XElement e when e.Name == Text + "s":
                    w.Raw(string.Concat(Enumerable.Repeat("&nbsp;", Math.Clamp((int?)e.Attribute(Text + "c") ?? 1, 1, 100))));
                    break;
                case XElement e when e.Name == Text + "tab":
                    w.Raw("&nbsp;&nbsp;&nbsp;&nbsp;");
                    break;
                case XElement e when e.Name == Text + "line-break":
                    w.Raw("<br/>");
                    break;
                case XElement e when e.Name == Draw + "frame":
                    if (e.Element(Draw + "image") is { } image)
                    {
                        w.Picture((string?)image.Attribute(XLink + "href"));
                    }
                    break;
                case XElement e:
                    OdfInline(w, e); // spans, links, bookmarks ...
                    break;
            }
        }
    }

    /// <summary>The non-empty cells of a table (repeated rows and columns spread out, within the limits).</summary>
    private static Dictionary<(int Row, int Column), string> OdfCells(Writer w, XElement table)
    {
        var cells = new Dictionary<(int Row, int Column), string>();
        var r = 0;
        foreach (var row in table.Descendants(Table + "table-row").Where(e => e.Ancestors(Table + "table").First() == table))
        {
            var rowRepeat = Math.Max(1, (int?)row.Attribute(Table + "number-rows-repeated") ?? 1);
            var values = new List<(int Column, string Text)>();
            var c = 0;
            foreach (var cell in row.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
            {
                var repeat = Math.Max(1, (int?)cell.Attribute(Table + "number-columns-repeated") ?? 1);
                var text = string.Join("\n", cell.Elements(Text + "p").Select(p => p.Value));
                for (var i = 0; i < repeat && c < MaxColumns; i++)
                {
                    c++;
                    if (text.Length > 0)
                    {
                        values.Add((c, text));
                    }
                }
                if (c >= MaxColumns && text.Length > 0 && repeat > 0)
                {
                    w.Cut |= c > MaxColumns;
                }
            }
            for (var i = 0; i < rowRepeat && r < MaxRows; i++)
            {
                r++;
                foreach (var (column, text) in values)
                {
                    cells[(r, column)] = text;
                }
            }
            if (r >= MaxRows)
            {
                if (values.Count > 0)
                {
                    w.Cut = true;
                }
                break;
            }
        }
        return cells;
    }
}
