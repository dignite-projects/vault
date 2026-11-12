using System;
using System.Collections.Generic;
using System.Linq;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// What one search may reach: the caller's read scope as Extract resolved it, narrowed by the caller's own
/// conditions. Every retrieval method of <see cref="IChunkSearchIndex"/> takes one, so a query without a scope does
/// not compile. The tenant is not here: it is the ambient tenant, applied by ABP's data filter.
/// </summary>
public sealed class ChunkSearchScope
{
    /// <summary>The caller reads every document of the tenant.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>The types the caller reads through a grant (ignored when <see cref="IsUnrestricted"/>).</summary>
    public IReadOnlyCollection<Guid> ReadableTypeIds { get; }

    /// <summary>The caller's user id when they read the documents they uploaded.</summary>
    public Guid? OwnerId { get; }

    /// <summary>The types the caller asked for; <c>null</c> for every readable type.</summary>
    public IReadOnlyCollection<Guid>? RequestedTypeIds { get; }

    public ChunkSearchScope(
        bool isUnrestricted,
        IEnumerable<Guid> readableTypeIds,
        Guid? ownerId,
        IEnumerable<Guid>? requestedTypeIds = null)
    {
        IsUnrestricted = isUnrestricted;
        ReadableTypeIds = isUnrestricted ? [] : readableTypeIds.Distinct().ToArray();
        OwnerId = isUnrestricted ? null : ownerId;
        RequestedTypeIds = requestedTypeIds?.Distinct().ToArray();
    }

    /// <summary>Nothing can match: no grant and no owner arm, or no requested type. Callers skip the query.</summary>
    public bool ReachesNothing
        => (!IsUnrestricted && ReadableTypeIds.Count == 0 && OwnerId is null) || RequestedTypeIds is { Count: 0 };

    /// <summary>The same rule as <see cref="ChunkScopeFilter"/>, for a single row already in memory.</summary>
    public bool Reaches(IChunkScopeFields row)
    {
        if (row.IsHidden)
        {
            return false;
        }

        if (RequestedTypeIds is not null && !RequestedTypeIds.Contains(row.DocumentTypeId))
        {
            return false;
        }

        return IsUnrestricted
               || ReadableTypeIds.Contains(row.DocumentTypeId)
               || (OwnerId is not null && row.OwnerId == OwnerId);
    }
}
