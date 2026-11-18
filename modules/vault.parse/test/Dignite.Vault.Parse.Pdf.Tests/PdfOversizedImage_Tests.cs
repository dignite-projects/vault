using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
/// A raster's decoded size is its width × height × components, whatever its compressed stream weighs: a bitmap of one
/// colour compresses a thousandfold. An image whose pixel count is over the cap is skipped before it is decoded.
/// </summary>
public class PdfOversizedImage_Tests
{
    [Fact]
    public async Task An_image_over_the_pixel_cap_is_skipped_without_being_decoded()
    {
        var ocr = ParseTestKit.Ocr("figure text");
        var extractor = new PdfExtractor(ocr, Options.Create(new PdfExtractorOptions { SkipFullPageScanBackground = false }));
        var pdf = PageWithImage(12_000, 12_000);

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var result = await extractor.ParseAsync(new MemoryStream(pdf), "bomb.pdf", "application/pdf");
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        result.Markdown.ShouldContain("Digital page body text");
        allocated.ShouldBeLessThan(64L * 1024 * 1024);
        result.IsComplete.ShouldBeFalse();
        await ocr.DidNotReceive().ExtractAsync(
            Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>One page of text in Helvetica, with a grey <paramref name="width"/> × <paramref name="height"/> raster drawn small beside it.</summary>
    private static byte[] PageWithImage(int width, int height)
    {
        var image = new MemoryStream();
        using (var zlib = new ZLibStream(image, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width];
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        var content = "BT /F1 12 Tf 50 700 Td (Digital page body text) Tj ET q 100 0 0 100 50 400 cm /Im1 Do Q";
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >> /Contents 4 0 R >>"),
            Stream(string.Empty, Ascii(content)),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Stream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode ", image.ToArray())
        };

        var pdf = new MemoryStream();
        pdf.Write(Ascii("%PDF-1.4\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            pdf.Write(Ascii($"{i + 1} 0 obj\n"));
            pdf.Write(objects[i]);
            pdf.Write(Ascii("\nendobj\n"));
        }

        var xref = pdf.Position;
        var trailer = new StringBuilder("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            trailer.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        trailer.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        pdf.Write(Ascii(trailer.ToString()));
        return pdf.ToArray();

        static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        static byte[] Stream(string dictionary, byte[] data)
        {
            var stream = new MemoryStream();
            stream.Write(Ascii($"<< {dictionary}/Length {data.Length} >>\nstream\n"));
            stream.Write(data);
            stream.Write(Ascii("\nendstream"));
            return stream.ToArray();
        }
    }
}
