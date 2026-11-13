using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Compilation;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Ingestion;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Runs;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Index.Profiles;

/// <summary>
/// The index profile page. Profiles are per layer, like Extract's types: the ambient tenant's rows only. The page shows
/// counts and settings, never document content, so it is not narrowed by a read scope; changing a profile needs
/// <see cref="VaultIndexPermissions.Profiles"/>. A change only queues work: the rebuild walks the affected documents in
/// the index at low priority, and each sync re-chunks a document only when its settings stamp differs, the same check
/// every sync makes.
/// </summary>
public class IndexProfileAppService : VaultIndexAppService, IIndexProfileAppService
{
    private readonly IRepository<IndexProfile, Guid> _profileRepository;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IRepository<IndexRun, Guid> _runRepository;
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IndexEnricherProvider _enricherProvider;
    private readonly DocumentSummaryCompilerProvider _compilerProvider;
    private readonly IRepository<Artifact, Guid> _artifactRepository;

    public IndexProfileAppService(
        IRepository<IndexProfile, Guid> profileRepository,
        IRepository<IndexedDocument, Guid> documentRepository,
        IRepository<IndexRun, Guid> runRepository,
        IDocumentTypeAppService documentTypeAppService,
        IBackgroundJobManager backgroundJobManager,
        IndexEnricherProvider enricherProvider,
        DocumentSummaryCompilerProvider compilerProvider,
        IRepository<Artifact, Guid> artifactRepository)
    {
        _profileRepository = profileRepository;
        _documentRepository = documentRepository;
        _runRepository = runRepository;
        _documentTypeAppService = documentTypeAppService;
        _backgroundJobManager = backgroundJobManager;
        _enricherProvider = enricherProvider;
        _compilerProvider = compilerProvider;
        _artifactRepository = artifactRepository;
    }

    public virtual async Task<IndexProfileListDto> GetListAsync()
    {
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Profiles);

        var profiles = await _profileRepository.GetListAsync();
        var layerDefault = profiles.FirstOrDefault(p => p.DocumentTypeId == null);
        var counts = (await AsyncExecuter.ToListAsync((await _documentRepository.GetQueryableAsync())
                .GroupBy(d => d.DocumentTypeId)
                .Select(g => new { TypeId = g.Key, Count = g.Count() })))
            .ToDictionary(x => x.TypeId, x => x.Count);

        var rows = new List<IndexProfileDto> { ToDto(null, null, null, layerDefault, null, counts.Values.Sum()) };
        foreach (var type in (await _documentTypeAppService.GetVisibleSummariesAsync()).OrderBy(t => t.TypeCode, StringComparer.Ordinal))
        {
            var own = profiles.FirstOrDefault(p => p.DocumentTypeId == type.Id);
            rows.Add(ToDto(type.Id, type.TypeCode, type.DisplayName, own, layerDefault, counts.GetValueOrDefault(type.Id)));
        }

