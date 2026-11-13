using System;
using Dignite.Vault.Index.Indexing;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.Profiles;

/// <summary>
/// How the documents of one type are indexed: chunking now, enrichment and compiled artifacts as they arrive. A row
/// with no <see cref="DocumentTypeId"/> is its layer's default, used by every type without a row of its own. Profiles
/// follow Extract's two layers: the host's for Host documents, each tenant's for its own.
/// <para>
/// <see cref="Version"/> moves on every change. An indexed document records the version it was built with, so a
/// changed profile re-chunks exactly the documents built with an older one.
/// </para>
/// </summary>
public class IndexProfile : AuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; protected set; }

    /// <summary>The Extract document type this profile is for; <c>null</c> for the layer default.</summary>
    public virtual Guid? DocumentTypeId { get; protected set; }

    public virtual ChunkingStrategy ChunkingStrategy { get; protected set; }

    public virtual int MaxTokensPerChunk { get; protected set; }

    public virtual int OverlapTokens { get; protected set; }

    /// <summary>What an LLM adds to each chunk; nothing by default.</summary>
    public virtual IndexEnrichment Enrichment { get; protected set; }

    /// <summary>
    /// What an LLM compiles from each document; nothing by default. Not part of the chunking settings: changing it
    /// re-chunks nothing, it only compiles or drops the artifacts.
    /// </summary>
    public virtual IndexCompilation Compilation { get; protected set; }

    public virtual int Version { get; protected set; }

    protected IndexProfile()
    {
    }

    public IndexProfile(
        Guid id,
        Guid? tenantId,
        Guid? documentTypeId,
        ChunkingStrategy chunkingStrategy,
        int maxTokensPerChunk,
        int overlapTokens,
        IndexEnrichment enrichment = IndexEnrichment.None,
        IndexCompilation compilation = IndexCompilation.None)
        : base(id)
    {
        TenantId = tenantId;
        DocumentTypeId = documentTypeId;
        ApplyChunking(chunkingStrategy, maxTokensPerChunk, overlapTokens);
        Enrichment = enrichment;
        Compilation = compilation;
        Version = 1;
    }

    /// <summary>Changes how documents are split, and moves <see cref="Version"/> when anything actually changed.</summary>
    public virtual void SetChunking(ChunkingStrategy chunkingStrategy, int maxTokensPerChunk, int overlapTokens)
    {
        if (chunkingStrategy == ChunkingStrategy && maxTokensPerChunk == MaxTokensPerChunk && overlapTokens == OverlapTokens)
        {
            return;
        }

        ApplyChunking(chunkingStrategy, maxTokensPerChunk, overlapTokens);
        Version++;
    }

    /// <summary>Changes what an LLM adds to each chunk, and moves <see cref="Version"/> when it actually changed.</summary>
    public virtual void SetEnrichment(IndexEnrichment enrichment)
    {
        if (enrichment == Enrichment)
        {
            return;
        }

        Enrichment = enrichment;
        Version++;
    }

    /// <summary>Changes what an LLM compiles from each document, and moves <see cref="Version"/> when it actually changed.</summary>
    public virtual void SetCompilation(IndexCompilation compilation)
    {
        if (compilation == Compilation)
        {
            return;
        }

        Compilation = compilation;
        Version++;
    }

    private void ApplyChunking(ChunkingStrategy chunkingStrategy, int maxTokensPerChunk, int overlapTokens)
    {
        Check.Range(maxTokensPerChunk, nameof(maxTokensPerChunk), 1, int.MaxValue);
        Check.Range(overlapTokens, nameof(overlapTokens), 0, maxTokensPerChunk - 1);

        ChunkingStrategy = chunkingStrategy;
        MaxTokensPerChunk = maxTokensPerChunk;
        OverlapTokens = overlapTokens;
    }
}
