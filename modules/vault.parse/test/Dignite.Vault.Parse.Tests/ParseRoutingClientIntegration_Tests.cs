using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dignite.Vault.Parse.ElBrunoMarkItDown;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// The router as the host composes it: resolved as <see cref="DocumentExtractionClient"/>, with the real ElBruno catch-all
/// engine and a substitute OCR engine.
/// </summary>
public class ParseRoutingClientIntegration_Tests : AbpIntegratedTest<ParseRoutingClientIntegration_Tests.ParseTestModule>
{
    private readonly DocumentExtractionClient _extractor;
    private readonly ParseOcrClient _ocrProvider;

    public ParseRoutingClientIntegration_Tests()
    {
        _extractor = GetRequiredService<DocumentExtractionClient>();
        _ocrProvider = GetRequiredService<ParseOcrClient>();
    }

    [Fact]
    public async Task Should_Use_OcrProvider_For_Image_Files()
    {
        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8 }); // fake JPEG bytes

        var result = await _extractor.ParseAsync(stream, "photo.jpg", "image/jpeg");

        result.Markdown.ShouldBe("fake ocr markdown");

        // The router calls the OCR engine once; the concrete model is the engine's / host's configuration.
        await _ocrProvider.Received(1).ExtractAsync(
            Arg.Any<Stream>(),
            Arg.Any<string>(),
            "image/jpeg",
            // #441: no central host default; with no per-document hints the engine gets none.
            Arg.Is<DocumentExtractionOptions?>(o => ParseTestKit.HasLanguageHints(o, System.Array.Empty<string>())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Use_Markdown_Provider_For_Txt_Files()
    {
        var content = "Hello World";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var result = await _extractor.ParseAsync(stream, "note.txt", "text/plain");

        result.Markdown.ShouldContain("Hello World");
        await _ocrProvider.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(".csv", "text/csv", "Name,City\nAlice,Tokyo", "Alice", "Tokyo")]
    [InlineData(".tsv", "text/tab-separated-values", "Name\tCity\nAlice\tTokyo", "Alice", "Tokyo")]
    public async Task Should_Convert_Delimited_Files_To_Markdown_Tables(
        string extension,
        string contentType,
        string content,
        string expectedName,
        string expectedCity)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var result = await _extractor.ParseAsync(stream, "table" + extension, contentType);

        result.Markdown.ShouldContain("|");
        result.Markdown.ShouldContain(expectedName);
        result.Markdown.ShouldContain(expectedCity);
        await _ocrProvider.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Convert_All_NonEmpty_Xlsx_Worksheets_To_Markdown()
    {
        using var workbook = new XLWorkbook();
        var people = workbook.AddWorksheet("People");
        people.Cell("A1").Value = "Name";
        people.Cell("B1").Value = "City";
        people.Cell("A2").Value = "Alice";
        people.Cell("B2").Value = "Tokyo";

        var totals = workbook.AddWorksheet("Totals");
        totals.Cell("A1").Value = "Count";
        totals.Cell("A2").Value = 1;
        workbook.AddWorksheet("Empty");

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var result = await _extractor.ParseAsync(
            stream, "book.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        result.Markdown.ShouldContain("People");
        result.Markdown.ShouldContain("Alice");
        result.Markdown.ShouldContain("Tokyo");
        result.Markdown.ShouldContain("Totals");
        result.Markdown.ShouldContain("Count");
        await _ocrProvider.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
        result.ProviderName.ShouldBe(ElBrunoExtractionClient.ProviderIdentifier);
    }

    [Fact]
    public async Task Should_Fail_Explicitly_For_Malformed_Xlsx()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("not an xlsx package"));
        await Should.ThrowAsync<InvalidDataException>(() => _extractor.ParseAsync(
            stream, "book.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
    }

    [Fact]
    public async Task Xlsx_Preflight_Should_Reject_Expanded_Content_Over_The_Budget()
    {
        using var stream = BuildZip(("xl/sharedStrings.xml", new string('x', 512)));
        var limits = TestXlsxLimits(maxExpandedArchiveBytes: 128);

        var exception = await Should.ThrowAsync<InvalidDataException>(
            () => XlsxSafetyGuard.ValidatePackageAsync(stream, limits));

        exception.Message.ShouldContain("expanded content exceeds");
    }

    [Fact]
    public async Task Xlsx_Preflight_Should_Reject_Cell_Count_Over_The_Budget()
    {
        const string worksheet =
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetData><row><c/><c/><c/></row></sheetData></worksheet>";
        using var stream = BuildZip(("xl/worksheets/sheet1.xml", worksheet));
        var limits = TestXlsxLimits(maxCells: 2);

        var exception = await Should.ThrowAsync<InvalidDataException>(
            () => XlsxSafetyGuard.ValidatePackageAsync(stream, limits));

        exception.Message.ShouldContain("more than 2 cells");
    }

    [Fact]
    public void Xlsx_Output_Should_Reject_Markdown_Over_The_Budget()
    {
        var limits = TestXlsxLimits(maxMarkdownCharacters: 4);

        var exception = Should.Throw<InvalidDataException>(
            () => XlsxSafetyGuard.ValidateMarkdown("12345", limits));

        exception.Message.ShouldContain("5 characters");
    }

    [Fact]
    public async Task Should_Preserve_Markdown_For_Md_Files()
    {
        var content = "# Title\n\nSome content.";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var result = await _extractor.ParseAsync(stream, "readme.md", "text/markdown");

        await _ocrProvider.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldNotBeNullOrEmpty();
        result.Markdown.ShouldContain("# Title");
        result.Markdown.ShouldContain("Some content");
    }

    [Fact]
    public async Task Should_Fallback_To_Ocr_For_Scanned_Pdf()
    {
        // Non-real PDF bytes make ElBruno conversion fail, so it falls back to OCR.
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("not a real pdf"));

        var result = await _extractor.ParseAsync(stream, "scan.pdf", "application/pdf");

        result.Markdown.ShouldBe("fake ocr markdown");
    }

    [Fact]
    public async Task Should_Not_Retry_Ocr_For_Image()
    {
        _ocrProvider.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => ParseTestKit.OcrDocument(call.ArgAt<string>(1), "| A | B |\n|---|---|\n| kept | table |"));

        var stream = new MemoryStream(new byte[] { 0xFF, 0xD8 });

        var result = await _extractor.ParseAsync(stream, "photo.jpg", "image/jpeg");

        result.Markdown.ShouldContain("kept");

        // The router does not retry OCR; the engine is called only once.
        await _ocrProvider.Received(1).ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    // === #210 provenance: the engine is named on the document ===

    [Fact]
    public async Task Image_path_names_the_ocr_engine()
    {
        var result = await _extractor.ParseAsync(new MemoryStream(new byte[] { 0xFF, 0xD8 }), "photo.jpg", "image/jpeg");

        result.ProviderName.ShouldBe("FakeOcr");
    }

    [Fact]
    public async Task Digital_path_names_the_catch_all_engine()
    {
        var result = await _extractor.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("# Title\n\nbody text")), "readme.md", "text/markdown");

        result.ProviderName.ShouldBe(ElBrunoExtractionClient.ProviderIdentifier);
    }

    [Fact]
    public async Task Scanned_pdf_fallback_names_the_ocr_engine()
    {
        // Non-real PDF bytes make ElBruno conversion fail with no text layer, so it falls back to OCR.
        var result = await _extractor.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("not a real pdf")), "scan.pdf", "application/pdf");

        result.ProviderName.ShouldBe("FakeOcr");
    }

    [DependsOn(
        typeof(VaultParseModule),
        typeof(VaultParseElBrunoMarkItDownModule))]
    public class ParseTestModule : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            // The OCR engine owns the configured model and its output; the router does not retry it.
            var fakeOcr = ParseTestKit.Ocr("fake ocr markdown");
            context.Services.AddSingleton(fakeOcr);
            context.Services.AddSingleton<ParseExtractionClient>(fakeOcr);
        }
    }

    private static MemoryStream BuildZip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(
                    entry.Open(), Encoding.UTF8, bufferSize: 1_024, leaveOpen: false);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static XlsxSafetyLimits TestXlsxLimits(
        long maxExpandedArchiveBytes = 10_000,
        long maxCells = 100,
        int maxMarkdownCharacters = 10_000)
        => new(
            MaxCompressedBytes: 10_000,
            MaxExpandedArchiveBytes: maxExpandedArchiveBytes,
            MaxArchiveEntries: 10,
            MaxWorksheets: 5,
            MaxCells: maxCells,
            MaxMarkdownCharacters: maxMarkdownCharacters);
}
