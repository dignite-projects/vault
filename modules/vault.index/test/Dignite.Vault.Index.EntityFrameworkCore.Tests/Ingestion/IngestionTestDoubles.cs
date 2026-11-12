using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Index.Chunks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Authorization;
using Volo.Abp.Security.Claims;
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

        context.Services.AddSingleton(sp => new InMemoryChunkSearchIndex(sp));
        context.Services.AddSingleton<IChunkSearchIndex>(sp => sp.GetRequiredService<InMemoryChunkSearchIndex>());
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton<SearchPermissionSwitch>();
        context.Services.Replace(ServiceDescriptor.Transient<IAuthorizationService, SwitchableAuthorizationService>());
        context.Services.Replace(ServiceDescriptor.Transient<IAbpAuthorizationService, SwitchableAuthorizationService>());

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

/// <summary>
/// The vector store as a dictionary keyed by (generation, chunk), with both retrieval legs: cosine over the stored
/// vectors, and a term match over the chunk rows in SQLite. Both go through the real <see cref="ChunkScopeFilter"/>.
/// </summary>
public sealed class InMemoryChunkSearchIndex : IChunkSearchIndex
{
    private readonly IServiceProvider _serviceProvider;

    public InMemoryChunkSearchIndex(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

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

    public Task UpdateDocumentFiltersAsync(Guid documentId, Guid documentTypeId, Guid? ownerId, bool isHidden, CancellationToken cancellationToken = default)
    {
        foreach (var pair in Vectors.Where(p => p.Value.DocumentId == documentId).ToList())
        {
            Vectors[pair.Key] = pair.Value with { DocumentTypeId = documentTypeId, OwnerId = ownerId, IsHidden = isHidden };
        }

        return Task.CompletedTask;
    }

    public Task SetChunkDisabledAsync(Guid chunkId, bool isDisabled, CancellationToken cancellationToken = default)
    {
        foreach (var pair in Vectors.Where(p => p.Key.ChunkId == chunkId).ToList())
        {
            Vectors[pair.Key] = pair.Value with { IsDisabled = isDisabled };
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

    public Task<IReadOnlyList<ChunkMatch>> SearchVectorsAsync(
        Guid generationId,
        ReadOnlyMemory<float> queryVector,
        ChunkSearchScope scope,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = queryVector.ToArray();
        IReadOnlyList<ChunkMatch> matches = ChunkScopeFilter
            .Apply(Vectors.Where(p => p.Key.GenerationId == generationId).Select(p => p.Value).AsQueryable(), scope)
            .AsEnumerable()
            .Select(v => new ChunkMatch(v.ChunkId, v.DocumentId, CosineDistance(query, v.Vector.ToArray())))
            .OrderBy(m => m.Score)
            .ThenBy(m => m.ChunkId)
            .Take(take)
            .ToList();
        return Task.FromResult(matches);
    }

    public async Task<IReadOnlyList<ChunkMatch>> SearchKeywordsAsync(
        string query,
        ChunkSearchScope scope,
        int take,
        CancellationToken cancellationToken = default)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var repository = _serviceProvider.GetRequiredService<IChunkRepository>();
        var chunks = await repository.AsyncExecuter.ToListAsync(ChunkScopeFilter.Apply(await repository.GetQueryableAsync(), scope));
        return chunks
            .Select(c => (Chunk: c, Score: terms.Count(t => c.Text.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.Id)
            .Take(take)
            .Select(x => new ChunkMatch(x.Chunk.Id, x.Chunk.DocumentId, x.Score))
            .ToList();
    }

    public Task<ChunkSearchIndexStatus> GetStatusAsync(Guid? generationId, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChunkSearchIndexStatus(
            Vectors.Keys.LongCount(k => k.GenerationId == generationId),
            KeywordIndexAvailable: true,
            KeywordIndexPendingChanges: 0,
            KeywordIndexPopulating: false));

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return 1 - dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>Which policies the test denies; everything else is allowed, as with AddAlwaysAllowAuthorization.</summary>
public sealed class SearchPermissionSwitch
{
    public HashSet<string> Denied { get; } = new();
}

public sealed class SwitchableAuthorizationService : IAbpAuthorizationService
{
    private readonly SearchPermissionSwitch _switch;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public SwitchableAuthorizationService(
        SearchPermissionSwitch permissionSwitch,
        ICurrentPrincipalAccessor principalAccessor,
        IServiceProvider serviceProvider)
    {
        _switch = permissionSwitch;
        _principalAccessor = principalAccessor;
        ServiceProvider = serviceProvider;
    }

    public IServiceProvider ServiceProvider { get; }

    public System.Security.Claims.ClaimsPrincipal CurrentPrincipal => _principalAccessor.Principal;

    public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        => Task.FromResult(AuthorizationResult.Success());

    public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, string policyName)
        => Task.FromResult(_switch.Denied.Contains(policyName) ? AuthorizationResult.Failed() : AuthorizationResult.Success());
}
