using System.IO.Compression;
using System.Text;
using BlueDrive.Infrastructure.Media;
using FluentAssertions;

namespace BlueDrive.UnitTests.Search;

/// <summary>Text read from documents for searching inside them.</summary>
public class DocumentTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bd-doctext-" + Guid.NewGuid().ToString("N"));

    public DocumentTextTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Package(string name, params (string Part, string Xml)[] parts)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (part, xml) in parts)
        {
            using var w = new StreamWriter(zip.CreateEntry(part).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        return path;
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";

    [Fact]
    public async Task Word_paragraphs_headers_and_tables_are_read_with_spaces_between_them()
    {
        var docx = Package("bien-ban.docx",
            ("word/document.xml", $"<w:document {W}><w:body><w:p><w:r><w:t>BIÊN BẢN</w:t></w:r><w:r><w:t xml:space=\"preserve\"> BÀN GIAO</w:t></w:r></w:p>" +
                                  "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Bên</w:t></w:r><w:r><w:tab/><w:t>A</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" +
                                  "<w:p><w:r><w:t>Trung tâm Dự báo</w:t></w:r></w:p></w:body></w:document>"),
            ("word/header1.xml", $"<w:hdr {W}><w:p><w:r><w:t>Cộng hòa xã hội chủ nghĩa Việt Nam</w:t></w:r></w:p></w:hdr>"),
            ("word/styles.xml", $"<w:styles {W}><w:style><w:name w:val=\"Không phải nội dung\"/></w:style></w:styles>"));
        var text = await DocumentText.ExtractAsync(docx, "docx", default);
        text.Should().Be("BIÊN BẢN BÀN GIAO Bên A Trung tâm Dự báo Cộng hòa xã hội chủ nghĩa Việt Nam");
    }

    [Fact]
    public async Task Excel_cells_are_read_from_a_real_workbook()
    {
        var path = Path.Combine(_dir, "ke-hoach.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.AddWorksheet("Kế hoạch");
            ws.Cell(1, 1).Value = "Hạng mục";
            ws.Cell(2, 1).Value = "Tờ rơi dân số";
            ws.Cell(2, 2).Value = 1500;
            wb.AddWorksheet("Phụ lục").Cell(1, 1).Value = "Ngân sách Quảng Trị";
            wb.SaveAs(path);
        }
        var text = await DocumentText.ExtractAsync(path, "xlsx", default);
        text.Should().Contain("Hạng mục").And.Contain("Tờ rơi dân số").And.Contain("Ngân sách Quảng Trị");
    }

    [Fact]
    public async Task PowerPoint_slides_come_in_slide_order_and_open_document_text_is_read()
    {
        var pptx = Package("trinh-chieu.pptx",
            ("ppt/slides/slide10.xml", $"<p:sld {A} xmlns:p=\"p\"><a:p><a:r><a:t>Mười</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide2.xml", $"<p:sld {A} xmlns:p=\"p\"><a:p><a:r><a:t>Hai</a:t></a:r></a:p></p:sld>"));
        (await DocumentText.ExtractAsync(pptx, "pptx", default)).Should().Be("Hai Mười");

        var odt = Package("van-ban.odt", ("content.xml",
            "<office:document-content xmlns:office=\"o\" xmlns:text=\"t\"><office:body><office:text>" +
            "<text:h>Tiêu đề</text:h><text:p>Nội dung <text:span>có định dạng</text:span></text:p></office:text></office:body></office:document-content>"));
        (await DocumentText.ExtractAsync(odt, "odt", default)).Should().Be("Tiêu đề Nội dung có định dạng");
    }

    [Fact]
    public async Task Text_files_and_damaged_or_unknown_files()
    {
        var txt = Path.Combine(_dir, "ghi-chu.txt");
        await File.WriteAllTextAsync(txt, "dòng 1\r\n\r\n\tdòng\u0001 2  ");
        (await DocumentText.ExtractAsync(txt, "TXT", default)).Should().Be("dòng 1 dòng 2");

        var broken = Path.Combine(_dir, "hong.docx");
        await File.WriteAllTextAsync(broken, "không phải zip");
        (await DocumentText.ExtractAsync(broken, "docx", default)).Should().BeNull();
        (await DocumentText.ExtractAsync(txt, "mp4", default)).Should().BeNull();
        DocumentText.Supports("PDF").Should().BeTrue();
        DocumentText.Supports("exe").Should().BeFalse();
    }

    [Fact]
    public async Task Output_is_capped_however_large_the_document()
    {
        var huge = string.Concat(Enumerable.Repeat("<w:p><w:r><w:t>dân số kế hoạch hóa gia đình</w:t></w:r></w:p>", 20_000));
        var docx = Package("dai.docx", ("word/document.xml", $"<w:document {W}><w:body>{huge}</w:body></w:document>"));
        (await DocumentText.ExtractAsync(docx, "docx", default))!.Length.Should().BeLessThanOrEqualTo(DocumentText.MaxChars);
    }
}
