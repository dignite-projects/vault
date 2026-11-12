using System;
using System.Linq;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The one place the read-scope condition is built for a query. Every retrieval query, in every storage provider,
/// passes its rows through <see cref="Apply{T}"/> before ranking, so the condition cannot drift between the keyword
/// and the vector leg, and removing any part of it turns the scope tests red. The tenant is ABP's data filter on the
/// queried DbSet; a provider outside EF Core (an external engine) must add it itself.
/// </summary>
public static class ChunkScopeFilter
{
    public static IQueryable<T> Apply<T>(IQueryable<T> query, ChunkSearchScope scope)
        where T : IChunkScopeFields
    {
        ArgumentNullException.ThrowIfNull(scope);

        query = query.Where(x => !x.IsHidden);

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
