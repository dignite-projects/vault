using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Search;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// The default <see cref="IChunkSearchIndex"/>: vectors in <c>VaultIndexChunkVectors</c>, written through EF Core in
/// the caller's unit of work, searched by exact cosine distance; keywords through SQL Server full-text search on the
/// chunk table. Every query goes through a DbSet, so ABP's tenant filter applies, and through
/// <see cref="ChunkScopeFilter"/>, so the read scope does.
/// </summary>
public class SqlServerChunkSearchIndex : IChunkSearchIndex, ITransientDependency
{
    /// <summary>Whether a database has the chunk full-text index, by connection string. Created by a migration, so it does not come and go.</summary>
    private static readonly ConcurrentDictionary<string, bool> FullTextIndexByDatabase = new();

    private readonly IDbContextProvider<VaultIndexSqlServerDbContext> _dbContextProvider;
    private readonly IDbContextProvider<IVaultIndexDbContext> _indexDbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly VaultIndexSqlServerOptions _options;

    public ILogger<SqlServerChunkSearchIndex> Logger { get; set; } = NullLogger<SqlServerChunkSearchIndex>.Instance;

    public SqlServerChunkSearchIndex(
        IDbContextProvider<VaultIndexSqlServerDbContext> dbContextProvider,
        IDbContextProvider<IVaultIndexDbContext> indexDbContextProvider,
        ICurrentTenant currentTenant,
        IOptions<VaultIndexSqlServerOptions> options)
    {
        _dbContextProvider = dbContextProvider;
        _indexDbContextProvider = indexDbContextProvider;
        _currentTenant = currentTenant;
        _options = options.Value;
    }

    public virtual int? VectorDimensions => _options.VectorDimensions;

