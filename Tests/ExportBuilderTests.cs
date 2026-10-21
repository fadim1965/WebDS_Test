using System.Text;
using ClosedXML.Excel;
using WebDS_Test.Services.Export;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Exports leave the application as bytes and are read by a spreadsheet the code never sees, so the
/// contract is pinned here: what a CSV looks like on disk, that numbers and dates survive it, that a
/// hostile cell cannot become a formula, and that the workbook round-trips through ClosedXML.
/// </summary>
public class ExportBuilderTests
{
    private static TabularData Sample() => new(
        "Orders",
        "orders",
        new[] { "Order", "Total", "Placed (UTC)" },
        new List<object?[]>
        {
            new object?[] { "ORD-1", 54.00m, new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc) }
        });

    private static TabularData One(string header, params object?[] values) =>
        new("Sheet", "file", new[] { header }, new List<object?[]> { values });

    private static string[] CsvLines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .TrimStart('\uFEFF')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------
    // Format selection
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, ExportFormat.Csv)]
    [InlineData("", ExportFormat.Csv)]
    [InlineData("csv", ExportFormat.Csv)]
    [InlineData("CSV", ExportFormat.Csv)]
    [InlineData("excel", ExportFormat.Excel)]
    [InlineData("xlsx", ExportFormat.Excel)]
    [InlineData("  Excel ", ExportFormat.Excel)]
    [InlineData("nonsense", ExportFormat.Csv)]
    public void Format_parsing_is_lenient_and_defaults_to_csv(string? input, ExportFormat expected) =>
        Assert.Equal(expected, ExportBuilder.ParseFormat(input));

    [Fact]
    public void Build_reports_the_content_type_and_extension_of_each_format()
    {
        var csv = ExportBuilder.Build(Sample(), ExportFormat.Csv);
        Assert.Equal("text/csv", csv.ContentType);
        Assert.Equal("csv", csv.Extension);

        var excel = ExportBuilder.Build(Sample(), ExportFormat.Excel);
        Assert.Equal(ExportBuilder.ExcelContentType, excel.ContentType);
        Assert.Equal("xlsx", excel.Extension);
    }

    // ------------------------------------------------------------------
    // CSV
    // ------------------------------------------------------------------

    [Fact]
    public void Csv_is_prefixed_with_a_utf8_bom_so_excel_reads_it_as_utf8()
    {
        var bytes = ExportBuilder.ToCsv(Sample());

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    [Fact]
    public void Csv_writes_headers_then_rows_with_invariant_numbers_and_utc_dates()
    {
        var lines = CsvLines(ExportBuilder.ToCsv(Sample()));

        Assert.Equal("Order,Total,Placed (UTC)", lines[0]);
        Assert.Equal("ORD-1,54.00,2026-10-16 09:30", lines[1]);
    }

    [Fact]
    public void Csv_quotes_and_doubles_cells_containing_commas_quotes_or_newlines()
    {
        var data = new TabularData("x", "x", new[] { "Name", "Note" }, new List<object?[]>
        {
            new object?[] { "Smith, John", "He said \"hi\"\nand left" }
        });

        var lines = CsvLines(ExportBuilder.ToCsv(data));

        Assert.Equal("\"Smith, John\",\"He said \"\"hi\"\"\nand left\"", lines[1]);
    }

    [Fact]
    public void Csv_neutralises_cells_that_look_like_spreadsheet_formulas()
    {
        Assert.Equal("'=1+1", CsvLines(ExportBuilder.ToCsv(One("Name", "=1+1")))[1]);
        Assert.Equal("'+2", CsvLines(ExportBuilder.ToCsv(One("Name", "+2")))[1]);
        Assert.Equal("'-2", CsvLines(ExportBuilder.ToCsv(One("Name", "-2")))[1]);
        Assert.Equal("'@SUM(A1)", CsvLines(ExportBuilder.ToCsv(One("Name", "@SUM(A1)")))[1]);
    }

    [Fact]
    public void Csv_writes_booleans_as_yes_no_and_nulls_as_empty()
    {
        var data = new TabularData("x", "x", new[] { "A", "B", "C" }, new List<object?[]>
        {
            new object?[] { true, false, null }
        });

        Assert.Equal("Yes,No,", CsvLines(ExportBuilder.ToCsv(data))[1]);
    }

    [Fact]
    public void Csv_separates_rows_with_crlf()
    {
        var bytes = ExportBuilder.ToCsv(Sample());
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

        Assert.EndsWith("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
    }

    // ------------------------------------------------------------------
    // Excel
    // ------------------------------------------------------------------

    [Fact]
    public void Excel_round_trips_headers_and_values_as_native_types()
    {
        using var workbook = new XLWorkbook(new MemoryStream(ExportBuilder.ToExcel(Sample())));
        var sheet = workbook.Worksheets.First();

        Assert.Equal("Orders", sheet.Name);
        Assert.Equal("Order", sheet.Cell(1, 1).GetString());
        Assert.Equal("ORD-1", sheet.Cell(2, 1).GetString());
        Assert.Equal(54.00, sheet.Cell(2, 2).GetDouble());     // a number Excel can sum
        Assert.Equal(new DateTime(2026, 10, 16, 9, 30, 0), sheet.Cell(2, 3).GetDateTime());
    }

    [Fact]
    public void Excel_writes_a_report_with_headers_but_no_rows()
    {
        var data = new TabularData("Empty", "empty", new[] { "A", "B" }, new List<object?[]>());

        using var workbook = new XLWorkbook(new MemoryStream(ExportBuilder.ToExcel(data)));

        Assert.Equal("A", workbook.Worksheets.First().Cell(1, 1).GetString());
    }

    [Fact]
    public void Excel_sanitises_a_sheet_name_that_excel_would_reject()
    {
        var data = new TabularData(
            "Weekly / Orders [2026]: *draft* that is far too long",
            "x",
            new[] { "A" },
            new List<object?[]>());

        using var workbook = new XLWorkbook(new MemoryStream(ExportBuilder.ToExcel(data)));
        var name = workbook.Worksheets.First().Name;

        Assert.True(name.Length <= 31);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("[", name);
        Assert.DoesNotContain(":", name);
    }
}
