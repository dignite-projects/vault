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
/// A scanner that turns a page with /Rotate stores the raster and its OCR text layer unturned and lets the viewer turn
/// them. PdfPig reads the page box, the image and the words on the page as it is shown, but the image's box is a turned
/// rectangle there: at /Rotate 90 or 270 its own Width and Height are the shown page's height and width.
/// </summary>
public class PdfRotatedScanBackground_Tests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public async Task The_scan_of_a_turned_or_cropped_sandwich_page_is_not_read_again(int rotate, bool crop)
    {
        var ocr = ParseTestKit.Ocr("SHOULD_NOT_BE_OCRED");
        var extractor = new PdfExtractor(ocr, Options.Create(new PdfExtractorOptions { SkipFullPageScanBackground = true }));

        var result = await extractor.ParseAsync(new MemoryStream(SandwichPage(rotate, crop)), "scan.pdf", "application/pdf");

        await ocr.DidNotReceive().ExtractAsync(
            Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>());
        result.Markdown.ShouldNotContain("SHOULD_NOT_BE_OCRED");
        result.Markdown.ShouldContain("LINE00 scanned body text");
        result.Markdown.ShouldContain("LINE19 scanned body text");
        result.IsComplete.ShouldBeTrue();
    }

    /// <summary>
    /// One 600 x 800 page at <paramref name="rotate"/>, cropped to [50 100 550 700] or not: a raster over the visible
    /// page and 20 lines of invisible text over it, upright as the page is shown.
    /// </summary>
    private static byte[] SandwichPage(int rotate, bool crop)
    {
        double x0 = crop ? 50 : 0, y0 = crop ? 100 : 0, x1 = crop ? 550 : 600, y1 = crop ? 700 : 800;
        var turned = rotate is 90 or 270;
        var height = turned ? x1 - x0 : y1 - y0;

        // A point of the page as it is shown (origin bottom-left), in content space.
        (double X, double Y) ToContent(double u, double v) => rotate switch
        {
            0 => (x0 + u, y0 + v),
            90 => (x1 - v, y0 + u),
            180 => (x1 - u, y1 - v),
            _ => (x0 + v, y1 - u),
        };
        var (cos, sin) = rotate switch { 0 => (1, 0), 90 => (0, 1), 180 => (-1, 0), _ => (0, -1) };

        var content = new StringBuilder(FormattableString.Invariant(
            $"q {x1 - x0} 0 0 {y1 - y0} {x0} {y0} cm /Im1 Do Q BT 3 Tr /F1 11 Tf "));
        for (var i = 0; i < 20; i++)
        {
            var (x, y) = ToContent(30, height - 40 - (i * (height - 80) / 19));
            content.Append(FormattableString.Invariant($"{cos} {sin} {-sin} {cos} {x:0.##} {y:0.##} Tm (LINE{i:D2} scanned body text) Tj "));
        }

        content.Append("ET");

        var page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] "
            + (crop ? "/CropBox [50 100 550 700] " : string.Empty)
            + (rotate != 0 ? $"/Rotate {rotate} " : string.Empty)
            + "/Resources << /Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >> /Contents 4 0 R >>";
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii(page),
            Stream(string.Empty, Ascii(content.ToString())),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Stream("/Type /XObject /Subtype /Image /Width 300 /Height 400 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode ", Raster(300, 400))
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

        trailer.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        pdf.Write(Ascii(trailer.ToString()));
        return pdf.ToArray();
    }

    /// <summary>A grey raster with some texture, so it reads as a scan rather than a blank.</summary>
    private static byte[] Raster(int width, int height)
    {
        var image = new MemoryStream();
        using (var zlib = new ZLibStream(image, CompressionLevel.Fastest, leaveOpen: true))
        {
            var random = new Random(7);
            var row = new byte[width];
            for (var y = 0; y < height; y++)
            {
                random.NextBytes(row);
                zlib.Write(row);
            }
        }

        return image.ToArray();
    }

    private static byte[] Stream(string dictionary, byte[] data)
    {
        var result = new MemoryStream();
        result.Write(Ascii($"<< {dictionary}/Length {data.Length} >>\nstream\n"));
        result.Write(data);
        result.Write(Ascii("\nendstream"));
        return result.ToArray();
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
}