    public virtual async Task ReplaceDocumentAsync(
        Guid documentId,
        Guid generationId,
        IReadOnlyList<ChunkVector> vectors,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();

        await dbContext.ChunkVectors
            .Where(v => v.DocumentId == documentId && v.GenerationId == generationId)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var vector in vectors)
        {
            CheckDimensions(vector.Vector.Length);
            dbContext.ChunkVectors.Add(new ChunkVectorRecord
            {
                GenerationId = generationId,
                ChunkId = vector.ChunkId,
                TenantId = _currentTenant.Id,
                DocumentId = vector.DocumentId,
                DocumentTypeId = vector.DocumentTypeId,
                OwnerId = vector.OwnerId,
                IsHidden = vector.IsHidden,
                Embedding = new SqlVector<float>(vector.Vector)
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public virtual async Task UpdateDocumentFiltersAsync(
        Guid documentId,
        Guid documentTypeId,
        Guid? ownerId,
        bool isHidden,
        CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        await dbContext.ChunkVectors
            .Where(v => v.DocumentId == documentId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(v => v.DocumentTypeId, documentTypeId)
                    .SetProperty(v => v.OwnerId, ownerId)
                    .SetProperty(v => v.IsHidden, isHidden),
                cancellationToken);
    }

    public virtual async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        await dbContext.ChunkVectors.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken);
    }

    public virtual async Task<IReadOnlyList<ChunkMatch>> SearchVectorsAsync(
        Guid generationId,
        ReadOnlyMemory<float> queryVector,
        ChunkSearchScope scope,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ReachesNothing || take <= 0)
        {
            return [];
        }

        CheckDimensions(queryVector.Length);
        var query = new SqlVector<float>(queryVector);
        var dbContext = await _dbContextProvider.GetDbContextAsync();

        // Exact search: the storage PoC found SQL Server 2025's own vector index unusable for a table that is written
        // to and filtered, and exact distance over the rows the scope leaves is fast enough up to about 50,000 of them.
        var rows = await ChunkScopeFilter.Apply(dbContext.ChunkVectors.Where(v => v.GenerationId == generationId), scope)
            .Select(v => new { v.ChunkId, v.DocumentId, Distance = EF.Functions.VectorDistance("cosine", v.Embedding, query) })
            .OrderBy(x => x.Distance)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new ChunkMatch(r.ChunkId, r.DocumentId, r.Distance)).ToList();
    }

    public virtual async Task<IReadOnlyList<ChunkMatch>> SearchKeywordsAsync(
        string query,
        ChunkSearchScope scope,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ReachesNothing || take <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var indexDbContext = await _indexDbContextProvider.GetDbContextAsync();
        var dbContext = (DbContext)indexDbContext;
        var table = QualifiedChunkTable(dbContext);
        if (!await HasFullTextIndexAsync(dbContext, table, cancellationToken))
        {
            return [];
        }

        // EF Core 10 does not translate FREETEXTTABLE, so the ranked keys come from SqlQuery and are joined in LINQ to the
        // chunk DbSet: the join keeps the tenant filter and the read scope in the generated SQL (storage PoC). The
        // query text is a parameter; the table name comes from the EF model. No TOP n inside FREETEXTTABLE: its ranking
        // spans every tenant, so a top-n cut there could leave this tenant with nothing.
        // The table name is the EF model's, quoted; only the query text comes from the caller, and it is a parameter.
        var sql = "SELECT [KEY] AS [ChunkId], [RANK] AS [Rank] FROM FREETEXTTABLE(" + table + ", [Text], @query)";
        var hits = dbContext.Database.SqlQueryRaw<FullTextHit>(sql, new SqlParameter("@query", query));

        var rows = await (
                from h in hits
                join c in ChunkScopeFilter.Apply(indexDbContext.Chunks, scope) on h.ChunkId equals c.Id
                orderby h.Rank descending, c.Id
                select new { c.Id, c.DocumentId, h.Rank })
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new ChunkMatch(r.Id, r.DocumentId, r.Rank)).ToList();
    }

    public virtual async Task<ChunkSearchIndexStatus> GetStatusAsync(Guid? generationId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var vectorCount = generationId is { } id
            ? await dbContext.ChunkVectors.LongCountAsync(v => v.GenerationId == id, cancellationToken)
            : 0;

        // Read fresh, not from the search's cache: this is where an operator checks that an install worked.
        var indexDbContext = (DbContext)await _indexDbContextProvider.GetDbContextAsync();
        var table = QualifiedChunkTable(indexDbContext);
        var state = await indexDbContext.Database
            .SqlQueryRaw<FullTextState>(
                """
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(@table) AND is_enabled = 1) THEN 1 ELSE 0 END AS bit) AS [Available],
                       CAST(OBJECTPROPERTYEX(OBJECT_ID(@table), 'TableFulltextPendingChanges') AS bigint) AS [PendingChanges],
                       CAST(OBJECTPROPERTYEX(OBJECT_ID(@table), 'TableFulltextPopulateStatus') AS int) AS [PopulateStatus]
                """,
                new SqlParameter("@table", table))
            .SingleAsync(cancellationToken);

        if (state.Available)
        {
            FullTextIndexByDatabase[indexDbContext.Database.GetConnectionString() ?? string.Empty] = true;
        }

        return new ChunkSearchIndexStatus(
            vectorCount,
            state.Available,
            state.Available ? state.PendingChanges : null,
            state.Available ? state.PopulateStatus is 1 or 2 : null);
    }

    protected virtual void CheckDimensions(int dimensions)
    {
        if (dimensions != _options.VectorDimensions)
        {
            throw new BusinessException(VaultIndexErrorCodes.EmbeddingDimensionMismatch)
                .WithData("Dimensions", dimensions)
                .WithData("StorageDimensions", _options.VectorDimensions);
        }
    }

    /// <summary>
    /// Whether the chunk table has a full-text index. It does not where Full-Text Search is not installed (LocalDB,
    /// the plain SQL Server container image): the migration skips it there, and the keyword leg returns nothing.
    /// </summary>
    protected virtual async Task<bool> HasFullTextIndexAsync(DbContext dbContext, string table, CancellationToken cancellationToken)
    {
        var key = dbContext.Database.GetConnectionString() ?? string.Empty;
        if (FullTextIndexByDatabase.TryGetValue(key, out var known))
        {
            return known;
        }

        var exists = await dbContext.Database
            .SqlQueryRaw<bool>(
                "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(@table) AND is_enabled = 1) THEN 1 ELSE 0 END AS bit) AS [Value]",
                new SqlParameter("@table", table))
            .SingleAsync(cancellationToken);

        if (!exists)
        {
            Logger.LogWarning(
                "Table {Table} has no full-text index, so Vault Index searches by vectors only. Install Full-Text Search on the SQL Server behind the VaultIndex connection string and re-run the Index migrations.",
                table);
        }

        FullTextIndexByDatabase[key] = exists;
        return exists;
    }

    private static string QualifiedChunkTable(DbContext dbContext)
    {
        var entityType = dbContext.Model.FindEntityType(typeof(Chunk))!;
        var schema = entityType.GetSchema();
        var name = "[" + entityType.GetTableName()!.Replace("]", "]]") + "]";
        return schema is null ? name : "[" + schema.Replace("]", "]]") + "]." + name;
    }

    /// <summary>
    /// The full-text index's state. <c>TableFulltextPopulateStatus</c> 1 and 2 are a full and an incremental population
    /// (after a backfill); 3 and 4 are the routine absorption of tracked changes, which the pending count covers.
    /// </summary>
    public class FullTextState
    {
        public bool Available { get; set; }

        public long? PendingChanges { get; set; }

        public int? PopulateStatus { get; set; }
    }

    /// <summary>One row of <c>FREETEXTTABLE</c>.</summary>
    public class FullTextHit
    {
        public Guid ChunkId { get; set; }

        public int Rank { get; set; }
    }
}
