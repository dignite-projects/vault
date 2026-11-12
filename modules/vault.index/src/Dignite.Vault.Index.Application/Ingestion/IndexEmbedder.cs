using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>Vectors for a batch of texts, and what they cost.</summary>
public sealed record EmbeddingBatch(IReadOnlyList<ReadOnlyMemory<float>> Vectors, long? InputTokenCount);

/// <summary>
/// Calls the host's embedding model, registered under <see cref="VaultIndexConsts.EmbeddingGeneratorKey"/>, in
/// batches of <see cref="VaultIndexIngestionOptions.EmbeddingBatchSize"/>. The model id comes from the generator's own
/// metadata, so the generation record cannot disagree with what actually produced the vectors.
/// </summary>
public class IndexEmbedder : ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;
    private readonly VaultIndexIngestionOptions _options;

    public IndexEmbedder(IServiceProvider serviceProvider, IOptions<VaultIndexIngestionOptions> options)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    /// <summary>
    /// Whether the host registered an embedding model. Without one, ingestion is off: nothing is indexed and no
    /// document is marked failed, so a model added later picks every document up through the first-install backfill.
    /// </summary>
    public virtual bool IsConfigured
        => _serviceProvider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(VaultIndexConsts.EmbeddingGeneratorKey) is not null;

    /// <summary>The configured model's id, as the generator reports it.</summary>
    public virtual string GetModelId()
    {
        var modelId = GetGenerator().GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new BusinessException(VaultIndexErrorCodes.EmbeddingGeneratorMissing);
        }

        return modelId;
    }

    public virtual async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        var generator = GetGenerator();
        var vectors = new List<ReadOnlyMemory<float>>(inputs.Count);
        long tokens = 0;
        var tokensReported = false;

        foreach (var batch in inputs.Chunk(Math.Max(1, _options.EmbeddingBatchSize)))
        {
            var result = await generator.GenerateAsync(batch, cancellationToken: cancellationToken);
            vectors.AddRange(result.Select(e => e.Vector));
            if (result.Usage?.InputTokenCount is { } used)
            {
                tokens += used;
                tokensReported = true;
            }
        }

        if (vectors.Count != inputs.Count)
        {
            throw new InvalidOperationException($"The embedding model returned {vectors.Count} vectors for {inputs.Count} inputs.");
        }

        return new EmbeddingBatch(vectors, tokensReported ? tokens : null);
    }

    protected virtual IEmbeddingGenerator<string, Embedding<float>> GetGenerator()
    {
        return _serviceProvider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(VaultIndexConsts.EmbeddingGeneratorKey)
               ?? throw new BusinessException(VaultIndexErrorCodes.EmbeddingGeneratorMissing);
    }
}
