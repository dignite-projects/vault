using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using UglyToad.PdfPig;
using Volo.Abp.DependencyInjection;
using Xunit;

namespace Dignite.Vault.Parse.Pdf;

/// <summary>
/// <see cref="PdfPageCopier"/> cuts a page range out of a PDF as a PDF of its own, the pages unchanged, and is found
/// as a <see cref="ParsePageCopier"/>.
/// </summary>
public class PdfPageCopier_Tests
{
    private static readonly byte[] ThreePages = PdfFixtures.BuildMultiPage(
    [
        [("Invoice 0451", 700)],
        [("Delivery note 77", 700)],
        [("Contract", 700)]
    ]);

    [Fact]
    public async Task A_page_range_becomes_a_PDF_of_those_pages()
    {
        var copied = await new PdfPageCopier().CopyPagesAsync(new MemoryStream(ThreePages), 2, 3);

        using var document = PdfDocument.Open(copied);
        document.NumberOfPages.ShouldBe(2);
        document.GetPage(1).Text.ShouldContain("Delivery note 77");
        document.GetPage(2).Text.ShouldContain("Contract");
        document.GetPages().SelectMany(page => page.GetWords()).ShouldNotContain(word => word.Text == "0451");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 4)]
    [InlineData(3, 2)]
    public async Task A_range_outside_the_file_is_invalid_data(int first, int last)
    {
        await Should.ThrowAsync<InvalidDataException>(
            () => new PdfPageCopier().CopyPagesAsync(new MemoryStream(ThreePages), first, last));
    }

    [Fact]
    public async Task A_file_that_is_not_a_PDF_is_invalid_data()
    {
        await Should.ThrowAsync<InvalidDataException>(
            () => new PdfPageCopier().CopyPagesAsync(new MemoryStream("not a pdf"u8.ToArray()), 1, 1));
    }

    [Fact]
    public void It_is_found_as_a_page_copier_for_PDFs_only()
    {
        ExposedServiceExplorer.GetExposedServices(typeof(PdfPageCopier)).ShouldContain(typeof(ParsePageCopier));
        typeof(ITransientDependency).IsAssignableFrom(typeof(PdfPageCopier)).ShouldBeTrue();
        new PdfPageCopier().CanCopy(".pdf", null).ShouldBeTrue();
        new PdfPageCopier().CanCopy(".png", "image/png").ShouldBeFalse();
    }
}
