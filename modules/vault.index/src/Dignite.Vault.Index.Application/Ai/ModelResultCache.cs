using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Volo.Abp.Caching;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Index.Ai;

/// <summary>
/// The results of Index's model calls that passed their checks, keyed by everything that produced them: the model, the
/// instructions and the input. A document re-indexed after a small edit, re-synced after its job failed part way, or
/// recompiled after some of its summaries failed asks the model again only for what changed or failed.
/// <para>
/// It stores results, not responses, as RAGFlow's per-chunk extraction cache does: an answer that could not be read, or
/// that a check rejected, is never stored, so asking again reaches the model. Microsoft.Extensions.AI's caching chat
/// client stores every response, so the retry of an unreadable answer would get the same answer back from it. Entries
/// live for <see cref="Lifetime"/> in the host's distributed cache, under the current tenant.
/// </para>
/// </summary>
public class ModelResultCache : ITransientDependency
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    /// <summary>Raise to drop every stored result, when what a result means changes without its key changing.</summary>
    private const string Version = "1";

    protected IDistributedCache<ModelResultCacheItem> Cache { get; }

    public ModelResultCache(IDistributedCache<ModelResultCacheItem> cache)
    {
        Cache = cache;
    }

    /// <summary>
    /// The key of the result <paramref name="client"/>'s model gives for <paramref name="instructions"/> and
    /// <paramref name="input"/>; <c>null</c> when the client does not name its model, as results of two models could
    /// not then be told apart.
    /// </summary>
    public static string? Key(IChatClient client, string instructions, string input)
    {
        var modelId = client.GetService<ChatClientMetadata>()?.DefaultModelId;
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', Version, modelId, instructions, input))));
    }

    /// <summary>The stored results of <paramref name="keys"/>, in their order; <c>null</c> where none is stored.</summary>
    public virtual async Task<string?[]> GetManyAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var found = await Cache.GetManyAsync(keys, token: cancellationToken);
        return found.Select(pair => pair.Value?.Value).ToArray();
    }

    public virtual async Task SetManyAsync(IEnumerable<KeyValuePair<string, string>> results, CancellationToken cancellationToken = default)
    {
        var items = results
            .DistinctBy(pair => pair.Key)
            .Select(pair => new KeyValuePair<string, ModelResultCacheItem>(pair.Key, new ModelResultCacheItem { Value = pair.Value }))
            .ToList();
        if (items.Count > 0)
        {
            await Cache.SetManyAsync(
                items,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime },
                token: cancellationToken);
        }
    }
}

[CacheName("VaultIndex.ModelResult")]
public class ModelResultCacheItem
{
    public string Value { get; set; } = default!;
}
