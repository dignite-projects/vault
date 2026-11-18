using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <see cref="Document.FieldSources"/> only ever holds ranges of the current Markdown for fields that have a value:
/// a correction clears them, a value that goes takes its source with it, a rename carries it along.
/// </summary>
public class DocumentFieldSources_Tests : VaultExtractDomainTestBase<VaultExtractDomainTestModule>
{
    private const string Markdown = "# 請求書\n\n合計 13,200 円";

    private readonly DocumentPipelineRunManager _manager;

    public DocumentFieldSources_Tests()
    {
        _manager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task Sources_outside_the_Markdown_or_for_a_field_without_a_value_are_dropped()
    {
        var document = await ExtractedAsync();

        document.SetFieldSources(new Dictionary<string, IReadOnlyList<DocumentTextSpan>>
        {
            ["total"] = [new(7, 9), new(0, 0), new(-1, 2), new(10, Markdown.Length)],
            ["vendor"] = [new(0, 5)]
        });

        document.FieldSources.Keys.ShouldBe(["total"]);
        document.FieldSources["total"].ShouldBe([new DocumentTextSpan(7, 9)]);
    }

    [Fact]
    public async Task Clearing_every_field_clears_every_source()
    {
        // A reclassification or a turn into a container clears the fields with SetFlexFields(null).
        var document = await ExtractedAsync();
        document.SetFieldSources(Source("total", 7, 9));

        document.SetFlexFields(null);

        document.FieldSources.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_Markdown_correction_clears_every_source()
    {
        var document = await ExtractedAsync();
        document.SetFieldSources(Source("total", 7, 9));

        document.CorrectMarkdown("# 請求書\n\n合計 13,200 円（税込）");

        document.FieldSources.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_edit_keeps_the_source_of_a_field_that_keeps_a_value_and_drops_the_others()
    {
        var document = await ExtractedAsync(new Dictionary<string, object?> { ["total"] = 13200m, ["memo"] = "x" });
        document.SetFieldSources(new Dictionary<string, IReadOnlyList<DocumentTextSpan>>
        {
            ["total"] = [new(7, 9)],
            ["memo"] = [new(0, 5)]
        });

        document.SetFlexFields(new Dictionary<string, object?> { ["total"] = 13000m, ["memo"] = null });

        document.FieldSources.Keys.ShouldBe(["total"]);
    }

    [Fact]
    public async Task A_rename_moves_the_source_with_the_value()
    {
        var document = await ExtractedAsync();
        document.SetFieldSources(Source("total", 7, 9));

        document.RenameField("total", "amount").ShouldBeTrue();

        document.FlexFields.ContainsKey("amount").ShouldBeTrue();
        document.FieldSources.Keys.ShouldBe(["amount"]);
        document.FieldSources["amount"].ShouldBe([new DocumentTextSpan(7, 9)]);
    }

    private async Task<Document> ExtractedAsync(Dictionary<string, object?>? values = null)
    {
        var document = new Document(
            id: Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin("blobs/test.pdf", "test-user", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1024, "test.pdf"));
        var run = await _manager.StartAsync(document, VaultExtractPipelines.Parse);
        await _manager.CompleteParseAsync(document, run, markdown: Markdown, title: "請求書", language: "ja",
            extractionMetadata: new DocumentParseMetadata(providerName: "TestProvider"));
        document.SetFlexFields(values ?? new Dictionary<string, object?> { ["total"] = 13200m });
        return document;
    }

    private static Dictionary<string, IReadOnlyList<DocumentTextSpan>> Source(string field, int offset, int length)
        => new() { [field] = [new DocumentTextSpan(offset, length)] };
}
