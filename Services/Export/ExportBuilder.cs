using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace WebDS_Test.Services.Export;

/// <summary>
/// Renders a <see cref="TabularData"/> report as CSV or as an Excel workbook. There is no third
/// option: both writers consume the same header/row description, so a report cannot drift between
/// formats and a new export is one description, not two implementations.
/// </summary>
public static class ExportBuilder
{
    public const string CsvContentType = "text/csv";

    public const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// Reads the <c>format</c> query value. Anything that is not an Excel spelling falls back to
    /// CSV, so a hand-typed or missing value still downloads something sensible rather than 404ing.
    /// </summary>
    public static ExportFormat ParseFormat(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            "excel" or "xlsx" or "xls" => ExportFormat.Excel,
            _ => ExportFormat.Csv
        };

    public static ExportFile Build(TabularData data, ExportFormat format) => format switch
    {
        ExportFormat.Excel => new ExportFile(ToExcel(data), ExcelContentType, "xlsx"),
        _ => new ExportFile(ToCsv(data), CsvContentType, "csv")
    };

    // ------------------------------------------------------------------
    // CSV
    // ------------------------------------------------------------------

    /// <summary>
    /// RFC 4180 CSV, prefixed with a UTF-8 BOM. The BOM is not decoration: Excel opens a bare UTF-8
    /// file as the legacy ANSI code page, which turns Arabic product names into mojibake.
    /// </summary>
    public static byte[] ToCsv(TabularData data)
    {
        var sb = new StringBuilder();
        AppendCsvRow(sb, data.Headers.Select(Format));
        foreach (var row in data.Rows)
        {
            AppendCsvRow(sb, row.Select(Format));
        }

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var preamble = Encoding.UTF8.GetPreamble();

        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        return bytes;
    }

    private static void AppendCsvRow(StringBuilder sb, IEnumerable<string> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append(EscapeCsv(cell));
            first = false;
        }

        sb.Append("\r\n");
    }

    /// <summary>
    /// Formats a single value for CSV. Invariant culture throughout: a report written under a
    /// comma-decimal locale would otherwise be unparseable by the very spreadsheets it targets.
    /// </summary>
    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "Yes" : "No",
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string EscapeCsv(string value)
    {
        // Formula injection: a cell that begins with one of these is executed by Excel/Sheets when
        // the file is opened. Prefix with a single quote so it is treated as text.
        if (value.Length > 0 && value.IndexOfAny(new[] { '=', '+', '-', '@', '\t', '\r' }) == 0)
        {
            value = "'" + value;
        }

        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // ------------------------------------------------------------------
    // Excel
    // ------------------------------------------------------------------

    public static byte[] ToExcel(TabularData data)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SafeSheetName(data.SheetName));

        for (var c = 0; c < data.Headers.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = data.Headers[c];
        }

        if (data.Headers.Count > 0)
        {
            var header = sheet.Range(1, 1, 1, data.Headers.Count);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
        }

        for (var r = 0; r < data.Rows.Count; r++)
        {
            var row = data.Rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                SetCell(sheet.Cell(r + 2, c + 1), row[c]);
            }
        }

        if (data.Headers.Count > 0 && data.Rows.Count > 0)
        {
            sheet.Range(1, 1, data.Rows.Count + 1, data.Headers.Count).SetAutoFilter();
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Writes the value with its native type so Excel can sum and sort it, rather than as a string
    /// that only looks like a number.
    /// </summary>
    private static void SetCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case bool b:
                cell.Value = b;
                break;
            case decimal d:
                cell.Value = (double)d;
                cell.Style.NumberFormat.Format = "#,##0.00";
                break;
            case double d:
                cell.Value = d;
                break;
            case int i:
                cell.Value = (double)i;
                break;
            case long l:
                cell.Value = (double)l;
                break;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                break;
            case DateTimeOffset dto:
                cell.Value = dto.UtcDateTime;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                break;
            default:
                cell.Value = value.ToString() ?? string.Empty;
                break;
        }
    }

    /// <summary>Excel rejects a worksheet name longer than 31 characters or containing <c>[]:*?/\</c>.</summary>
    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "Export";
        }

        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }
}
