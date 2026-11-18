using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>The chunking templates beyond headings and tokens: the whole document, a chunk per table row, a chunk per page.</summary>
public class ChunkingTemplates_Tests
{
    private const string Invoice = """
        # 請求書

        株式会社サンプル商事 御中

        ## ご請求明細

        | 品名 | 数量 | 金額 |
        | --- | --- | --- |
        | コピー用紙 | 10 | 4,800 |
        |  |  |  |
        | トナー | 2 | 12,000 |

        ## お振込先

        みずなみ銀行 本店営業部 普通預金 1234567
        """;

    private readonly IndexChunker _chunker = new();

    [Fact]
    public async Task Whole_keeps_a_document_that_fits_in_one_chunk()
    {
        var chunks = await _chunker.ChunkAsync(Invoice, "doc", Profile(ChunkingStrategy.Whole, 400));

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.HeadingPath.ShouldBeNull();
        chunk.Text.ShouldContain("サンプル商事");
        chunk.Text.ShouldContain("1234567");
    }

    [Fact]
    public async Task Whole_chunks_a_longer_document_by_headings()
    {
        var chunks = await _chunker.ChunkAsync(Invoice, "doc", Profile(ChunkingStrategy.Whole, 60));

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldContain(c => c.HeadingPath == "# 請求書 ## お振込先");
    }

    [Fact]
    public async Task TableRow_makes_each_data_row_a_chunk_with_the_header_and_the_heading_path()
    {
        var chunks = await _chunker.ChunkAsync(Invoice, "doc", Profile(ChunkingStrategy.TableRow, 400));

        var rows = chunks.Where(c => c.HeadingPath == "# 請求書 ## ご請求明細").ToList();
        rows.Count.ShouldBe(2, "the empty row is skipped");
        rows[0].Text.Replace("\r\n", "\n").ShouldBe("# 請求書 ## ご請求明細\n| 品名 | 数量 | 金額 |\n| --- | --- | --- |\n| コピー用紙 | 10 | 4,800 |");
        rows[1].Text.ShouldContain("| トナー | 2 | 12,000 |");
        rows[1].Text.ShouldNotContain("コピー用紙");
        rows.ShouldAllBe(r => !r.Text.Contains('\u200B'));

        // The text around the table is chunked as the heading strategy chunks it, in document order.
        chunks.Select(c => c.HeadingPath).ShouldBe(["# 請求書", "# 請求書 ## ご請求明細", "# 請求書 ## ご請求明細", "# 請求書 ## お振込先"]);
        chunks[0].Text.ShouldContain("サンプル商事");
        chunks[^1].Text.ShouldContain("1234567");
    }

    [Fact]
    public async Task TableRow_keeps_a_table_with_no_data_row_in_the_text()
    {
        var chunks = await _chunker.ChunkAsync("# 一覧\n\n| 品名 | 金額 |\n| --- | --- |\n\n以上", "doc", Profile(ChunkingStrategy.TableRow, 400));

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.Text.ShouldContain("品名");
        chunk.Text.ShouldContain("以上");
    }

    [Fact]
    public async Task Page_makes_each_page_a_chunk_and_splits_a_page_over_the_budget()
    {
        var page2 = Invoice.IndexOf("## お振込先", StringComparison.Ordinal);
        var pages = new List<DocumentPageSpansDto>
        {
            Page(1, Invoice.IndexOf("株式会社", StringComparison.Ordinal), page2),
            Page(2, page2, Invoice.Length)
        };

        var chunks = await _chunker.ChunkAsync(Invoice, "doc", Profile(ChunkingStrategy.Page, 400), pages: pages);

        chunks.Count.ShouldBe(2);
        chunks[0].Text.ShouldStartWith("# 請求書", Case.Sensitive, "what precedes the first page's text goes with it");
        chunks[0].Text.ShouldContain("トナー");
        chunks[1].Text.ShouldStartWith("## お振込先");

        var small = await _chunker.ChunkAsync(Invoice, "doc", Profile(ChunkingStrategy.Page, 60), pages: pages);
        small.Count.ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task Page_without_pages_chunks_by_headings()
    {
        var page = await _chunker.ChunkAsync(Invoice, "doc", Profile(ChunkingStrategy.Page, 400));
        var heading = await _chunker.ChunkAsync(Invoice, "doc", Profile(ChunkingStrategy.Heading, 400));

        page.Select(c => c.Text).ShouldBe(heading.Select(c => c.Text));
    }

    private static ResolvedIndexProfile Profile(ChunkingStrategy strategy, int maxTokens) => new(strategy, maxTokens, 0);

    private static DocumentPageSpansDto Page(int number, int start, int end) => new()
    {
        PageNumber = number,
        Spans = [new DocumentTextSpanDto { Offset = start, Length = end - start }]
    };
}
