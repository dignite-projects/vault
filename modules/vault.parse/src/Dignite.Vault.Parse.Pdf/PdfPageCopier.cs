using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// Cuts a page range out of a PDF with PdfPig's <see cref="PdfDocumentBuilder"/>, which copies each page with its
/// content stream and resources as they are, the same copy the PDF engine uses to send one page to OCR.
/// </summary>
[ExposeServices(typeof(ParsePageCopier), typeof(PdfPageCopier))]
public class PdfPageCopier : ParsePageCopier, ITransientDependency
{
    public override bool CanCopy(string extension, string? mediaType)
        => extension == ".pdf" || string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public override async Task<byte[]> CopyPagesAsync(
        Stream source, int firstPage, int lastPage, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);

        try
        {
            using var document = PdfDocument.Open(buffer.ToArray());
            if (firstPage < 1 || lastPage < firstPage || lastPage > document.NumberOfPages)
            {
                throw new InvalidDataException(
                    $"Pages {firstPage}-{lastPage} are outside the PDF's {document.NumberOfPages} pages.");
            }

            using var builder = new PdfDocumentBuilder();
            for (var page = firstPage; page <= lastPage; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.AddPage(document, page);
            }

            return builder.Build();
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OperationCanceledException)
        {
            throw new InvalidDataException("The PDF's pages could not be copied.", ex);
        }
    }
}
