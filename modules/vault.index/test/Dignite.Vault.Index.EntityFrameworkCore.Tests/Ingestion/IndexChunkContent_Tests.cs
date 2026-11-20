using System;
using Dignite.Vault.Index.Indexing;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>Telling from the document's Markdown whether a chunk holds a table or a figure.</summary>
public class IndexChunkContent_Tests
{
    private const string Markdown = """
        # 見積書

        下記の通りお見積り申し上げます。

        | 品名 | 数量 |
        | --- | --- |
        | 机 | 2 |

        <table>
        <tr><th>品名</th><th>単価</th></tr>
        <tr><td>椅子</td><td>5,000</td></tr>
        </table>

        <figure id="fig-1">
        <figcaption>図1 配置図</figcaption>

        会議室の机の配置を示す図。

        </figure>

        ```html
        <figure id="fig-2">
        </figure>
        ```
        """;

    [Theory]
    [InlineData("下記の通り", ChunkContent.Text)]
    [InlineData("| 机 | 2 |", ChunkContent.Table)]
    [InlineData("<td>椅子</td>", ChunkContent.Table)]
    [InlineData("会議室の机", ChunkContent.Figure)]
    [InlineData("<figcaption>", ChunkContent.Figure)]
    [InlineData("<figure id=\"fig-2\">", ChunkContent.Text)]
    public void A_chunk_holds_what_its_part_of_the_Markdown_holds(string text, ChunkContent expected)
    {
        var blocks = IndexChunkContent.Blocks(Markdown);
        var offset = Markdown.IndexOf(text, StringComparison.Ordinal);

        IndexChunkContent.Of((offset, text.Length), blocks).ShouldBe(expected);
    }

    [Fact]
    public void A_chunk_over_a_table_and_a_figure_holds_both()
    {
        var blocks = IndexChunkContent.Blocks(Markdown);
        var start = Markdown.IndexOf("| 品名", StringComparison.Ordinal);
        var end = Markdown.IndexOf("会議室", StringComparison.Ordinal);

        IndexChunkContent.Of((start, end - start), blocks).ShouldBe(ChunkContent.Table | ChunkContent.Figure);
        IndexChunkContent.Of(null, blocks).ShouldBeNull();
        IndexChunkContent.Blocks("本文だけ。").ShouldBeEmpty();
    }
}
