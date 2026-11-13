using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.Linq;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Index.Compilation;

/// <summary>
/// Compiles a document's artifacts from its chunks: today its summaries, when its type's profile asks for them and the
/// host has Index's chat model. The index job queues it after every successful sync, and a chunk's disable or enable
/// queues it too; it compiles only when the source changed (or <see cref="IndexCompileJobArgs.Force"/>), so a queued
/// job for a document already compiled costs a read. It reads only Index's own rows, never Extract.
/// <para>
/// Like the index job, no transaction is held across model calls: read, compile, then write in one short unit of
/// work. A document re-chunked while it compiled gets summaries whose source hash no longer matches, which the outline
/// does not serve, and the sync that re-chunked it has queued the next compilation. When every model call fails the
/// job throws, so ABP retries it later; a partial result is kept, with a note.
/// </para>
/// </summary>
public class IndexCompileJob : AsyncBackgroundJob<IndexCompileJobArgs>, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IRepository<IndexedDocument, Guid> _documentRepository;
    private readonly IChunkRepository _chunkRepository;
    private readonly IRepository<Artifact, Guid> _artifactRepository;
    private readonly IndexProfileResolver _profileResolver;
    private readonly DocumentSummaryCompilerProvider _compilerProvider;
    private readonly IAsyncQueryableExecuter _asyncExecuter;
    private readonly IGuidGenerator _guidGenerator;

    public IndexCompileJob(
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IRepository<IndexedDocument, Guid> documentRepository,
        IChunkRepository chunkRepository,
        IRepository<Artifact, Guid> artifactRepository,
        IndexProfileResolver profileResolver,
        DocumentSummaryCompilerProvider compilerProvider,
        IAsyncQueryableExecuter asyncExecuter,
        IGuidGenerator guidGenerator)
    {
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _documentRepository = documentRepository;
        _chunkRepository = chunkRepository;
        _artifactRepository = artifactRepository;
        _profileResolver = profileResolver;
        _compilerProvider = compilerProvider;
        _asyncExecuter = asyncExecuter;
        _guidGenerator = guidGenerator;
    }

    public override async Task ExecuteAsync(IndexCompileJobArgs args)
    {
        using (_currentTenant.Change(args.TenantId))
        {
            var source = await ReadSourceAsync(args.DocumentId);
            if (source is null)
            {
                return;
            }

            if (!source.Compilation.HasFlag(IndexCompilation.Summaries) || source.Chunks.Count == 0)
            {
                // The profile no longer asks for summaries: drop them, so turning them off and rebuilding removes them.
                if (source.HasSummaries)
                {
                    await DropSummariesAsync(args.DocumentId);
                }

                return;
            }

            var fresh = source.SummariesHash == source.SourceHash && source.SummariesVersion == DocumentSummaries.CurrentVersion;
            if (fresh && !args.Force)
            {
                if (source.SummariesTypeId != source.DocumentTypeId)
                {
                    // Reclassified with the same text: the summaries stand, and move with their document.
                    await MoveSummariesAsync(args.DocumentId);
                }

                return;
            }

            var compiler = _compilerProvider.Create(source.Language);
            if (compiler is null)
            {
                return;
            }

            var compilation = await compiler.CompileAsync(source.Title, source.Chunks);
            if (compilation.Failed)
            {
                throw new AbpException(
                    $"Compiling the summaries of document {args.DocumentId} failed: all {compilation.CallCount} model calls failed.");
            }

            await WriteSummariesAsync(args.DocumentId, source.SourceHash, compilation);
            Logger.LogInformation(
                "Compiled the summaries of document {DocumentId}: {Sections} sections, {Calls} calls, {InputTokens} input and {OutputTokens} output tokens.",
                args.DocumentId, compilation.Summaries.Sections.Count, compilation.CallCount, compilation.InputTokenCount, compilation.OutputTokenCount);
        }
    }

    /// <summary>
    /// The document's enabled chunks, as the outline sees them, and the summaries it has. <c>null</c> when there is
    /// nothing to compile: the document is gone, was never indexed, or is out of retrieval.
    /// </summary>
    protected virtual async Task<CompileSource?> ReadSourceAsync(Guid documentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: false);
        var row = await _documentRepository.FindAsync(documentId);
        if (row is null || row.ContentHash is null || row.IsHidden)
        {
            await uow.CompleteAsync();
            return null;
        }

        var profile = await _profileResolver.ResolveAsync(row.DocumentTypeId);
        var chunks = await _asyncExecuter.ToListAsync((await _chunkRepository.GetQueryableAsync())
            .Where(c => c.DocumentId == documentId && !c.IsDisabled)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Ordinal, c.HeadingPath, c.Text, c.ContentHash }));
        var summaries = await FindSummariesAsync(documentId);
        await uow.CompleteAsync();

        return new CompileSource(
            row.DocumentTypeId,
            row.Title,
            row.Language,
            profile.Compilation,
            chunks.Select(c => new CompileChunk(c.Ordinal, c.HeadingPath, c.Text)).ToList(),
            DocumentSummaries.SourceHash(row.Language, chunks.Select(c => (c.Ordinal, c.HeadingPath, c.ContentHash))),
            summaries is not null,
            summaries?.DocumentTypeId,
            summaries?.SourceHash,
            summaries is null ? null : DocumentSummaries.FromJson(summaries.Content)?.Version);
    }

    protected virtual async Task WriteSummariesAsync(Guid documentId, string sourceHash, SummaryCompilation compilation)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        var row = await _documentRepository.FindAsync(documentId);
        if (row is null)
        {
            // Removed while this job compiled: write nothing, or the summaries of a deleted document would survive it.
            await uow.CompleteAsync();
            return;
        }

        var content = compilation.Summaries.ToJson();
        var artifact = await FindSummariesAsync(documentId);
        if (artifact is null)
        {
            artifact = new Artifact(
                _guidGenerator.Create(), _currentTenant.Id, ArtifactLevel.Document, ArtifactTemplateNames.Summaries,
                row.DocumentTypeId, documentId, content, sourceHash);
            artifact.Recompile(content, sourceHash, compilation.InputTokenCount, compilation.OutputTokenCount, compilation.Note);
            await _artifactRepository.InsertAsync(artifact);
        }
        else
        {
            artifact.Recompile(content, sourceHash, compilation.InputTokenCount, compilation.OutputTokenCount, compilation.Note);
            artifact.SetDocumentType(row.DocumentTypeId);
            await _artifactRepository.UpdateAsync(artifact);
        }

        await uow.CompleteAsync();
    }

    protected virtual async Task MoveSummariesAsync(Guid documentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        var row = await _documentRepository.FindAsync(documentId);
        var artifact = await FindSummariesAsync(documentId);
        if (row is not null && artifact is not null)
        {
            artifact.SetDocumentType(row.DocumentTypeId);
            await _artifactRepository.UpdateAsync(artifact);
        }

        await uow.CompleteAsync();
    }

    protected virtual async Task DropSummariesAsync(Guid documentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        await _artifactRepository.DeleteDirectAsync(a => a.DocumentId == documentId && a.TemplateName == ArtifactTemplateNames.Summaries);
        await uow.CompleteAsync();
    }

    protected virtual Task<Artifact?> FindSummariesAsync(Guid documentId)
        => _artifactRepository.FirstOrDefaultAsync(a => a.DocumentId == documentId && a.TemplateName == ArtifactTemplateNames.Summaries);

    /// <summary>What a compilation reads, and the summaries the document has (their hash and version).</summary>
    protected sealed record CompileSource(
        Guid DocumentTypeId,
        string? Title,
        string? Language,
        IndexCompilation Compilation,
        IReadOnlyList<CompileChunk> Chunks,
        string SourceHash,
        bool HasSummaries,
        Guid? SummariesTypeId,
        string? SummariesHash,
        int? SummariesVersion);
}
