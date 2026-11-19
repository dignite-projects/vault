using System.Threading.Tasks;
using Dignite.Vault.Index.Profiles;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>Tables as parsed documents render them, which DataIngestion's Markdown reader must take whole.</summary>
public class IndexChunkerTables_Tests
{
    [Theory]
    [InlineData("| | | 名称 | 金額 |\n| --- | --- | --- | --- |\n| | 運用 | 監視 | 60,000 |")]
    [InlineData("| 区分 | 名称 | 金額 |\n| --- | --- | --- |\n| 保守 | 監視 | 60,000 |\n| | | |")]
    [InlineData("| 区分 | 名称 | 金額 |\n| --- | --- | --- |\n|  | 監視 | 60,000 |")]
    [InlineData("| 区分 | 名称 | 金額 |\n| --- | --- | --- |\n|| 監視 | 60,000 |")]
    public async Task A_table_with_empty_cells_is_chunked_and_the_cells_stay_empty(string table)
    {
        var chunks = await new IndexChunker().ChunkAsync("# 価格表\n\n" + table, "doc", ResolvedIndexProfile.BuiltIn);

        chunks.ShouldContain(c => c.Text.Contains("60,000"));
        chunks.ShouldAllBe(c => !c.Text.Contains('\u200B'));
    }

    [Fact]
    public async Task A_figure_is_chunked_as_its_caption_and_its_text()
    {
        // A figure as Parse writes it (Azure Document Intelligence's form): the blank lines leave the transcription
        // Markdown, so its table is a table; the tags are not text.
        const string markdown = "# 報告\n\n本文。\n\n<figure>\n<figcaption>図1 領収書</figcaption>\n\n| 品目 | 金額 |\n| --- | --- |\n| 監視 | 60,000 |\n\n</figure>\n\n後文。";

        var chunks = await new IndexChunker().ChunkAsync(markdown, "doc", ResolvedIndexProfile.BuiltIn);

        chunks.ShouldContain(c => c.Text.Contains("図1 領収書"));
        chunks.ShouldContain(c => c.Text.Contains("| 監視 | 60,000 |"));
        chunks.ShouldAllBe(c => !c.Text.Contains("figure>") && !c.Text.Contains("figcaption"));
    }

    [Fact]
    public void An_escaped_pipe_does_not_open_a_cell()
    {
        var prepared = new IndexChunker().Prepare("| 記号 | 意味 |\n| --- | --- |\n| a \\| b | または |");

        prepared.ShouldNotContain("\u200B");
    }
}
