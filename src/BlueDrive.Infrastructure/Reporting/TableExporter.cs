using BlueDrive.Application.Abstractions;
using BlueDrive.Application.Reporting;
using BlueDrive.Application.Settings;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BlueDrive.Infrastructure.Reporting;

/// <summary>Excel (ClosedXML) and PDF (QuestPDF, embedded Be Vietnam Pro font) tabular exports.</summary>
public sealed class TableExporter(ISettingsService settings, TimeProvider clock) : ITableExporter
{
    public const string FontFamily = "Be Vietnam Pro";
    private static readonly TimeZoneInfo VnTz = VietnamTime.Zone;

    static TableExporter() => PdfFonts.EnsureRegistered();

    public async Task<ExportedFile> ExportAsync(TableExport data, string format, CancellationToken ct = default)
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, VnTz);
        var baseName = $"{Slug(data.Title)}_{now:yyyyMMdd_HHmm}";
        if (ExportFormat.Normalize(format) == "pdf")
        {
            var header = new PdfHeader(
                await settings.GetAsync(SettingKeys.ReportShowNationalHeader, true, ct),
                await settings.GetAsync(SettingKeys.ReportGoverningBody, "CỤC DÂN SỐ - BỘ Y TẾ", ct),
                await settings.GetAsync(SettingKeys.BrandingOrgName, "Trung tâm Thông tin, Chuyển đổi số và Dịch vụ dân số", ct));
            return new ExportedFile(BuildPdf(data, now, header), "application/pdf", baseName + ".pdf");
        }
        return new ExportedFile(BuildExcel(data, now), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", baseName + ".xlsx");
    }

    private static byte[] BuildExcel(TableExport data, DateTime now)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Dữ liệu");
        var cols = data.Columns.Count + 1;
        ws.Cell(1, 1).Value = data.Title.ToUpperInvariant();
        ws.Range(1, 1, 1, cols).Merge().Style.Font.SetBold().Font.SetFontSize(14).Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        ws.Cell(2, 1).Value = $"Ngày xuất: {now:dd/MM/yyyy HH:mm} - Người xuất: {data.ExportedBy}";
        ws.Range(2, 1, 2, cols).Merge().Style.Font.SetItalic();
        var row = 3;
        if (!string.IsNullOrWhiteSpace(data.FilterDescription))
        {
            ws.Cell(row, 1).Value = $"Điều kiện lọc: {data.FilterDescription}";
            ws.Range(row, 1, row, cols).Merge().Style.Font.SetItalic();
            row++;
        }
        row++;
        var headerRow = row;
        ws.Cell(row, 1).Value = "STT";
        for (var i = 0; i < data.Columns.Count; i++) ws.Cell(row, i + 2).Value = data.Columns[i].Header;
        var headerRange = ws.Range(row, 1, row, cols);
        headerRange.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DCE8F5"))
            .Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin)
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Alignment.SetWrapText();
        for (var r = 0; r < data.Rows.Count; r++)
        {
            row++;
            ws.Cell(row, 1).Value = r + 1;
            for (var c = 0; c < data.Columns.Count; c++)
                ws.Cell(row, c + 2).Value = c < data.Rows[r].Count ? data.Rows[r][c] ?? "" : "";
        }
        if (data.Rows.Count > 0)
            ws.Range(headerRow + 1, 1, row, cols).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin);
        ws.Cell(row + 2, 1).Value = $"Tổng số: {data.Rows.Count} bản ghi";
        ws.Columns(1, cols).AdjustToContents(headerRow, row, 8, 60);
        ws.SheetView.FreezeRows(headerRow);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private sealed record PdfHeader(bool ShowNational, string GoverningBody, string OrgName);

    private static byte[] BuildPdf(TableExport data, DateTime now, PdfHeader h)
    {
        var landscape = data.Columns.Count > 5;
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontFamily(FontFamily).FontSize(9));
            page.Header().Column(col =>
            {
                col.Item().Row(r =>
                {
                    r.RelativeItem().AlignCenter().Column(c =>
                    {
                        c.Item().AlignCenter().Text(h.GoverningBody).FontSize(10);
                        c.Item().AlignCenter().Text(h.OrgName.ToUpperInvariant()).Bold().FontSize(10);
                    });
                    if (h.ShowNational)
                        r.RelativeItem().AlignCenter().Column(c =>
                        {
                            c.Item().AlignCenter().Text("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM").Bold().FontSize(10);
                            c.Item().AlignCenter().Text("Độc lập - Tự do - Hạnh phúc").Bold().FontSize(10);
                        });
                });
                col.Item().PaddingTop(12).AlignCenter().Text(data.Title.ToUpperInvariant()).Bold().FontSize(14);
                col.Item().AlignCenter().Text($"Ngày xuất: {now:dd/MM/yyyy HH:mm} - Người xuất: {data.ExportedBy}").Italic();
                if (!string.IsNullOrWhiteSpace(data.FilterDescription))
                    col.Item().AlignCenter().Text($"Điều kiện lọc: {data.FilterDescription}").Italic();
                col.Item().PaddingBottom(6);
            });
            page.Content().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(30);
                    foreach (var col in data.Columns) c.RelativeColumn(col.RelativeWidth);
                });
                table.Header(hd =>
                {
                    hd.Cell().Element(HeaderCell).Text("STT").Bold();
                    foreach (var col in data.Columns) hd.Cell().Element(HeaderCell).Text(col.Header).Bold();
                });
                for (var r = 0; r < data.Rows.Count; r++)
                {
                    table.Cell().Element(BodyCell).AlignCenter().Text((r + 1).ToString());
                    for (var c = 0; c < data.Columns.Count; c++)
                        table.Cell().Element(BodyCell).Text(c < data.Rows[r].Count ? data.Rows[r][c] ?? "" : "");
                }
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"Tổng số: {data.Rows.Count} bản ghi");
                r.RelativeItem().AlignRight().Text(t =>
                {
                    t.Span("Trang ");
                    t.CurrentPageNumber();
                    t.Span("/");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();

        static IContainer HeaderCell(IContainer c) => c.Border(0.5f).Background("#DCE8F5").Padding(3).AlignCenter().AlignMiddle();
        static IContainer BodyCell(IContainer c) => c.Border(0.5f).Padding(3);
    }

    private static string Slug(string s)
    {
        var u = Application.Common.TextUtil.Unaccent(s);
        var chars = u.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return new string(chars).Trim('_');
    }
}

public static class PdfFonts
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        var asm = typeof(PdfFonts).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            QuestPDF.Drawing.FontManager.RegisterFontFromStream(s);
        }
    }
}

public static class VietnamTime
{
    public static readonly TimeZoneInfo Zone = FindZone();

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "Giờ Việt Nam", "Giờ Việt Nam");
    }

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    public static string Format(DateTime? utc) => utc is null ? "" : ToLocal(utc.Value).ToString("dd/MM/yyyy HH:mm");
}
