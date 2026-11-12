using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Search;

/// <summary>Pointers, the outline built from heading paths, and rank fusion: pure functions.</summary>
public class SearchPrimitives_Tests
{
    private static readonly Guid Document = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData("chunk:3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("node:d0000000-0000-0000-0000-000000000001/1")]
    [InlineData("node:d0000000-0000-0000-0000-000000000001/2.1.3")]
    public void A_pointer_round_trips(string value)
    {
        IndexPointer.TryParse(value, out var pointer).ShouldBeTrue();
        pointer!.ToString().ShouldBe(value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chunk:not-a-guid")]
    [InlineData("chunk:3f2504e04f8911d39a0c0305e82c3301")]
    [InlineData("node:d0000000-0000-0000-0000-000000000001")]
    [InlineData("node:d0000000-0000-0000-0000-000000000001/")]
    [InlineData("node:d0000000-0000-0000-0000-000000000001/0")]
    [InlineData("node:d0000000-0000-0000-0000-000000000001/1.-2")]
    [InlineData("node:d0000000-0000-0000-0000-000000000001/1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1.1")]
    [InlineData("doc:d0000000-0000-0000-0000-000000000001[0:500]")]
    public void Anything_else_is_not_a_pointer(string? value)
    {
        IndexPointer.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Heading_paths_parse_into_levels_and_titles()
    {
        DocumentOutline.ParseHeadingPath("# 契約書 ## 第1条（目的） ### 1.1 細則").ShouldBe(
        [
            (1, "契約書"),
            (2, "第1条（目的）"),
            (3, "1.1 細則")
        ]);
        DocumentOutline.ParseHeadingPath("## C# の使い方").ShouldBe([(2, "C# の使い方")]);
        DocumentOutline.ParseHeadingPath(null).ShouldBeEmpty();
    }

    [Fact]
    public void The_outline_nests_numbers_and_spans_its_chunks()
    {
        var outline = DocumentOutline.Build(
        [
            new OutlineChunk(0, null),
            new OutlineChunk(1, "# 契約書"),
            new OutlineChunk(2, "# 契約書 ## 第1条"),
            new OutlineChunk(3, "# 契約書 ## 第1条"),
            new OutlineChunk(4, "# 契約書 ## 第1条 ### 1.1"),
            new OutlineChunk(5, "# 契約書 ## 第2条"),
            new OutlineChunk(6, "# 附則")
        ]);

        outline.Nodes.Select(n => (Path: string.Join('.', n.Path), n.Title, n.FirstOrdinal, n.LastOrdinal, n.ChunkCount)).ShouldBe(
        [
            ("1", "契約書", 1, 5, 5),
            ("1.1", "第1条", 2, 4, 3),
            ("1.1.1", "1.1", 4, 4, 1),
            ("1.2", "第2条", 5, 5, 1),
            ("2", "附則", 6, 6, 1)
        ]);
        outline.Find([1, 2])!.HeadingPath.ShouldBe("# 契約書 ## 第2条");
        outline.Find([3]).ShouldBeNull();
    }

    [Fact]
    public void Fusion_rewards_agreement_and_reads_ranks_not_scores()
    {
        Guid a = Id(1), b = Id(2), c = Id(3), d = Id(4);
        var keyword = new List<ChunkMatch> { new(a, Document, 900), new(b, Document, 800), new(c, Document, 1) };
        var vector = new List<ChunkMatch> { new(d, Document, 0.01), new(c, Document, 0.02), new(a, Document, 0.9) };

        var fused = ReciprocalRankFusion.Fuse(keyword, vector, 60);

        // a: 1/61 + 1/63; c: 1/63 + 1/62; d: 1/61; b: 1/62.
        fused.Select(f => f.ChunkId).ShouldBe([a, c, d, b]);
        fused[0].KeywordRank.ShouldBe(1);
        fused[0].VectorRank.ShouldBe(3);
        fused[2].KeywordRank.ShouldBeNull();
    }

    private static Guid Id(int i) => Guid.Parse($"00000000-0000-0000-0000-{i:D12}");
}
