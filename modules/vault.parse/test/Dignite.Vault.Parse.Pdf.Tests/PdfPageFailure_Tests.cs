using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// A page PdfPig cannot read fails that page, not the file: PdfPig runs a page's content stream when the page is taken,
/// so the failure surfaces there, and the other pages still read.
/// </summary>
public class PdfPageFailure_Tests
{
    private readonly ParseOcrClient _ocr = Substitute.ForPartsOf<ParseOcrClient>();

    private PdfExtractor CreateExtractor(bool ocrPages)
        => new(_ocr, Options.Create(new PdfExtractorOptions { MinImagePixels = 0, OcrPagesWithoutText = ocrPages }));

    [Fact]
    public async Task A_page_whose_content_stream_cannot_be_read_fails_alone()
    {
        var result = await CreateExtractor(ocrPages: false).ParseAsync(new MemoryStream(TwoPagesSecondBroken()), "broken.pdf", "application/pdf");

        result.Markdown.ShouldContain("Digital page body text");
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task With_page_ocr_a_page_that_cannot_be_copied_either_fails_alone()
    {
        _ocr.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => ParseTestKit.OcrDocument(call.ArgAt<string>(1), "Recovered by OCR"));

        // Copying the page into a one-page PDF for the OCR engine runs the same content stream, so it fails too.
        var result = await CreateExtractor(ocrPages: true).ParseAsync(new MemoryStream(TwoPagesSecondBroken()), "broken.pdf", "application/pdf");

        result.Markdown.ShouldBe("Digital page body text");
        result.IsComplete.ShouldBeFalse();
        result.Document.SourcePages.Select(page => page.PageNumber).ShouldBe([1]);
    }

    /// <summary>
    /// Two pages written by hand: the first shows text in Helvetica, the second shows text in a font its resources do
    /// not define, which PdfPig refuses when it runs the page's content stream.
    /// </summary>
    private static byte[] TwoPagesSecondBroken()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            Stream("BT /F1 12 Tf 50 700 Td (Digital page body text) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 6 0 R >>",
            Stream("BT /F9 12 Tf 50 700 Td (Broken page) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());

        static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
    }
}
