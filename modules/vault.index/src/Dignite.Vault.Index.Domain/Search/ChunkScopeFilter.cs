using System;
using System.Linq;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The one place the read-scope condition is built for a query. Every retrieval query, in every storage provider,
/// passes its rows through <see cref="Apply{T}"/> before ranking, and the status pages pass document rows through
/// <see cref="ApplyReadScope{T}"/>, so the condition cannot drift between them, and removing any part of it turns the
/// scope tests red. The tenant is ABP's data filter on the queried DbSet; a provider outside EF Core (an external
/// engine) must add it itself.
/// </summary>
public static class ChunkScopeFilter
{
    /// <summary>Rows retrieval may return: in retrieval, and within the read scope.</summary>
    public static IQueryable<T> Apply<T>(IQueryable<T> query, ChunkSearchScope scope)
        where T : IChunkScopeFields
    {
        ArgumentNullException.ThrowIfNull(scope);

        query = query.Where(x => !x.IsHidden);
        return ApplyReadScope(query, scope);
    }

    /// <summary>Rows the caller may see at all, in retrieval or not: the read scope and the requested types.</summary>
    public static IQueryable<T> ApplyReadScope<T>(IQueryable<T> query, ChunkSearchScope scope)
        where T : IReadScopeFields
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.RequestedTypeIds is not null)
        {
            var requested = scope.RequestedTypeIds.ToList();
            query = query.Where(x => requested.Contains(x.DocumentTypeId));
        }

        if (scope.IsUnrestricted)
        {
            return query;
        }

        var readable = scope.ReadableTypeIds.ToList();
        if (scope.OwnerId is { } ownerId)
        {
            return query.Where(x => readable.Contains(x.DocumentTypeId) || x.OwnerId == ownerId);
        }

        return query.Where(x => readable.Contains(x.DocumentTypeId));
    }
}
