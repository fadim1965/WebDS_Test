namespace WebDS_Test.Services.Export;

/// <summary>One row of a CSV file, with the line number it came from so errors can be found.</summary>
public sealed record CsvRow(int LineNumber, IReadOnlyList<string> Fields);

/// <summary>
/// A minimal, correct RFC 4180 reader — the exact inverse of what <see cref="ExportBuilder"/> writes,
/// so a file the store exports can be edited in a spreadsheet and handed straight back.
/// </summary>
/// <remarks>
/// Written by hand rather than pulled from a package because this machine has no route to nuget.org,
/// and because the format is small: quoted fields, doubled quotes inside them, embedded newlines and
/// commas, and both LF and CRLF line endings. A UTF-8 BOM is skipped so a file saved by Excel — which
/// always writes one — does not arrive with a stray character glued to the first header.
/// </remarks>
public static class CsvReader
{
    /// <summary>
    /// Every record in the document, in order. The header is row 1 and is returned like any other;
    /// the caller decides what to do with it.
    /// </summary>
    /// <exception cref="ArgumentException">A quoted field is never closed.</exception>
    public static IReadOnlyList<CsvRow> ReadRows(string content)
    {
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        var rows = new List<CsvRow>();
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var line = 1;
        var rowStartLine = 1;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is a literal quote.
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    // Swallow CR; the LF that follows (if any) ends the record.
                    break;

                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add(new CsvRow(rowStartLine, fields));
                    fields = new List<string>();
                    line++;
                    rowStartLine = line;
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        if (inQuotes)
        {
            throw new ArgumentException($"The file ends inside a quoted value that started on line {rowStartLine}.");
        }

        // A trailing newline must not produce a phantom final row: only flush if content remains.
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add(new CsvRow(rowStartLine, fields));
        }

        return rows;
    }

    /// <summary>Maps header names to their column index, so column order in the file cannot matter.</summary>
    public static Dictionary<string, int> MapHeaders(IEnumerable<string> headers)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var header in headers)
        {
            var key = header.Trim().Trim('\uFEFF').Trim();
            if (key.Length > 0 && !map.ContainsKey(key))
            {
                map[key] = index;
            }

            index++;
        }

        return map;
    }
}
