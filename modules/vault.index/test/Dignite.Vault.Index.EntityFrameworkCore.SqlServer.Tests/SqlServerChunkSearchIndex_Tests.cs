using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Search;
using Microsoft.Data.SqlClient;
using Shouldly;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// The default store's two retrieval legs on a real SQL Server 2025: exact cosine distance and Japanese full-text
/// ranking, each confined to the caller's read scope, the ambient tenant, the requested generation and the documents
/// in retrieval. Removing the tenant filter or any scope condition from either leg turns these red.
/// </summary>
public class SqlServerChunkSearchIndex_Tests : VaultIndexTestBase<VaultIndexSqlServerTestModule>
{
    private static readonly Guid TypeA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid TypeB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid OwnerX = Guid.Parse("0e000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OwnerY = Guid.Parse("0e000000-0000-0000-0000-0000000000b0");
    private static readonly Guid OtherTenant = Guid.Parse("7e000000-0000-0000-0000-000000000001");
    private static readonly Guid Generation1 = Guid.Parse("9e000000-0000-0000-0000-000000000001");
    private static readonly Guid Generation2 = Guid.Parse("9e000000-0000-0000-0000-000000000002");

    private static readonly ChunkSearchScope Everything = new(true, [], null);

    [SqlServerFact]
    public async Task The_vector_leg_ranks_by_cosine_within_scope_tenant_and_generation()
    {
        await ClearAsync();
        var a = await AddAsync(TypeA, OwnerX, "a", [1, 0, 0, 0]);
        var b = await AddAsync(TypeB, OwnerY, "b", [0.8f, 0.6f, 0, 0]);
        await AddAsync(TypeA, OwnerX, "hidden", [1, 0, 0, 0], isHidden: true);
        await AddAsync(TypeA, OwnerX, "other tenant", [1, 0, 0, 0], tenantId: OtherTenant);
        await AddVectorAsync(a, Generation2, [0, 0, 0, 1]);

        (await VectorsAsync(Generation1, Everything)).ShouldBe([a.ChunkId, b.ChunkId]);
        (await VectorsAsync(Generation1, new ChunkSearchScope(false, [TypeB], null))).ShouldBe([b.ChunkId]);
        (await VectorsAsync(Generation1, new ChunkSearchScope(false, [], OwnerX))).ShouldBe([a.ChunkId]);
        (await VectorsAsync(Generation1, new ChunkSearchScope(true, [], null, [TypeA]))).ShouldBe([a.ChunkId]);
        (await VectorsAsync(Generation1, new ChunkSearchScope(false, [], null))).ShouldBeEmpty();
        (await VectorsAsync(Generation2, Everything)).ShouldBe([a.ChunkId]);

        using (GetRequiredService<ICurrentTenant>().Change(OtherTenant))
        {
            (await VectorsAsync(Generation1, Everything)).Count.ShouldBe(1);
        }
    }

    [SqlServerFact]
    public async Task The_keyword_leg_ranks_full_text_within_scope_and_tenant()
    {
        await ClearAsync();
        var rent = await AddAsync(TypeA, OwnerX, "賃料は月額十八万五千円とし、毎月末日までに支払う。", [1, 0, 0, 0]);
        var deposit = await AddAsync(TypeB, OwnerY, "敷金は賃料の二か月分とする。", [0, 1, 0, 0]);
        await AddAsync(TypeA, OwnerX, "賃料の改定は協議による。", [1, 0, 0, 0], isHidden: true);
        await AddAsync(TypeA, OwnerX, "賃料は当月払いとする。", [1, 0, 0, 0], tenantId: OtherTenant);
        await WaitForFullTextAsync();

        (await KeywordsAsync("賃料", Everything)).ShouldBe([rent.ChunkId, deposit.ChunkId], ignoreOrder: true);
        (await KeywordsAsync("賃料", new ChunkSearchScope(false, [TypeB], null))).ShouldBe([deposit.ChunkId]);
        (await KeywordsAsync("賃料", new ChunkSearchScope(false, [], OwnerX))).ShouldBe([rent.ChunkId]);
        (await KeywordsAsync("敷金", new ChunkSearchScope(false, [TypeA], null))).ShouldBeEmpty();
        (await KeywordsAsync("駐車場", Everything)).ShouldBeEmpty();

        using (GetRequiredService<ICurrentTenant>().Change(OtherTenant))
        {
            (await KeywordsAsync("賃料", Everything)).Count.ShouldBe(1);
        }
    }

    [SqlServerFact]
    public async Task Hiding_a_document_takes_it_out_of_both_legs()
    {
        await ClearAsync();
        var rent = await AddAsync(TypeA, OwnerX, "賃料は月額十八万五千円とする。", [1, 0, 0, 0]);
        await WaitForFullTextAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            await GetRequiredService<IChunkRepository>().UpdateDocumentFiltersAsync(rent.DocumentId, TypeA, OwnerX, isHidden: true);
            await GetRequiredService<IChunkSearchIndex>().UpdateDocumentFiltersAsync(rent.DocumentId, TypeA, OwnerX, isHidden: true);
        });

