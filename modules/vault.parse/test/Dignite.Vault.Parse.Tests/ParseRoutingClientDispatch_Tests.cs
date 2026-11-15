using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Parse.ElBrunoMarkItDown;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DocumentExtraction;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using Volo.Abp.Testing;
using Xunit;

namespace Dignite.Vault.Parse;

/// <summary>
/// Verifies the router's per-file engine choice in the composed container: a specialized engine that claims
/// <c>.pdf</c> at a higher priority wins over the ElBruno catch-all, while other files still fall through to the
/// catch-all. This is the model behind the PdfExtractor module — and the regression guard that omitting a specialized
/// module degrades gracefully to ElBruno.
/// </summary>
public class ParseRoutingClientDispatch_Tests : AbpIntegratedTest<ParseRoutingClientDispatch_Tests.DispatchTestModule>
{
    public const string FakePdfMarkdown = "FAKE_PDF_PROVIDER_OUTPUT";

    private readonly DocumentExtractionClient _extractor;

    public ParseRoutingClientDispatch_Tests()
    {
        _extractor = GetRequiredService<DocumentExtractionClient>();
    }

    // Run under Autofac, the production host's container, so provider resolution / IEnumerable injection
    // matches production rather than the default Microsoft DI container.
    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        options.UseAutofac();
    }

    [Fact]
    public async Task Dispatches_pdf_to_the_specialized_provider_over_the_catch_all()
    {
        var result = await _extractor.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.7 placeholder")), "digital.pdf", "application/pdf");

        // The higher-priority .pdf engine wins; ElBruno (catch-all) is not used and no OCR fallback runs.
        result.Markdown.ShouldBe(FakePdfMarkdown);
    }

    [Fact]
    public async Task Dispatches_other_extensions_to_the_catch_all_provider()
    {
        var result = await _extractor.ParseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("hello dispatch")), "note.txt", "text/plain");

        result.Markdown.ShouldContain("hello dispatch");
        result.Markdown.ShouldNotContain(FakePdfMarkdown);
    }

    [DependsOn(
        typeof(VaultParseModule),
        typeof(VaultParseElBrunoMarkItDownModule))]
    public class DispatchTestModule : AbpModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var fakeOcr = ParseTestKit.Ocr("fake ocr markdown");
            context.Services.AddSingleton(fakeOcr);
            context.Services.AddSingleton<ParseExtractionClient>(fakeOcr);

            // A specialized engine that owns ".pdf" at the default priority (> fallback).
            context.Services.AddSingleton(TestDoubles.Engine(ParseExtractionPriorities.Default, ".pdf", FakePdfMarkdown, "FakePdf"));
        }
    }
}
