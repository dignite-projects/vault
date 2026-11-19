using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// A page without a usable text layer in an otherwise digital PDF — a scanned page — is read by the OCR engine as a
/// one-page PDF, and its elements land on that page. A PDF with no usable page is left whole to the router's OCR.
/// </summary>
public class PdfPageOcr_Tests
{
    private readonly ParseOcrClient _ocr = Substitute.ForPartsOf<ParseOcrClient>();

    private PdfExtractor CreateExtractor(int maxOcrPages = 30, bool ocrPages = true)
        => new(_ocr, Options.Create(new PdfExtractorOptions { MinImagePixels = 0, MaxOcrPagesPerPdf = maxOcrPages, OcrPagesWithoutText = ocrPages }));

    /// <summary>Page 1 carries text; page 2 only a full-page image, as a scanned page does.</summary>
    private static byte[] DigitalThenScanned()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(PdfFixtures.PageWidth, PdfFixtures.PageHeight);
        first.AddText("Digital page body text", 12, new PdfPoint(50, 700), font);
        var second = builder.AddPage(PdfFixtures.PageWidth, PdfFixtures.PageHeight);
        second.AddPng(TinyPng.CreateSolid(64, 64), new PdfRectangle(10, 10, PdfFixtures.PageWidth - 10, PdfFixtures.PageHeight - 10));
        return builder.Build();
    }

    private void StubOcr(string markdown)
        => _ocr.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => ParseTestKit.OcrDocument(call.ArgAt<string>(1), markdown));

    [Fact]
    public async Task Reads_a_scanned_page_of_a_digital_pdf_by_ocr_on_that_page()
    {
        StubOcr("Scanned page transcription");

        var result = await CreateExtractor().ParseAsync(new MemoryStream(DigitalThenScanned()), "mixed.pdf", "application/pdf");

        result.Markdown.ShouldBe("Digital page body text\n\nScanned page transcription");
        result.IsComplete.ShouldBeTrue();

        // One OCR call, for page 2 alone, as a PDF of one page; its image is not transcribed again as a figure.
        var call = _ocr.ReceivedCalls().ShouldHaveSingleItem().GetArguments();
        ((string)call[1]!).ShouldEndWith("#page-2");
        call[2].ShouldBe("application/pdf");

        var page2 = result.Document.SourcePages.Single(page => page.PageNumber == 2);
        var section = result.Document.Sections[1];
        section.Sources.ShouldHaveSingleItem().Page.ShouldBeSameAs(page2);
        section.Elements.ShouldHaveSingleItem().Sources.ShouldHaveSingleItem().Page.ShouldBeSameAs(page2);
    }

    [Fact]
    public async Task Sends_the_ocr_engine_a_one_page_copy_of_the_page()
    {
        byte[]? sent = null;
        _ocr.ExtractAsync(Arg.Do<Stream>(stream =>
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                sent = copy.ToArray();
            }), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => ParseTestKit.OcrDocument(call.ArgAt<string>(1), "x"));

        await CreateExtractor().ParseAsync(new MemoryStream(DigitalThenScanned()), "mixed.pdf", "application/pdf");

        using var page = PdfDocument.Open(sent!);
        page.NumberOfPages.ShouldBe(1);
        page.GetPage(1).GetImages().ShouldHaveSingleItem();
        page.GetPage(1).GetWords().ShouldBeEmpty();
    }

    [Fact]
    public async Task Keeps_the_text_pages_when_a_page_ocr_fails()
    {
        _ocr.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var result = await CreateExtractor().ParseAsync(new MemoryStream(DigitalThenScanned()), "mixed.pdf", "application/pdf");

        result.Markdown.ShouldBe("Digital page body text");
        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason!.ShouldContain("failed OCR");
    }

    [Fact]
    public async Task Skips_pages_past_the_ocr_page_cap_and_says_so()
    {
        StubOcr("never");

        var result = await CreateExtractor(maxOcrPages: 0).ParseAsync(new MemoryStream(DigitalThenScanned()), "mixed.pdf", "application/pdf");

        result.Markdown.ShouldBe("Digital page body text");
        result.IncompleteReason!.ShouldContain("OCR page cap");
        _ocr.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_page_ocr_a_scanned_page_reads_as_an_embedded_figure_as_before()
    {
        StubOcr("Figure transcription");

        var result = await CreateExtractor(ocrPages: false).ParseAsync(new MemoryStream(DigitalThenScanned()), "mixed.pdf", "application/pdf");

        result.Markdown.ShouldContain("Figure transcription");
        FigureMarkup.Contains(result.Markdown).ShouldBeTrue();
        ((string)_ocr.ReceivedCalls().ShouldHaveSingleItem().GetArguments()[2]!).ShouldStartWith("image/");
    }

    [Fact]
    public async Task Leaves_a_pdf_without_any_usable_page_to_the_routers_ocr()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PdfFixtures.PageWidth, PdfFixtures.PageHeight)
            .AddPng(TinyPng.CreateSolid(64, 64), new PdfRectangle(10, 10, 500, 700));

        var result = await CreateExtractor().ParseAsync(new MemoryStream(builder.Build()), "scan.pdf", "application/pdf");

        result.Document.Sections.ShouldBeEmpty();
        _ocr.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("   text", true)] // private use
    [InlineData("see (cid:123) and (cid:7)", true)]
    [InlineData("A plain English sentence of ordinary length.", false)]
    [InlineData("日本語の契約書の本文です。", false)]
    public void Judges_unmapped_characters(string text, bool garbled)
        => PdfPageText.IsUnmapped(text).ShouldBe(garbled);

    [Theory]
    [InlineData("RA SB !! QX ;; ZT ,, KW .. PL :: VN %% JR ## TB", true)]     // no word-like run, no digits
    [InlineData("The quick brown fox jumps over the lazy dog again", false)] // lowercase runs
    [InlineData("請求書 株式会社サンプル商事 御中 下記のとおり", false)]          // CJK runs
    [InlineData("INV 2026 1120 TOTAL 473000 TAX 43000 DUE 20261231", false)] // a digit-dense line is content
    [InlineData("INVOICE BILL TO SHIP TO DESCRIPTION QUANTITY UNIT PRICE AMOUNT", false)] // a form in capitals
    public void Judges_scan_noise(string text, bool noise)
        => PdfPageText.IsScanNoise(text).ShouldBe(noise);

    [Fact]
    public void A_table_of_contents_with_dot_leaders_is_not_a_broken_font()
    {
        const string font = "ABCDEF+Arial";
        var toc = string.Concat(Enumerable.Repeat("Introduction..................1\nScope.........................2\n", 3));

        PdfPageText.IsBrokenFontEncoding(toc.Where(c => !char.IsWhiteSpace(c)).Select(c => (c.ToString(), (string?)font)).ToList())
            .ShouldBeFalse();

        // Punctuation that is not laid out in runs still reads as a font mapped to the wrong codes.
        PdfPageText.IsBrokenFontEncoding("!#%&()*+,-./:;<=>?@[]^_{|}~a!#%&()*+,-./".Select(c => (c.ToString(), (string?)font)).ToList())
            .ShouldBeTrue();
    }
}
