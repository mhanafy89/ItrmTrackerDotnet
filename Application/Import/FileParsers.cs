using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ItrmTracker.Application.Import;

/// <summary>
/// Dependency-free CSV parser (port of lib/csv-reader.js). Handles quoted fields, embedded commas and newlines,
/// escaped double quotes ("") and both CRLF / LF line endings. Returns rows of string cells.
/// </summary>
public static class CsvParser
{
    public static List<List<string>> Parse(string text)
    {
        // Strip a leading UTF-8 BOM if present (Excel loves to add one).
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var sawAny = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++; // skip the escaped quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
                sawAny = true;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                sawAny = true;
            }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; // consume CRLF as one break
                row.Add(field.ToString());
                rows.Add(row);
                row = new List<string>();
                field.Clear();
                sawAny = false;
            }
            else
            {
                field.Append(ch);
                sawAny = true;
            }
        }

        // Flush the final field/row if the file didn't end with a newline.
        if (sawAny || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        // Drop trailing fully-empty rows.
        while (rows.Count > 0 && rows[^1].All(c => c.Length == 0)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }
}

/// <summary>
/// Minimal .xlsx reader (port of lib/xlsx-reader.js) using only the BCL: returns the FIRST worksheet as rows of
/// strings. Supports shared/inline strings, numbers, booleans and Excel date serials (YYYY-MM-DD when the cell style
/// is a date format). XML is parsed with DTD processing disabled (XXE-safe) and entry sizes are capped (zip-bomb safe).
/// </summary>
public static class XlsxReader
{
    private const long MaxEntryBytes = 200L * 1024 * 1024;
    private static readonly int[] BuiltInDateFormats = { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };

    public static List<List<string>> Parse(byte[] buffer)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(buffer), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Not a valid .xlsx file (missing ZIP end-of-central-directory).");
        }

        using (zip)
        {
            var sharedStrings = ParseSharedStrings(Load(zip, "xl/sharedStrings.xml"));
            var dateStyles = ParseDateStyleIndexes(Load(zip, "xl/styles.xml"));
            var sheetPath = ResolveFirstSheetPath(zip);
            var sheet = Load(zip, sheetPath) ?? Load(zip, "xl/worksheets/sheet1.xml")
                ?? throw new InvalidDataException("No worksheet found in the .xlsx file.");

            var rows = ParseWorksheet(sheet, sharedStrings, dateStyles);
            // Normalize ragged rows to the widest row so headers line up with cells.
            var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            foreach (var r in rows) while (r.Count < width) r.Add(string.Empty);
            return rows;
        }
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        if (entry.Length > MaxEntryBytes) throw new InvalidDataException($"The .xlsx part {path} is too large.");
        using var stream = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }

    private static IEnumerable<XElement> Named(XContainer? node, string localName) =>
        node?.Descendants().Where(e => e.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

    private static string? Attr(XElement e, string localName) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static List<string> ParseSharedStrings(XDocument? doc) =>
        Named(doc, "si").Select(si => string.Concat(Named(si, "t").Select(t => t.Value))).ToList();

    private static HashSet<int> ParseDateStyleIndexes(XDocument? doc)
    {
        var result = new HashSet<int>();
        if (doc is null) return result;

        var dateFmtIds = new HashSet<string>(BuiltInDateFormats.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        foreach (var f in Named(doc, "numFmt"))
        {
            var code = (Attr(f, "formatCode") ?? string.Empty).ToLowerInvariant();
            var id = Attr(f, "numFmtId");
            if (!string.IsNullOrEmpty(id) && code.IndexOfAny(new[] { 'd', 'm', 'y' }) >= 0 && !code.Contains('[')) dateFmtIds.Add(id);
        }
        var cellXfs = Named(doc, "cellXfs").FirstOrDefault();
        if (cellXfs is null) return result;
        var i = 0;
        foreach (var xf in cellXfs.Elements().Where(e => e.Name.LocalName == "xf"))
        {
            var numFmtId = Attr(xf, "numFmtId");
            if (!string.IsNullOrEmpty(numFmtId) && dateFmtIds.Contains(numFmtId)) result.Add(i);
            i++;
        }
        return result;
    }

    private static string ResolveFirstSheetPath(ZipArchive zip)
    {
        const string fallback = "xl/worksheets/sheet1.xml";
        var workbook = Load(zip, "xl/workbook.xml");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        if (workbook is null || rels is null) return fallback;
        var firstSheet = Named(workbook, "sheet").FirstOrDefault();
        var rid = firstSheet is null ? null : Attr(firstSheet, "id");
        if (rid is null) return fallback;
        var rel = Named(rels, "Relationship").FirstOrDefault(r => Attr(r, "Id") == rid);
        if (rel is null) return fallback;
        var target = Attr(rel, "Target") ?? string.Empty;
        target = target.TrimStart('/');
        if (target.StartsWith("xl/", StringComparison.Ordinal)) target = target[3..];
        return "xl/" + target;
    }

    private static int ColumnIndex(string reference)
    {
        var idx = 0;
        foreach (var c in reference.Where(char.IsLetter)) idx = idx * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return idx - 1;
    }

    /// <summary>Excel's epoch is 1899-12-30 (it wrongly treats 1900 as a leap year).</summary>
    public static string ExcelSerialToIso(double serial)
    {
        var days = Common.JsMath.Round(serial);
        var epoch = new DateOnly(1899, 12, 30);
        if (double.IsNaN(days) || days < -epoch.DayNumber || days > DateOnly.MaxValue.DayNumber - epoch.DayNumber)
        {
            return serial.ToString("R", CultureInfo.InvariantCulture);
        }
        return epoch.AddDays((int)days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static List<List<string>> ParseWorksheet(XDocument doc, List<string> sharedStrings, HashSet<int> dateStyles)
    {
        var rows = new List<List<string>>();
        foreach (var rowEl in Named(doc, "row"))
        {
            var cells = new List<string?>();
            foreach (var c in rowEl.Elements().Where(e => e.Name.LocalName == "c"))
            {
                var reference = Attr(c, "r");
                var type = Attr(c, "t");
                var style = Attr(c, "s");
                var colIdx = reference is null ? cells.Count : ColumnIndex(reference);
                string value;
                if (type == "inlineStr")
                {
                    value = Named(c, "t").FirstOrDefault()?.Value ?? string.Empty;
                }
                else
                {
                    var raw = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
                    if (type == "s")
                    {
                        value = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var si) && si >= 0 && si < sharedStrings.Count
                            ? sharedStrings[si]
                            : string.Empty;
                    }
                    else if (type == "str")
                    {
                        value = raw;
                    }
                    else if (type == "b")
                    {
                        value = raw == "1" ? "TRUE" : "FALSE";
                    }
                    else if (raw.Length > 0 && style is not null && int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                             && dateStyles.Contains(s)
                             && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                    {
                        value = ExcelSerialToIso(serial);
                    }
                    else
                    {
                        value = raw;
                    }
                }
                while (cells.Count <= colIdx) cells.Add(null);
                cells[colIdx] = value;
            }
            rows.Add(cells.Select(x => x ?? string.Empty).ToList());
        }
        return rows;
    }
}