        return new IndexProfileListDto { Items = rows, ChatModelAvailable = _compilerProvider.IsAvailable };
    }

    public virtual async Task<IndexRebuildEstimateDto> GetRebuildEstimateAsync(IndexProfileInput input)
    {
        Check.NotNull(input, nameof(input));
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Profiles);
        await CheckTypeAsync(input.DocumentTypeId);

        // The stamp of the settings as they would apply: enrichment counts only when there is a model to run it.
        var enrichment = _enricherProvider.Effective(input.Enrichment);
        var stamp = new ResolvedIndexProfile(input.ChunkingStrategy, input.MaxTokensPerChunk, input.OverlapTokens, enrichment).Stamp;
        var applying = await GetApplyingAsync(input.DocumentTypeId);
        var estimate = await EstimateAsync(applying.Where(d => d.ProfileStamp != stamp), enrichment);
        if (_compilerProvider.Effective(input.Compilation).HasFlag(IndexCompilation.Summaries))
        {
            var compiled = (await _artifactRepository.GetQueryableAsync())
                .Where(a => a.TemplateName == ArtifactTemplateNames.Summaries && a.DocumentId != null)
                .Select(a => a.DocumentId!.Value);
            var toCompile = applying.Where(d => d.ProfileStamp != stamp || !compiled.Contains(d.Id));
            estimate.SummaryDocumentCount = await AsyncExecuter.CountAsync(toCompile);
            estimate.SummaryCallCount = await AsyncExecuter.SumAsync(toCompile.Select(d =>
                (d.ChunkCount + DocumentSummaryCompiler.ItemsPerCall - 1) / DocumentSummaryCompiler.ItemsPerCall + 2));
        }

        return estimate;
    }

    public virtual async Task<IndexProfileDto> SetAsync(IndexProfileInput input)
    {
        Check.NotNull(input, nameof(input));
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Profiles);
        var type = await CheckTypeAsync(input.DocumentTypeId);

        var profile = await _profileRepository.FirstOrDefaultAsync(p => p.DocumentTypeId == input.DocumentTypeId);
        if (profile is null)
        {
            profile = new IndexProfile(
                GuidGenerator.Create(), CurrentTenant.Id, input.DocumentTypeId,
                input.ChunkingStrategy, input.MaxTokensPerChunk, input.OverlapTokens, input.Enrichment, input.Compilation);
            await _profileRepository.InsertAsync(profile);
        }
        else
        {
            profile.SetChunking(input.ChunkingStrategy, input.MaxTokensPerChunk, input.OverlapTokens);
            profile.SetEnrichment(input.Enrichment);
            profile.SetCompilation(input.Compilation);
            await _profileRepository.UpdateAsync(profile);
        }

        await QueueRebuildAsync(input.DocumentTypeId);

        var layerDefault = input.DocumentTypeId is null
            ? null
            : await _profileRepository.FirstOrDefaultAsync(p => p.DocumentTypeId == null);
        var count = await AsyncExecuter.CountAsync((await _documentRepository.GetQueryableAsync())
            .Where(d => input.DocumentTypeId == null || d.DocumentTypeId == input.DocumentTypeId));
        return ToDto(input.DocumentTypeId, type?.TypeCode, type?.DisplayName, profile, layerDefault, count);
    }

    public virtual async Task ResetAsync(Guid documentTypeId)
    {
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Profiles);
        await CheckTypeAsync(documentTypeId);

        var profile = await _profileRepository.FirstOrDefaultAsync(p => p.DocumentTypeId == documentTypeId);
        if (profile is null)
        {
            return;
        }

        await _profileRepository.DeleteAsync(profile);
        await QueueRebuildAsync(documentTypeId);
    }

    /// <summary>
    /// The indexed documents a profile applies to: the type's, or for the layer default those of every type without
    /// its own profile.
    /// </summary>
    protected virtual async Task<IQueryable<IndexedDocument>> GetApplyingAsync(Guid? documentTypeId)
    {
        var query = await _documentRepository.GetQueryableAsync();
        if (documentTypeId is { } typeId)
        {
            return query.Where(d => d.DocumentTypeId == typeId);
        }

        var ownProfiles = (await _profileRepository.GetListAsync(p => p.DocumentTypeId != null)).Select(p => p.DocumentTypeId!.Value).ToList();
        return query.Where(d => !ownProfiles.Contains(d.DocumentTypeId));
    }

    protected virtual async Task<IndexRebuildEstimateDto> EstimateAsync(IQueryable<IndexedDocument> affected, IndexEnrichment enrichment)
    {
        var ids = affected.Select(d => d.Id);
        var runs = (await _runRepository.GetQueryableAsync())
            .Where(r => r.Status == IndexRunStatus.Succeeded && r.EmbeddingTokenCount != null && ids.Contains(r.DocumentId));
        var latest = runs
            .GroupBy(r => r.DocumentId)
            .Select(g => new { DocumentId = g.Key, StartedTime = g.Max(r => r.StartedTime) });
        var lastTokens = from r in runs
                         join l in latest on new { r.DocumentId, r.StartedTime } equals new { l.DocumentId, l.StartedTime }
                         select new { r.DocumentId, Tokens = r.EmbeddingTokenCount!.Value };
        var tokens = await AsyncExecuter.ToListAsync(lastTokens);
        var perDocument = tokens.GroupBy(t => t.DocumentId).ToDictionary(g => g.Key, g => g.Max(t => t.Tokens));

        var documentCount = await AsyncExecuter.CountAsync(affected);
        return new IndexRebuildEstimateDto
        {
            DocumentCount = documentCount,
            ChunkCount = await AsyncExecuter.SumAsync(affected.Select(d => (long)d.ChunkCount)),
            EmbeddingTokenCount = perDocument.Values.Sum(),
            DocumentsWithoutTokenCount = documentCount - perDocument.Count,
            EnrichmentCallCount = enrichment == IndexEnrichment.None
                ? 0
                : await AsyncExecuter.SumAsync(affected.Select(d => (d.ChunkCount + IndexChunkEnricher.BatchSize - 1) / IndexChunkEnricher.BatchSize))
        };
    }

    /// <summary>Walks the affected documents in the index at low priority; each sync decides on its own stamp.</summary>
    protected virtual Task QueueRebuildAsync(Guid? documentTypeId)
        => _backgroundJobManager.EnqueueAsync(
            new IndexBackfillJobArgs { TenantId = CurrentTenant.Id, IndexedOnly = true, DocumentTypeId = documentTypeId },
            BackgroundJobPriority.Low);

    /// <summary>The type, when one is named: it must be a type the caller can see in this layer.</summary>
    protected virtual async Task<DocumentTypeSummaryDto?> CheckTypeAsync(Guid? documentTypeId)
    {
        if (documentTypeId is not { } typeId)
        {
            return null;
        }

        return (await _documentTypeAppService.GetVisibleSummariesAsync()).FirstOrDefault(t => t.Id == typeId)
               ?? throw new EntityNotFoundException(typeof(IndexProfile), typeId);
    }

    protected virtual IndexProfileDto ToDto(
        Guid? documentTypeId,
        string? typeCode,
        string? typeName,
        IndexProfile? own,
        IndexProfile? layerDefault,
        int indexedDocumentCount)
    {
        var source = own ?? layerDefault;
        var applied = source is not null
            ? new ResolvedIndexProfile(source.ChunkingStrategy, source.MaxTokensPerChunk, source.OverlapTokens, source.Enrichment, source.Compilation)
            : ResolvedIndexProfile.BuiltIn;

        return new IndexProfileDto
        {
            DocumentTypeId = documentTypeId,
            DocumentTypeCode = typeCode,
            DocumentTypeName = typeName,
            HasOwnProfile = own is not null,
            ChunkingStrategy = applied.Strategy,
            MaxTokensPerChunk = applied.MaxTokensPerChunk,
            OverlapTokens = applied.OverlapTokens,
            Enrichment = applied.Enrichment,
            Compilation = applied.Compilation,
            IndexedDocumentCount = indexedDocumentCount
        };
    }
}
