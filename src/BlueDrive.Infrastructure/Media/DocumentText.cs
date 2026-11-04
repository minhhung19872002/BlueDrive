using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace BlueDrive.Infrastructure.Media;

/// <summary>
/// Plain text of a document for searching inside it. Office Open XML (docx, xlsx, pptx…) and OpenDocument (odt, ods, odp) are zip
/// packages of XML read directly; PDF goes through poppler's pdftotext (installed in the worker image with pdftoppm); text files are
/// read as UTF-8. Output is capped (<see cref="MaxChars"/>), and so is what is read from a package, so a crafted file cannot exhaust memory.
/// </summary>
public static partial class DocumentText
{
    public const int MaxChars = 200_000;
    /// <summary>Uncompressed bytes read from one package (zip bombs stop here).</summary>
    private const long MaxPackageBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> WordLike = ["docx", "docm", "dotx", "dotm"];
    private static readonly HashSet<string> SheetLike = ["xlsx", "xlsm", "xltx", "xltm"];
    private static readonly HashSet<string> SlideLike = ["pptx", "pptm", "potx", "ppsx"];
    private static readonly HashSet<string> OpenDocument = ["odt", "ods", "odp", "ott", "ots", "otp"];
    private static readonly HashSet<string> Plain = ["txt", "csv", "md", "json", "xml", "html", "htm", "log", "tsv"];

    /// <summary>Every extension whose text is read.</summary>
    public static IEnumerable<string> Extensions => WordLike.Concat(SheetLike).Concat(SlideLike).Concat(OpenDocument).Concat(Plain).Append("pdf");

    public static bool Supports(string? extension) =>
        extension?.ToLowerInvariant() is { } e && (WordLike.Contains(e) || SheetLike.Contains(e) || SlideLike.Contains(e) || OpenDocument.Contains(e) || Plain.Contains(e) || e == "pdf");

    /// <summary>Text of the file at <paramref name="path"/>, or null when the format is not supported or the file cannot be read.</summary>
    public static async Task<string?> ExtractAsync(string path, string extension, CancellationToken ct)
    {
        var e = extension.ToLowerInvariant();
        string? raw;
        try
        {
            raw = e switch
            {
                _ when WordLike.Contains(e) => FromPackage(path, n => n == "word/document.xml" || n.StartsWith("word/header") || n.StartsWith("word/footer")
                                                                    || n is "word/footnotes.xml" or "word/endnotes.xml", "p", "t", "tab"),
                _ when SheetLike.Contains(e) => FromPackage(path, n => n == "xl/sharedStrings.xml" || (n.StartsWith("xl/worksheets/sheet") && n.EndsWith(".xml")), "si|row", "t", null),
                _ when SlideLike.Contains(e) => FromPackage(path, n => (n.StartsWith("ppt/slides/slide") || n.StartsWith("ppt/notesSlides/notesSlide")) && n.EndsWith(".xml"), "p", "t", null),
                _ when OpenDocument.Contains(e) => FromPackage(path, n => n == "content.xml", "p|h|table-row", "p|h|span", null),
                _ when Plain.Contains(e) => await PlainAsync(path, ct),
                "pdf" => await PdfAsync(path, ct),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
        {
            return null; // damaged or not really that format: nothing to search
        }
        return raw is null ? null : Normalize(raw);
    }

    /// <summary>Collapses runs of white space, drops control characters, caps the length.</summary>
    public static string Normalize(string text)
    {
        var clean = Spaces().Replace(Controls().Replace(text, " "), " ").Trim();
        return clean.Length <= MaxChars ? clean : clean[..MaxChars];
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F]")]
    private static partial Regex Controls();

    [GeneratedRegex(@"[ \t\r\n ]+")]
    private static partial Regex Spaces();

    /// <param name="blocks">Elements (local names, "|" separated) that end with a space: paragraphs, rows.</param>
    /// <param name="texts">Elements whose text is kept.</param>
    /// <param name="tabs">Elements written as a space (Word tabs).</param>
    private static string FromPackage(string path, Func<string, bool> parts, string blocks, string texts, string? tabs)
    {
        using var zip = ZipFile.OpenRead(path);
        var blockSet = blocks.Split('|').ToHashSet();
        var textSet = texts.Split('|').ToHashSet();
        var sb = new StringBuilder();
        long budget = MaxPackageBytes;
        foreach (var entry in zip.Entries.Where(x => parts(x.FullName)).OrderBy(x => x.FullName, NaturalOrder.Instance))
        {
            if (budget <= 0 || sb.Length >= MaxChars) break;
            using var stream = new CappedStream(entry.Open(), budget);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
            var depthInText = 0;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (textSet.Contains(reader.LocalName) && !reader.IsEmptyElement) depthInText++;
                        if (tabs is not null && reader.LocalName == tabs) sb.Append(' ');
                        break;
                    case XmlNodeType.EndElement:
                        if (textSet.Contains(reader.LocalName) && depthInText > 0) depthInText--;
                        if (blockSet.Contains(reader.LocalName)) sb.Append(' ');
                        break;
                    case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA when depthInText > 0:
                        sb.Append(reader.Value);
                        break;
                }
                if (sb.Length >= MaxChars) break;
            }
            budget -= stream.Consumed;
        }
        return sb.ToString();
    }

    private static async Task<string> PlainAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars];
        var n = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return new string(buffer, 0, n);
    }

    private static async Task<string?> PdfAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pdftotext", ["-q", "-enc", "UTF-8", path, "-"])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; } // poppler not installed (development machine)
        if (p is null) return null;
        using (p)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var buffer = new char[MaxChars];
            int n;
            try
            {
                n = await p.StandardOutput.ReadBlockAsync(buffer.AsMemory(), timeout.Token);
                if (!p.HasExited) p.Kill(entireProcessTree: true); // the rest is beyond the cap
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                p.Kill(entireProcessTree: true);
                return null;
            }
            return n == 0 && p.ExitCode != 0 ? null : new string(buffer, 0, n);
        }
    }

    /// <summary>sheet2 before sheet10, slide2 before slide10.</summary>
    private sealed partial class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();
        [GeneratedRegex(@"\d+")]
        private static partial Regex Digits();
        public int Compare(string? x, string? y) =>
            string.CompareOrdinal(Digits().Replace(x ?? "", m => m.Value.PadLeft(8, '0')), Digits().Replace(y ?? "", m => m.Value.PadLeft(8, '0')));
    }

    /// <summary>Stops reading after <c>max</c> bytes (the XML reader then sees the end of the part).</summary>
    private sealed class CappedStream(Stream inner, long max) : Stream
    {
        public long Consumed { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Consumed >= max) return 0;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, max - Consumed));
            Consumed += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
