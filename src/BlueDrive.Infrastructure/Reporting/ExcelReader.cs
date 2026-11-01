using BlueDrive.Application.Common;
using BlueDrive.Application.Reporting;
using ClosedXML.Excel;

namespace BlueDrive.Infrastructure.Reporting;

public sealed class ExcelReader : IExcelReader
{
    public IReadOnlyList<ExcelRow> Read(Stream xlsx)
    {
        XLWorkbook wb;
        try { wb = new XLWorkbook(xlsx); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new AppException("Tệp Excel không hợp lệ hoặc bị hỏng. Vui lòng dùng đúng tệp mẫu (.xlsx).");
        }
        using (wb)
        {
            var ws = wb.Worksheets.First();
            var used = ws.RangeUsed();
            if (used is null) return [];
            // Header row = first row whose first cell is non-empty and not a note line.
            var headerRow = used.FirstRow().RowNumber();
            var headers = new Dictionary<int, string>();
            foreach (var cell in ws.Row(headerRow).CellsUsed())
                headers[cell.Address.ColumnNumber] = cell.GetString().Trim().TrimEnd('*').Trim();
            var rows = new List<ExcelRow>();
            for (var r = headerRow + 1; r <= used.LastRow().RowNumber(); r++)
            {
                var row = ws.Row(r);
                if (row.IsEmpty()) continue;
                var cells = headers.ToDictionary(h => h.Value, h => row.Cell(h.Key).GetFormattedString(), StringComparer.OrdinalIgnoreCase);
                if (cells.Values.All(string.IsNullOrWhiteSpace)) continue;
                rows.Add(new ExcelRow(r, cells));
            }
            return rows;
        }
    }

    public byte[] CreateTemplate(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> sampleRows, IReadOnlyList<string>? notes = null)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);
        for (var i = 0; i < headers.Count; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DCE8F5")).Border.SetOutsideBorder(XLBorderStyleValues.Thin);
        }
        for (var r = 0; r < sampleRows.Count; r++)
            for (var c = 0; c < sampleRows[r].Count; c++)
            {
                var cell = ws.Cell(r + 2, c + 1);
                cell.Style.NumberFormat.Format = "@";
                cell.Value = sampleRows[r][c];
            }
        ws.Columns(1, headers.Count).Width = 24;
        if (notes is { Count: > 0 })
        {
            var guide = wb.Worksheets.Add("Hướng dẫn");
            for (var i = 0; i < notes.Count; i++) guide.Cell(i + 1, 1).Value = notes[i];
            guide.Column(1).Width = 120;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
