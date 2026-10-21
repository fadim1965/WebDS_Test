namespace WebDS_Test.Services.Export;

/// <summary>The two shapes an admin report can be downloaded in.</summary>
public enum ExportFormat
{
    Csv = 0,
    Excel = 1
}

/// <summary>
/// One report, described as data rather than as markup: a sheet name, a file stem, fixed column
/// headers and the rows beneath them. Keeping it provider-independent is what lets the same report
/// be written to CSV or to an Excel workbook without either writer knowing what it is exporting.
/// </summary>
/// <param name="SheetName">Worksheet name for Excel; ignored by CSV.</param>
/// <param name="FileStem">Download name before the timestamp and extension are appended.</param>
/// <param name="Headers">Column headings, in order. The row width is taken from here.</param>
/// <param name="Rows">One array per record, positionally matched to <paramref name="Headers"/>.</param>
public sealed record TabularData(
    string SheetName,
    string FileStem,
    IReadOnlyList<string> Headers,
    IReadOnlyList<object?[]> Rows);

/// <summary>A report rendered into bytes, ready to hand to <c>Controller.File</c>.</summary>
public sealed record ExportFile(byte[] Content, string ContentType, string Extension);
