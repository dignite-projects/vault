using System;
using System.Collections.Generic;
using System.Linq;

namespace Dignite.Vault.Index.Search;

/// <summary>A chunk after fusion: its fused score and where each leg ranked it (from 1).</summary>
public sealed record FusedChunk(Guid ChunkId, Guid DocumentId, double Score, int? KeywordRank, int? VectorRank);

/// <summary>
/// Reciprocal rank fusion: score = Σ 1 / (k + rank) over the legs that returned the chunk. It reads ranks only, never
/// the legs' own scores (a full-text rank and a cosine distance are not comparable), so the order does not depend on
/// which store produced the candidates.
/// </summary>
public static class ReciprocalRankFusion
{
    public static IReadOnlyList<FusedChunk> Fuse(IReadOnlyList<ChunkMatch> keyword, IReadOnlyList<ChunkMatch> vector, int k)
    {
        var fused = new Dictionary<Guid, (Guid DocumentId, double Score, int? KeywordRank, int? VectorRank)>();

        for (var i = 0; i < keyword.Count; i++)
        {
            var match = keyword[i];
            if (fused.ContainsKey(match.ChunkId))
            {
                continue;
            }

            fused[match.ChunkId] = (match.DocumentId, 1d / (k + i + 1), i + 1, null);
        }

        for (var i = 0; i < vector.Count; i++)
        {
            var match = vector[i];
            if (fused.TryGetValue(match.ChunkId, out var entry))
            {
                if (entry.VectorRank is null)
                {
                    fused[match.ChunkId] = (entry.DocumentId, entry.Score + 1d / (k + i + 1), entry.KeywordRank, i + 1);
                }

                continue;
            }

            fused[match.ChunkId] = (match.DocumentId, 1d / (k + i + 1), null, i + 1);
        }

        // Ties (the same rank in one leg each) go to the better single rank, then to the id, so the order is stable.
        return fused
            .Select(p => new FusedChunk(p.Key, p.Value.DocumentId, p.Value.Score, p.Value.KeywordRank, p.Value.VectorRank))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => Math.Min(c.KeywordRank ?? int.MaxValue, c.VectorRank ?? int.MaxValue))
            .ThenBy(c => c.ChunkId)
            .ToList();
    }
}
