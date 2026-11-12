using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.EntityFrameworkCore;
using Dignite.Vault.Index.Search;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// The ingestion host for tests: real Index (domain, application, EF on SQLite), with Extract's integration service,
/// the job queue, the vector store and the embedding model replaced by doubles a test can steer and inspect.
/// </summary>
[DependsOn(typeof(VaultIndexEntityFrameworkCoreTestModule))]
public class VaultIndexIngestionTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentIntegrationService>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());

        var searchIndex = new InMemoryChunkSearchIndex();
        context.Services.AddSingleton(searchIndex);
        context.Services.AddSingleton<IChunkSearchIndex>(searchIndex);

        var generator = new FakeEmbeddingGenerator();
        context.Services.AddSingleton(generator);
        context.Services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(VaultIndexConsts.EmbeddingGeneratorKey, generator);

        Configure<VaultIndexIngestionOptions>(options => options.MaxFailedAttempts = 2);
    }
}

/// <summary>Deterministic vectors, a counter of calls and inputs, and a switch to fail.</summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public string ModelId { get; set; } = "fake-embedding";

    public int Dimensions { get; set; } = 8;

    public int Calls { get; private set; }

    public List<string> Inputs { get; } = [];

    public Exception? FailWith { get; set; }

    /// <summary>Runs inside the model call, where an index job spends its time: the place a concurrent event lands.</summary>
    public Func<Task>? DuringGenerate { get; set; }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        if (DuringGenerate is { } during)
        {
            DuringGenerate = null;
            await during();
        }

        if (FailWith is not null)
        {
            throw FailWith;
        }

        var batch = values.ToList();
        Inputs.AddRange(batch);
        var embeddings = batch.Select(v => new Embedding<float>(VectorFor(v))).ToList();
        return new GeneratedEmbeddings<Embedding<float>>(embeddings)
        {
            Usage = new UsageDetails { InputTokenCount = batch.Count * 10 }
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata("fake", null, ModelId, Dimensions)
            : null;

    public void Dispose()
    {
    }

    private float[] VectorFor(string text)
    {
        var random = new Random(StringComparer.Ordinal.GetHashCode(text));
        return Enumerable.Range(0, Dimensions).Select(_ => (float)random.NextDouble()).ToArray();
    }
}

/// <summary>The vector store as a dictionary keyed by (generation, chunk).</summary>
public sealed class InMemoryChunkSearchIndex : IChunkSearchIndex
{
    public int? VectorDimensions { get; set; } = 8;

    public Dictionary<(Guid GenerationId, Guid ChunkId), ChunkVector> Vectors { get; } = new();

    public Task ReplaceDocumentAsync(Guid documentId, Guid generationId, IReadOnlyList<ChunkVector> vectors, CancellationToken cancellationToken = default)
    {
        foreach (var key in Vectors.Where(p => p.Key.GenerationId == generationId && p.Value.DocumentId == documentId).Select(p => p.Key).ToList())
        {
            Vectors.Remove(key);
        }

        foreach (var vector in vectors)
        {
            Vectors[(generationId, vector.ChunkId)] = vector;
        }

        return Task.CompletedTask;
    }

    public Task UpdateDocumentFiltersAsync(Guid documentId, Guid documentTypeId, Guid? ownerId, CancellationToken cancellationToken = default)
    {
        foreach (var pair in Vectors.Where(p => p.Value.DocumentId == documentId).ToList())
        {
            Vectors[pair.Key] = pair.Value with { DocumentTypeId = documentTypeId, OwnerId = ownerId };
        }

        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        foreach (var key in Vectors.Where(p => p.Value.DocumentId == documentId).Select(p => p.Key).ToList())
        {
            Vectors.Remove(key);
        }

        return Task.CompletedTask;
    }
}
