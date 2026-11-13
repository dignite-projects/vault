using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Compilation;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Permissions;
using Microsoft.AspNetCore.Authorization;
using Dignite.Vault.Index.Search;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Index.Chunks;

/// <summary>
/// The chunk view. It shows chunk text, so a document is listed only when the caller's read scope reaches it, the rule
/// search uses; a document in the recycle bin or withheld is still listed, as on the status page. Disabling is Index's
/// own overlay: the mark goes on the chunk and on its vectors in the same transaction, and
/// <see cref="Ingestion.IndexDocumentJob"/> carries it to the rebuilt chunk with the same text. A changed mark changes
/// what the document's summaries are built from, so it queues their compilation.
/// </summary>
public class IndexChunkAppService : VaultIndexAppService, IIndexChunkAppService
{
    private readonly IChunkRepository _chunkRepository;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IChunkSearchIndex _searchIndex;
    private readonly IndexReadScopeResolver _scopeResolver;
    private readonly DocumentSummaryCompilerProvider _compilerProvider;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public IndexChunkAppService(
        IChunkRepository chunkRepository,
        IRepository<IndexedDocument, Guid> documentRepository,
        IChunkSearchIndex searchIndex,
        IndexReadScopeResolver scopeResolver,
        DocumentSummaryCompilerProvider compilerProvider,
        IBackgroundJobManager backgroundJobManager)
    {
        _chunkRepository = chunkRepository;
        _documentRepository = documentRepository;
        _searchIndex = searchIndex;
        _scopeResolver = scopeResolver;
        _compilerProvider = compilerProvider;
        _backgroundJobManager = backgroundJobManager;
    }

    public virtual async Task<IndexChunkListDto> GetListAsync(GetIndexChunkListInput input)
    {
        Check.NotNull(input, nameof(input));
        await AuthorizationService.CheckAsync(VaultIndexPermissions.Status);

        var types = await _scopeResolver.GetVisibleTypesAsync();
        var scope = await _scopeResolver.GetScopeAsync(null, types);
        var document = await AsyncExecuter.FirstOrDefaultAsync(
                           ChunkScopeFilter.ApplyReadScope(await _documentRepository.GetQueryableAsync(), scope)
                               .Where(d => d.Id == input.DocumentId))
                       ?? throw new EntityNotFoundException(typeof(IndexedDocument), input.DocumentId);

        // The chunks carry the document's type and owner too; filtering them as well keeps a chunk that has not caught
        // up with a reclassification from showing under the new grant.
        var chunks = ChunkScopeFilter.ApplyReadScope(await _chunkRepository.GetQueryableAsync(), scope)
            .Where(c => c.DocumentId == document.Id);
        var totalCount = await AsyncExecuter.CountAsync(chunks);
        var disabledCount = await AsyncExecuter.CountAsync(chunks.Where(c => c.IsDisabled));
        var page = await AsyncExecuter.ToListAsync(chunks
            .OrderBy(c => c.Ordinal)
            .Skip(input.SkipCount)
            .Take(Math.Clamp(input.MaxResultCount, 1, IndexSearchConsts.MaxChunkPageSize)));

        return new IndexChunkListDto
        {
            DocumentId = document.Id,
            DocumentTitle = document.Title,
            DocumentTypeCode = types.GetValueOrDefault(document.DocumentTypeId),
            Status = document.Status,
            IsHidden = document.IsHidden,
            DisabledCount = disabledCount,
            TotalCount = totalCount,
            Items = page.Select(c => new IndexChunkDto
            {
                Id = c.Id,
                Pointer = IndexPointer.ForChunk(c.Id).ToString(),
                Ordinal = c.Ordinal,
                HeadingPath = c.HeadingPath,
                Text = c.Text,
                IsDisabled = c.IsDisabled,
                Keywords = c.Keywords,
                Questions = c.Questions
            }).ToList()
        };
    }

    public virtual Task DisableAsync(Guid id) => SetDisabledAsync(id, true);

    public virtual Task EnableAsync(Guid id) => SetDisabledAsync(id, false);

    protected virtual async Task SetDisabledAsync(Guid id, bool isDisabled)
    {
        await AuthorizationService.CheckAsync(VaultIndexPermissions.StatusManageChunks);

        var types = await _scopeResolver.GetVisibleTypesAsync();
        var scope = await _scopeResolver.GetScopeAsync(null, types);
        var chunk = await AsyncExecuter.FirstOrDefaultAsync(
                        ChunkScopeFilter.ApplyReadScope(await _chunkRepository.GetQueryableAsync(), scope).Where(c => c.Id == id))
                    ?? throw new EntityNotFoundException(typeof(Chunk), id);
        if (chunk.IsDisabled == isDisabled)
        {
            return;
        }

        chunk.SetDisabled(isDisabled);
        await _chunkRepository.UpdateAsync(chunk);
        await _searchIndex.SetChunkDisabledAsync(chunk.Id, isDisabled);

        if (_compilerProvider.IsAvailable)
        {
            // The job compiles only when the document's profile asks for summaries.
            await _backgroundJobManager.EnqueueAsync(
                new IndexCompileJobArgs { DocumentId = chunk.DocumentId, TenantId = CurrentTenant.Id },
                BackgroundJobPriority.Low);
        }
    }
}