        (await VectorsAsync(Generation1, Everything)).ShouldBeEmpty();
        (await KeywordsAsync("賃料", Everything)).ShouldBeEmpty();
    }

    private Task<List<Guid>> VectorsAsync(Guid generationId, ChunkSearchScope scope)
        => WithUnitOfWorkAsync(async () => (await GetRequiredService<IChunkSearchIndex>()
            .SearchVectorsAsync(generationId, new float[] { 1, 0, 0, 0 }, scope, 10)).Select(m => m.ChunkId).ToList());

    private Task<List<Guid>> KeywordsAsync(string query, ChunkSearchScope scope)
        => WithUnitOfWorkAsync(async () => (await GetRequiredService<IChunkSearchIndex>()
            .SearchKeywordsAsync(query, scope, 10)).Select(m => m.ChunkId).ToList());

    /// <summary>
    /// The full-text index follows writes a few seconds late (about 4.6 s in the storage PoC). Waits until it has no
    /// pending change and holds every row, so the rows a test expects to be excluded are indexed too, not just absent.
    /// </summary>
    private static async Task WaitForFullTextAsync()
    {
        const string sql = """
            SELECT CASE WHEN OBJECTPROPERTYEX(OBJECT_ID('VaultIndexChunks'), 'TableFulltextPendingChanges') = 0
                          AND OBJECTPROPERTYEX(OBJECT_ID('VaultIndexChunks'), 'TableFulltextItemCount') = (SELECT COUNT(*) FROM VaultIndexChunks)
                        THEN 1 ELSE 0 END
            """;
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        for (var i = 0; i < 120; i++)
        {
            await using var command = new SqlCommand(sql, connection);
            if ((int)(await command.ExecuteScalarAsync())! == 1)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("The full-text index did not catch up in 60 seconds.");
    }

    private async Task<ChunkVector> AddAsync(Guid typeId, Guid ownerId, string text, float[] vector, bool isHidden = false, Guid? tenantId = null)
    {
        var chunk = new ChunkVector(Guid.NewGuid(), Guid.NewGuid(), typeId, ownerId, isHidden, vector);
        using (GetRequiredService<ICurrentTenant>().Change(tenantId))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                await GetRequiredService<IChunkRepository>().InsertAsync(new Chunk(
                    chunk.ChunkId, tenantId, chunk.DocumentId, typeId, ownerId, 0, null, text, new string('0', 64), isHidden));
                await GetRequiredService<IChunkSearchIndex>().ReplaceDocumentAsync(chunk.DocumentId, Generation1, [chunk]);
            });
        }

        return chunk;
    }

    private Task AddVectorAsync(ChunkVector chunk, Guid generationId, float[] vector)
        => WithUnitOfWorkAsync(() => GetRequiredService<IChunkSearchIndex>()
            .ReplaceDocumentAsync(chunk.DocumentId, generationId, [chunk with { Vector = vector }]));

    private static async Task ClearAsync()
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM VaultIndexChunkVectors; DELETE FROM VaultIndexChunks;", connection);
        await command.ExecuteNonQueryAsync();
    }
}
