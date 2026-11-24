using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using ItrmTracker.Application.Tracker;

namespace ItrmTracker.Application.Export;

/// <summary>
/// Minimal Office Open XML (.xlsx) writer using only the BCL: one worksheet, inline strings, bold frozen header row and
/// wrapped text. Replaces the SpreadsheetML 2003 ".xls" the original browser code generated (which Excel opens with a
/// format warning); the columns and values are the same.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                $"<sheets><sheet name=\"{Esc(SafeSheetName(sheetName))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");
            Add(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8EAED\"/></patternFill></fill></fills>" +
                "<borders count=\"1\"><border/></borders>" +
                "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
                "<cellXfs count=\"3\"><xf/>" +
                "<xf fontId=\"1\" fillId=\"2\" applyFont=\"1\" applyFill=\"1\"/>" +
                "<xf applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs>" +
                "</styleSheet>");

            var sheet = new StringBuilder();
            sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sheet.Append("<sheetData>");
            AppendRow(sheet, headers, 1);
            foreach (var row in rows) AppendRow(sheet, row, 2);
            sheet.Append("</sheetData></worksheet>");
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, int style)
    {
        sb.Append("<row>");
        foreach (var cell in cells)
        {
            sb.Append($"<c t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{Esc(Clean(cell))}</t></is></c>");
        }
        sb.Append("</row>");
    }

    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray());
        return cleaned.Length > 31 ? cleaned[..31] : cleaned.Length == 0 ? "Sheet1" : cleaned;
    }

    /// <summary>Removes characters that are illegal in XML 1.0.</summary>
    private static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : new string(s.Where(c => c is '\t' or '\n' or '\r' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF).ToArray());

    private static string Esc(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Builds the export workbook for tracker rows with the given columns.</summary>
    public static byte[] WriteTracker(string sheetName, IReadOnlyList<TrackerColumn> columns, IEnumerable<TrackerRecord> rows) =>
        Write(sheetName, columns.Select(c => c.Label).ToList(), rows.Select(r => (IReadOnlyList<string>)columns.Select(c => TrackerColumns.Text(r, c)).ToList()));
}

/// <summary>
/// Builds an Outlook-compatible unsent e-mail draft (.eml) containing the selected records as an HTML table
/// (port of buildEmlDraft / buildEmailTableHtml). Nothing is sent by the server.
/// </summary>
public static class EmlBuilder
{
    public static string CleanAddressList(string? value) =>
        string.Join("; ", (value ?? string.Empty).Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

    public static string TableHtml(IEnumerable<TrackerRecord> rows, IReadOnlyList<TrackerColumn> cols, Func<TrackerRecord, TrackerColumn, string>? cell = null)
    {
        cell ??= TrackerColumns.Text;
        const string th = "border:1px solid #bbb;padding:4px 8px;background:#e8eaed;text-align:left;font-weight:bold;";
        const string td = "border:1px solid #bbb;padding:4px 8px;vertical-align:top;";
        var head = string.Concat(cols.Select(c => $"<th style=\"{th}\">{WebUtility.HtmlEncode(c.Label)}</th>"));
        var body = string.Concat(rows.Select(r => "<tr>" + string.Concat(cols.Select(c => $"<td style=\"{td}\">{WebUtility.HtmlEncode(cell(r, c))}</td>")) + "</tr>"));
        return $"<table style=\"border-collapse:collapse;font-family:Calibri,Arial,sans-serif;font-size:11pt;\"><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>";
    }

    public static string Build(string? to, string? cc, string subject, string? message, string tableHtml)
    {
        var messageHtml = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : $"<p>{WebUtility.HtmlEncode(message.Trim()).Replace("\r\n", "<br>").Replace("\n", "<br>")}</p>";
        var html = $"<html><body style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt;\">{messageHtml}{tableHtml}</body></html>";
        var encodedSubject = $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))}?=";
        var headers = new List<string>
        {
            "X-Unsent: 1", // makes Outlook open the file as an editable draft
            $"To: {Sanitize(CleanAddressList(to))}",
        };
        var ccList = CleanAddressList(cc);
        if (ccList.Length > 0) headers.Add($"Cc: {Sanitize(ccList)}");
        headers.Add($"Subject: {encodedSubject}");
        headers.Add("MIME-Version: 1.0");
        headers.Add("Content-Type: text/html; charset=utf-8");
        headers.Add("Content-Transfer-Encoding: base64");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(html), Base64FormattingOptions.None);
        var wrapped = new StringBuilder();
        for (var i = 0; i < b64.Length; i += 76) wrapped.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        return string.Join("\r\n", headers) + "\r\n\r\n" + wrapped;
    }

    /// <summary>Prevents header injection through the address fields.</summary>
    private static string Sanitize(string s) => s.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
