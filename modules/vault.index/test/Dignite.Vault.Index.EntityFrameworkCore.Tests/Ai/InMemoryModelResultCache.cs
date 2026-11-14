using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Index.Ai;

/// <summary>A <see cref="ModelResultCache"/> over a dictionary, for the enricher and compiler built without DI.</summary>
public sealed class InMemoryModelResultCache() : ModelResultCache(null!)
{
    public Dictionary<string, string> Entries { get; } = new();

    public override Task<string?[]> GetManyAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
        => Task.FromResult(keys.Select(key => Entries.GetValueOrDefault(key)).ToArray());

    public override Task SetManyAsync(IEnumerable<KeyValuePair<string, string>> results, CancellationToken cancellationToken = default)
    {
        foreach (var (key, value) in results)
        {
            Entries[key] = value;
        }

        return Task.CompletedTask;
    }
}
