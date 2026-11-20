using System;
using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Indexing;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>Outlining a chunk on its pages from the regions of the elements its Markdown range overlaps.</summary>
public class IndexChunkRegions_Tests
{
    [Fact]
    public void Only_the_elements_the_range_overlaps_outline_the_chunk()
    {
        var regions = new List<DocumentPageRegionDto>
        {
            Region(0, 10, 1, 0.1, 0.10, 0.8, 0.02),
            Region(12, 10, 1, 0.1, 0.50, 0.8, 0.02),
            Region(30, 10, 2, 0.1, 0.10, 0.8, 0.02)
        };

        IndexChunkRegions.Of((12, 10), regions).ShouldBe([new ChunkRegion(1, 0.1, 0.5, 0.8, 0.02)]);
        IndexChunkRegions.Of((50, 10), regions).ShouldBeNull();
        IndexChunkRegions.Of(null, regions).ShouldBeNull();
        IndexChunkRegions.Of((0, 40), null).ShouldBeNull();
    }

    [Fact]
    public void The_cells_and_rows_of_a_table_join_into_one_box()
    {
        // Two rows of three cells, each 0.2 wide and 0.03 high, the rows touching.
        var regions = new List<DocumentPageRegionDto>();
        var offset = 0;
        foreach (var top in new[] { 0.30, 0.33 })
        {
            foreach (var left in new[] { 0.1, 0.3, 0.5 })
            {
                regions.Add(Region(offset, 4, 1, left, top, 0.2, 0.03));
                offset += 6;
            }
        }

        var box = IndexChunkRegions.Of((0, offset), regions).ShouldHaveSingleItem();

        box.ShouldBe(new ChunkRegion(1, 0.1, 0.3, 0.6, 0.06));
    }

    [Fact]
    public void Paragraphs_one_under_the_other_join_and_two_columns_stay_apart()
    {
        var regions = new List<DocumentPageRegionDto>
        {
            // The left column: two paragraphs a line apart.
            Region(0, 10, 1, 0.08, 0.10, 0.40, 0.10),
            Region(12, 10, 1, 0.08, 0.215, 0.40, 0.10),
            // The right column, from the top again, past the gutter.
            Region(24, 10, 1, 0.52, 0.10, 0.40, 0.10),
            // The next page.
            Region(36, 10, 2, 0.08, 0.10, 0.40, 0.10)
        };

        IndexChunkRegions.Of((0, 46), regions).ShouldBe(
        [
            new ChunkRegion(1, 0.08, 0.10, 0.40, 0.215),
            new ChunkRegion(1, 0.52, 0.10, 0.40, 0.10),
            new ChunkRegion(2, 0.08, 0.10, 0.40, 0.10)
        ]);
    }

    [Fact]
    public void Paragraphs_far_apart_stay_two_boxes()
    {
        var regions = new List<DocumentPageRegionDto>
        {
            Region(0, 10, 1, 0.1, 0.10, 0.8, 0.05),
            Region(12, 10, 1, 0.1, 0.60, 0.8, 0.05)
        };

        IndexChunkRegions.Of((0, 22), regions)!.Count.ShouldBe(2);
    }

    [Fact]
    public void Too_many_boxes_become_one_box_per_page()
    {
        // Short lines scattered down two pages, each too far from the last to join.
        var regions = Enumerable.Range(0, IndexingConsts.MaxChunkRegions + 4)
            .Select(i => Region(i * 10, 5, 1 + i % 2, (i % 3) * 0.3, 0.025 * (i / 2), 0.05, 0.01))
            .ToList();

        var boxes = IndexChunkRegions.Of((0, regions.Count * 10), regions)!;

        boxes.Select(box => box.Page).ShouldBe([1, 2]);
        boxes[0].Left.ShouldBe(0);
        boxes[0].Top.ShouldBe(0);
    }

    [Fact]
    public void A_chunk_keeps_only_regions_on_its_pages()
    {
        var chunk = new Chunk(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), null, 0, null, "本文", new string('a', 64));
        chunk.SetPages(1, 2);

        chunk.SetRegions([new ChunkRegion(2, 0.1, 0.1, 0.5, 0.1)]).ShouldBeTrue();
        chunk.SetRegions([new ChunkRegion(2, 0.1, 0.1, 0.5, 0.1)]).ShouldBeFalse("the same regions are no change");
        Should.Throw<ArgumentException>(() => chunk.SetRegions([new ChunkRegion(3, 0.1, 0.1, 0.5, 0.1)]));
        Should.Throw<ArgumentException>(() => chunk.SetRegions([new ChunkRegion(1, 0.6, 0.1, 0.5, 0.1)]));

        // Moved off page 2, the chunk's region there no longer belongs to it.
        chunk.SetPages(1, 1);
        chunk.Regions.ShouldBeNull();
    }

    private static DocumentPageRegionDto Region(int offset, int length, int page, double left, double top, double width, double height)
        => new()
        {
            Offset = offset,
            Length = length,
            PageNumber = page,
            Left = left,
            Top = top,
            Width = width,
            Height = height
        };
}
