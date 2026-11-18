using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Options;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace Dignite.Vault.Parse.Ocr.VisionLlm;

// The only tests here that load PDFium: the rasterizer's resolution is what keeps a large page within the engine's
// input cap, and only a real render shows the size it produces.
public class PdfToImageRasterizer_Tests
{
    [Fact]
    public void A_page_is_rendered_at_300_dpi()
    {
        var rasterizer = new PdfToImageRasterizer(Options.Create(new VisionLlmOcrOptions()));

        var png = rasterizer.RenderPageToPng(OnePage(612, 792), 0);

        // PDFtoImage truncates the scaled side, so 792 pt can come out a pixel short of 3300.
        var (width, height) = SizeOf(png);
        width.ShouldBeInRange(2549, 2550);
        height.ShouldBeInRange(3299, 3300);
    }

    [Fact]
    public void A_page_too_large_at_300_dpi_is_rendered_within_the_input_cap()
    {
        // A US Letter page is 8.4 megapixels at 300 dpi; an A0 drawing, 139, is over the default cap the same way.
        var options = new VisionLlmOcrOptions { MaxInputImagePixels = 2_000_000 };
        var rasterizer = new PdfToImageRasterizer(Options.Create(options));

        var png = rasterizer.RenderPageToPng(OnePage(612, 792), 0);

        var (width, height) = SizeOf(png);
        ((long)width * height).ShouldBeLessThanOrEqualTo(options.MaxInputImagePixels);
        ((long)width * height).ShouldBeGreaterThan(options.MaxInputImagePixels * 9 / 10);
        ((double)width / height).ShouldBe(612d / 792d, tolerance: 0.01);
    }

    private static (int Width, int Height) SizeOf(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded));
        return (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>A PDF of one blank page of <paramref name="width"/> × <paramref name="height"/> points.</summary>
    private static byte[] OnePage(int width, int height)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << >> /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream"
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
    }
}
