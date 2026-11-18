using System;
using System.Drawing;
using System.IO;
using Microsoft.Extensions.Options;
using PDFtoImage;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse.Ocr.VisionLlm;

/// <summary>
/// Default <see cref="IPdfRasterizer"/> backed by PDFtoImage (PDFium + SkiaSharp).
/// <para>
/// PDFium is not thread-safe; PDFtoImage serialises all native calls with an internal lock, so concurrent
/// document pipelines will queue on PDF rasterization. For the single-document background-job path this is
/// a non-issue (OCR is already a slow sequential external call).
/// </para>
/// <para>
/// A page is rendered at <see cref="Dpi"/>, or at the lower resolution that keeps it within
/// <see cref="VisionLlmOcrOptions.MaxInputImagePixels"/>: an A0 drawing is 139 megapixels at 300 dpi, and a page's
/// size is whatever its MediaBox says, so a PDF of a few bytes could otherwise ask for gigabytes.
/// </para>
/// </summary>
// [ExposeServices] is REQUIRED: the class name does not end with "PdfRasterizer", so ABP's default
// conventional registration (which exposes IFoo only when the class name ends with "Foo") would register
// the concrete type but NOT the IPdfRasterizer interface — and VisionLlmOcrClient injects the interface.
// Without this, resolving the OCR engine throws at runtime once the VisionLlm module is enabled.
[ExposeServices(typeof(IPdfRasterizer))]
public class PdfToImageRasterizer : IPdfRasterizer, ITransientDependency
{
    /// <summary>The resolution a page is rendered at when it fits the input cap: PDFtoImage's default.</summary>
    public const int Dpi = 300;

    private const double PointsPerInch = 72;

    private readonly VisionLlmOcrOptions _options;

    public PdfToImageRasterizer(IOptions<VisionLlmOcrOptions> options)
    {
        _options = options.Value;
    }

    public virtual int GetPageCount(byte[] pdf)
    {
        return Conversion.GetPageCount(pdf);
    }

    public virtual byte[] RenderPageToPng(byte[] pdf, int pageIndex)
    {
        using var stream = new MemoryStream();
        // SavePng renders the page to an SKBitmap and PNG-encodes it into the stream internally,
        // so the rasterizer itself never touches SkiaSharp types (VisionLlmImageDownscaler does, later,
        // when the page is over the pixel budget). page (System.Index) accepts the 0-based int via
        // implicit conversion.
        Conversion.SavePng(stream, pdf, pageIndex, options: new RenderOptions(Dpi: GetDpi(pdf, pageIndex)));
        return stream.ToArray();
    }

    /// <summary>
    /// <see cref="Dpi"/>, or the highest whole resolution at which the page is within
    /// <see cref="VisionLlmOcrOptions.MaxInputImagePixels"/>, counting each side rounded up.
    /// </summary>
    protected virtual int GetDpi(byte[] pdf, int pageIndex)
    {
        var cap = _options.MaxInputImagePixels;
        if (cap <= 0)
        {
            return Dpi;
        }

        var page = Conversion.GetPageSize(pdf, pageIndex);
        var pixels = PixelsAt(page, Dpi);
        if (pixels <= cap)
        {
            return Dpi;
        }

        var dpi = Math.Max(1, (int)Math.Floor(Dpi * Math.Sqrt(cap / pixels)));
        while (dpi > 1 && PixelsAt(page, dpi) > cap)
        {
            dpi--;
        }

        return dpi;
    }

    private static double PixelsAt(SizeF page, int dpi)
    {
        return Math.Ceiling(page.Width * dpi / PointsPerInch) * Math.Ceiling(page.Height * dpi / PointsPerInch);
    }
}
