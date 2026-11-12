using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Integration;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Ingestion;
using Shouldly;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// The two red lines of the design (section 10). One: Extract's integration service, which reads documents with no
/// user and no read rule, is for ingestion only; a retrieval path that used it would bypass the read rule. Two: the
/// read-scope condition is built in one place, <see cref="ChunkScopeFilter"/>, and dropping any part of it, or the
/// tenant filter beside it, turns these tests red.
/// </summary>
public class SearchBoundary_Tests : VaultIndexTestBase<VaultIndexIngestionTestModule>
{
    private static readonly Guid TypeA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid TypeB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Owner = Guid.Parse("0e000000-0000-0000-0000-0000000000e1");
    private static readonly Guid OtherTenant = Guid.Parse("7e000000-0000-0000-0000-000000000001");

    [Fact]
    public void Only_ingestion_reads_through_the_integration_service()
    {
        var offenders = typeof(VaultIndexApplicationModule).Assembly.GetTypes()
            .Concat(typeof(VaultIndexDomainModule).Assembly.GetTypes())
            .Where(t => t.Namespace != typeof(IndexDocumentJob).Namespace)
            .Where(DependsOnIntegrationService)
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty();

        // The rule is not vacuous: ingestion does depend on it.
        DependsOnIntegrationService(typeof(IndexDocumentJob)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_scope_filter_reaches_exactly_the_read_rule()
    {
        var hidden = Guid.NewGuid();
        var a = await InsertChunkAsync(TypeA, ownerId: null);
        var bOwned = await InsertChunkAsync(TypeB, Owner);
        var bOther = await InsertChunkAsync(TypeB, ownerId: Guid.NewGuid());
        await InsertChunkAsync(TypeA, Owner, isHidden: true, id: hidden);

        (await ReachAsync(new ChunkSearchScope(true, [], null))).ShouldBe([a, bOwned, bOther], ignoreOrder: true);
        (await ReachAsync(new ChunkSearchScope(false, [TypeA], null))).ShouldBe([a]);
        (await ReachAsync(new ChunkSearchScope(false, [], Owner))).ShouldBe([bOwned]);
        (await ReachAsync(new ChunkSearchScope(false, [TypeA], Owner))).ShouldBe([a, bOwned], ignoreOrder: true);
        (await ReachAsync(new ChunkSearchScope(false, [], null))).ShouldBeEmpty();
        (await ReachAsync(new ChunkSearchScope(true, [], null, requestedTypeIds: [TypeB]))).ShouldBe([bOwned, bOther], ignoreOrder: true);
        (await ReachAsync(new ChunkSearchScope(false, [TypeA], Owner, requestedTypeIds: [TypeB]))).ShouldBe([bOwned]);
        (await ReachAsync(new ChunkSearchScope(true, [], null, requestedTypeIds: []))).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_in_memory_rule_agrees_with_the_query()
    {
        await InsertChunkAsync(TypeA, null);
        await InsertChunkAsync(TypeB, Owner);
        await InsertChunkAsync(TypeB, Guid.NewGuid());
        await InsertChunkAsync(TypeA, Owner, isHidden: true);
        var scopes = new[]
        {
            new ChunkSearchScope(true, [], null),
            new ChunkSearchScope(false, [TypeA], null),
            new ChunkSearchScope(false, [], Owner),
            new ChunkSearchScope(false, [TypeA], Owner, [TypeB]),
            new ChunkSearchScope(false, [], null)
        };

        var all = await WithUnitOfWorkAsync(() => GetRequiredService<IChunkRepository>().GetListAsync());
        foreach (var scope in scopes)
        {
            (await ReachAsync(scope)).ShouldBe(all.Where(scope.Reaches).Select(c => c.Id), ignoreOrder: true);
        }
    }

    [Fact]
    public async Task Another_tenants_chunks_are_out_of_reach()
    {
        var mine = await InsertChunkAsync(TypeA, null);
        using (GetRequiredService<ICurrentTenant>().Change(OtherTenant))
        {
            await InsertChunkAsync(TypeA, null);
        }

        (await ReachAsync(new ChunkSearchScope(true, [], null))).ShouldBe([mine]);
    }

    private static bool DependsOnIntegrationService(Type type)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var target = typeof(IDocumentIntegrationService);
        return type.GetConstructors(all).SelectMany(c => c.GetParameters()).Any(p => p.ParameterType == target)
               || type.GetFields(all).Any(f => f.FieldType == target)
               || type.GetProperties(all).Any(p => p.PropertyType == target);
    }

    private Task<List<Guid>> ReachAsync(ChunkSearchScope scope)
        => WithUnitOfWorkAsync(async () =>
        {
            var repository = GetRequiredService<IChunkRepository>();
            return await repository.AsyncExecuter.ToListAsync(ChunkScopeFilter.Apply(await repository.GetQueryableAsync(), scope).Select(c => c.Id));
        });

    private async Task<Guid> InsertChunkAsync(Guid typeId, Guid? ownerId, bool isHidden = false, Guid? id = null)
    {
        var chunkId = id ?? Guid.NewGuid();
        var tenantId = GetRequiredService<ICurrentTenant>().Id;
        await WithUnitOfWorkAsync(() => GetRequiredService<IChunkRepository>().InsertAsync(
            new Chunk(chunkId, tenantId, Guid.NewGuid(), typeId, ownerId, 0, null, "text", new string('0', 64), isHidden)));
        return chunkId;
    }
}
