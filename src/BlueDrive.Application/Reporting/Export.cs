namespace BlueDrive.Application.Reporting;

public sealed record ExportColumn(string Header, float RelativeWidth = 1);

public sealed record TableExport(
    string Title,
    IReadOnlyList<ExportColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    string? FilterDescription,
    string ExportedBy);

public sealed record ExportedFile(byte[] Content, string ContentType, string FileName);

public interface ITableExporter
{
    /// <param name="format">"xlsx" or "pdf".</param>
    Task<ExportedFile> ExportAsync(TableExport data, string format, CancellationToken ct = default);
}

/// <summary>Reads the first worksheet of an .xlsx into rows keyed by (trimmed) header text.</summary>
public interface IExcelReader
{
    IReadOnlyList<ExcelRow> Read(Stream xlsx);
    byte[] CreateTemplate(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> sampleRows, IReadOnlyList<string>? notes = null);
}

public sealed record ExcelRow(int RowNumber, IReadOnlyDictionary<string, string> Cells)
{
    public string Get(string header) => Cells.TryGetValue(header, out var v) ? v.Trim() : "";
}

public static class ExportFormat
{
    public static string Normalize(string? format) => format?.ToLowerInvariant() switch
    {
        "pdf" => "pdf",
        _ => "xlsx",
    };
}
