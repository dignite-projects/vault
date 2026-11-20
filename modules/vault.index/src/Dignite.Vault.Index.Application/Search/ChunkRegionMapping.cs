using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Index.Chunks;

namespace Dignite.Vault.Index.Search;

/// <summary>A chunk's regions as the DTOs hand them out.</summary>
public static class ChunkRegionMapping
{
    public static List<ChunkRegionDto>? ToDtos(IReadOnlyList<ChunkRegion>? regions)
        => regions?.Select(region => new ChunkRegionDto
        {
            Page = region.Page,
            Left = region.Left,
            Top = region.Top,
            Width = region.Width,
            Height = region.Height
        }).ToList();
}
