using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DocumentExtraction;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// Unit tests for <see cref="ParseRoutingClient"/>: images go to the OCR engine, everything else to the highest-priority
/// engine that reads the file, and a PDF whose engine finds no text falls back to whole-page OCR. The engines are
/// NSubstitute stubs, so each branch is driven precisely (an empty PDF read, a priority tie) without depending on any
/// real engine.
/// </summary>
public class ParseRoutingClient_Tests
{
    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static Task<ParseResult> Parse(ParseRoutingClient router, string fileName, string mediaType, string[]? languageHints = null)
        => router.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes("payload")), fileName, mediaType, languageHints);

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".tiff")]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    [InlineData(".JPG")]  // case-insensitive
    [InlineData(".PNG")]
    public async Task Routes_image_extensions_to_the_ocr_engine(string extension)
    {
        var ocr = ParseTestKit.Ocr("image ocr");
        // The catch-all claims every extension, images included; the OCR engine outranks it.
        var catchAll = TestDoubles.Engine(ParseExtractionPriorities.Fallback, handles: extension.ToLowerInvariant());
        var router = TestDoubles.Router(catchAll, ocr);

        var result = await Parse(router, "photo" + extension, "image/jpeg");

        result.Markdown.ShouldBe("image ocr");
        await ocr.Received(1).ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
        await catchAll.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Routes_a_non_image_extension_to_its_engine_without_ocr()
    {
        var ocr = ParseTestKit.Ocr();
        var txt = TestDoubles.Engine(ParseExtractionPriorities.Default, handles: ".txt", markdown: "digital text");
        var router = TestDoubles.Router(ocr, txt);

        var result = await Parse(router, "note.txt", "text/plain");

        result.Markdown.ShouldBe("digital text");
        await ocr.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_ocr_when_a_pdf_has_no_text()
    {
        // The PDF engine returns an empty document for a scanned / image-only PDF; the router owns the single
        // whole-page OCR fallback.
        var ocr = ParseTestKit.Ocr("ocr of scanned pdf");
        var pdf = TestDoubles.Engine(ParseExtractionPriorities.Default, handles: ".pdf", markdown: string.Empty, provider: "FakePdf");
        var router = TestDoubles.Router(ocr, pdf);

        var result = await Parse(router, "scan.pdf", "application/pdf");

        result.Markdown.ShouldBe("ocr of scanned pdf");
        result.ProviderName.ShouldBe("FakeOcr");
        await ocr.Received(1).ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_fall_back_to_ocr_when_a_pdf_has_text()
    {
        var ocr = ParseTestKit.Ocr();
        var pdf = TestDoubles.Engine(ParseExtractionPriorities.Default, handles: ".pdf", markdown: "# Real digital PDF text");
        var router = TestDoubles.Router(ocr, pdf);

        var result = await Parse(router, "digital.pdf", "application/pdf");

        result.Markdown.ShouldBe("# Real digital PDF text");
        await ocr.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("   \n\n  ")]      // whitespace only
    [InlineData("--- \n\n ## ")]   // punctuation / markup only, no letter or digit
    public async Task Treats_a_pdf_read_without_letters_or_digits_as_no_text(string emptyish)
    {
        var ocr = ParseTestKit.Ocr("ocr fallback");
        var pdf = TestDoubles.Engine(ParseExtractionPriorities.Default, handles: ".pdf", markdown: emptyish);
        var router = TestDoubles.Router(ocr, pdf);

        var result = await Parse(router, "scan.pdf", "application/pdf");

        result.Markdown.ShouldBe("ocr fallback");
    }

    [Fact]
    public async Task Does_not_fall_back_to_ocr_for_an_empty_non_pdf()
    {
        // The OCR fallback is PDF-only: an empty read of any other file is passed through untouched.
        var ocr = ParseTestKit.Ocr();
        var txt = TestDoubles.Engine(ParseExtractionPriorities.Fallback, handles: ".txt", markdown: string.Empty);
        var router = TestDoubles.Router(ocr, txt);

        var result = await Parse(router, "empty.txt", "text/plain");

        result.Markdown.ShouldBe(string.Empty);
        await ocr.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Passes_language_hints_and_media_type_to_the_ocr_engine()
    {
        var ocr = ParseTestKit.Ocr();
        var router = TestDoubles.Router(ocr);

        await Parse(router, "photo.jpg", "image/jpeg", languageHints: new[] { "fr", "de" });

        await ocr.Received(1).ExtractAsync(
            Arg.Any<Stream>(),
            Arg.Any<string>(),
            "image/jpeg",
            Arg.Is<DocumentExtractionOptions?>(o => ParseTestKit.HasLanguageHints(o, new[] { "fr", "de" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Passes_no_language_hints_when_there_are_none()
    {
        // #441: there is no central default. With no per-document hints the engine gets none; an engine that needs a
        // default reads its own configuration (PaddleOcr:Languages, proven by PaddleOcrClient_Tests).
        var ocr = ParseTestKit.Ocr();
        var router = TestDoubles.Router(ocr);

        await Parse(router, "photo.jpg", "image/jpeg");

        await ocr.Received(1).ExtractAsync(
            Arg.Any<Stream>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<DocumentExtractionOptions?>(o => ParseTestKit.HasLanguageHints(o, Array.Empty<string>())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_the_ocr_engine_metadata()
    {
        var ocr = ParseTestKit.Ocr("ocr md", incompleteReason: "truncated", language: "ja");
        var router = TestDoubles.Router(ocr);

        var result = await Parse(router, "receipt.png", "image/png");

        result.DetectedLanguage.ShouldBe("ja");
        result.ProviderName.ShouldBe("FakeOcr");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldBe("truncated");
    }

    [Fact]
    public async Task Names_the_engine_when_the_engine_does_not()
    {
        var engine = Substitute.For<ParseExtractionClient>();
        engine.ProviderName.Returns("Silent");
        engine.CanExtract(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        engine.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => MarkdownDocumentReader.Read("text", call.ArgAt<string>(1)));

        var result = await Parse(TestDoubles.Router(engine), "note.txt", "text/plain");

        result.ProviderName.ShouldBe("Silent");
    }

    [Fact]
    public async Task Selects_the_highest_priority_engine_that_reads_the_file()
    {
        var high = TestDoubles.Engine(10, ".docx", "HIGH");
        var low = TestDoubles.Engine(1, ".docx", "LOW");
        var router = TestDoubles.Router(ParseTestKit.Ocr(), low, high);

        var result = await Parse(router, "letter.docx", Docx);

        result.Markdown.ShouldBe("HIGH");
        await low.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ignores_a_higher_priority_engine_that_does_not_read_the_file()
    {
        var higherButUnrelated = TestDoubles.Engine(99, ".pdf", "PDF");
        var matching = TestDoubles.Engine(1, ".docx", "DOCX");
        var router = TestDoubles.Router(ParseTestKit.Ocr(), higherButUnrelated, matching);

        var result = await Parse(router, "letter.docx", Docx);

        result.Markdown.ShouldBe("DOCX");
        await higherButUnrelated.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_the_first_engine_when_two_tie_on_priority()
    {
        // A same-file, same-priority clash is a composition mistake; selection must still be deterministic.
        var first = TestDoubles.Engine(5, ".docx", "FIRST");
        var second = TestDoubles.Engine(5, ".docx", "SECOND");
        var router = TestDoubles.Router(ParseTestKit.Ocr(), first, second);

        var result = await Parse(router, "letter.docx", Docx);

        result.Markdown.ShouldBe("FIRST");
        await second.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Throws_when_no_engine_reads_the_file()
    {
        // The host always installs the catch-all engine, so this is a composition error, not a document's.
        var router = TestDoubles.Router(ParseTestKit.Ocr(), TestDoubles.Engine(0, ".pdf"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Parse(router, "letter.docx", Docx));

        ex.Message.ShouldContain(".docx");
        ex.Message.ShouldContain("catch-all");
    }

    [Fact]
    public async Task Gives_the_engine_a_seekable_copy_of_a_forward_only_stream()
    {
        bool? seekable = null;
        var engine = TestDoubles.Engine(0, ".txt");
        _ = engine.ExtractAsync(Arg.Do<Stream>(s => seekable = s.CanSeek), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());

        await TestDoubles.Router(engine).ParseAsync(new ForwardOnlyStream(Encoding.UTF8.GetBytes("abc")), "note.txt", "text/plain");

        // Read inside the call: the router disposes its copy afterwards.
        seekable.ShouldBe(true);
    }

    private sealed class ForwardOnlyStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
