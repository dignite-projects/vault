using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Features;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Localization;
using Dignite.Vault.Index.Permissions;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Shouldly;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Features;
using Volo.Abp.Localization;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// The module skeleton end to end on SQLite: every entity persists through its default repository, the tenant filter
/// isolates rows, the uniqueness rules hold, and the permission and feature definitions resolve with their
/// localized names.
/// </summary>
public class VaultIndexModel_Tests : VaultIndexTestBase<VaultIndexEntityFrameworkCoreTestModule>
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TypeId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid DocumentId = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid OwnerId = Guid.Parse("33333333-0000-0000-0000-000000000003");
    private static readonly string Hash = new('a', IndexingConsts.ContentHashLength);

    [Fact]
    public async Task Every_entity_round_trips_through_its_repository()
    {
        var started = new DateTime(2026, 11, 12, 1, 2, 3, DateTimeKind.Utc);

        await WithUnitOfWorkAsync(async () =>
        {
            await Repo<IndexProfile>().InsertAsync(new IndexProfile(Guid.NewGuid(), null, TypeId, ChunkingStrategy.Heading, 400, 40));
            await Repo<IndexedDocument>().InsertAsync(new IndexedDocument(DocumentId, null, TypeId, OwnerId));
            await Repo<Chunk>().InsertAsync(new Chunk(Guid.NewGuid(), null, DocumentId, TypeId, OwnerId, 0, "# 契約書 ## 第1条", "甲は乙に対し…", Hash));
            await Repo<Artifact>().InsertAsync(new Artifact(Guid.NewGuid(), null, ArtifactLevel.Document, "outline", TypeId, DocumentId, "{}", Hash));
            await Repo<IndexRun>().InsertAsync(new IndexRun(Guid.NewGuid(), null, DocumentId, IndexRunTrigger.DocumentReady, 1, started));
            await Repo<IndexGeneration>().InsertAsync(new IndexGeneration(Guid.NewGuid(), null, "text-embedding-3-small", 1536));
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var profile = await SingleAsync<IndexProfile>();
            profile.DocumentTypeId.ShouldBe(TypeId);
            profile.MaxTokensPerChunk.ShouldBe(400);
            profile.Version.ShouldBe(1);

            var document = await Repo<IndexedDocument>().GetAsync(DocumentId);
            document.Status.ShouldBe(IndexedDocumentStatus.Pending);
            document.OwnerId.ShouldBe(OwnerId);
            document.CreationTime.ShouldNotBe(default);

            var chunk = await SingleAsync<Chunk>();
            chunk.HeadingPath.ShouldBe("# 契約書 ## 第1条");
            chunk.Text.ShouldBe("甲は乙に対し…");

            (await SingleAsync<Artifact>()).Level.ShouldBe(ArtifactLevel.Document);

            var run = await SingleAsync<IndexRun>();
            run.Status.ShouldBe(IndexRunStatus.Running);
            run.StartedTime.ShouldBe(started);

            var generation = await SingleAsync<IndexGeneration>();
            generation.Status.ShouldBe(IndexGenerationStatus.Building);
            generation.Dimensions.ShouldBe(1536);
        });
    }

    [Fact]
    public async Task The_tenant_filter_keeps_each_layer_to_its_own_rows()
    {
        var currentTenant = GetRequiredService<ICurrentTenant>();

        await WithUnitOfWorkAsync(async () =>
        {
            await Repo<Chunk>().InsertAsync(new Chunk(Guid.NewGuid(), null, DocumentId, TypeId, null, 0, null, "host", Hash));
            await Repo<Chunk>().InsertAsync(new Chunk(Guid.NewGuid(), TenantA, Guid.NewGuid(), TypeId, null, 0, null, "tenant", Hash));
        });

        (await WithUnitOfWorkAsync(() => SingleAsync<Chunk>())).Text.ShouldBe("host");

        using (currentTenant.Change(TenantA))
        {
            (await WithUnitOfWorkAsync(() => SingleAsync<Chunk>())).Text.ShouldBe("tenant");
        }
    }

    /// <summary>
    /// On a tenant's layer. SQLite treats every NULL in a unique index as distinct, so the Host layer (TenantId NULL)
    /// and the one-default-per-layer rule (DocumentTypeId NULL) are SQL Server facts: the index carries no filter
    /// there, and SQL Server's unique index treats NULLs as equal. The migration pins that.
    /// </summary>
    [Fact]
    public async Task A_document_type_has_at_most_one_profile_per_layer()
    {
        using var _ = GetRequiredService<ICurrentTenant>().Change(TenantA);

        await WithUnitOfWorkAsync(() =>
            Repo<IndexProfile>().InsertAsync(new IndexProfile(Guid.NewGuid(), TenantA, TypeId, ChunkingStrategy.Heading, 400, 40)));

        await Should.ThrowAsync<DbUpdateException>(() => WithUnitOfWorkAsync(() =>
            Repo<IndexProfile>().InsertAsync(new IndexProfile(Guid.NewGuid(), TenantA, TypeId, ChunkingStrategy.Token, 300, 0))));
    }

    [Fact]
    public async Task A_document_has_one_chunk_per_ordinal()
    {
        await WithUnitOfWorkAsync(() =>
            Repo<Chunk>().InsertAsync(new Chunk(Guid.NewGuid(), null, DocumentId, TypeId, null, 0, null, "first", Hash)));

        await Should.ThrowAsync<DbUpdateException>(() => WithUnitOfWorkAsync(() =>
            Repo<Chunk>().InsertAsync(new Chunk(Guid.NewGuid(), null, DocumentId, TypeId, null, 0, null, "again", Hash))));
    }

    [Fact]
    public void Changing_how_a_profile_chunks_moves_its_version_and_an_identical_change_does_not()
    {
        var profile = new IndexProfile(Guid.NewGuid(), null, null, ChunkingStrategy.Heading, 400, 40);

        profile.SetChunking(ChunkingStrategy.Heading, 400, 40);
        profile.Version.ShouldBe(1);

        profile.SetChunking(ChunkingStrategy.Heading, 600, 40);
        profile.Version.ShouldBe(2);

        Should.Throw<ArgumentException>(() => profile.SetChunking(ChunkingStrategy.Heading, 100, 100));
        profile.Version.ShouldBe(2);
    }

    [Fact]
    public void A_document_artifact_needs_its_document_and_a_type_artifact_has_none()
    {
        Should.Throw<ArgumentException>(() =>
            new Artifact(Guid.NewGuid(), null, ArtifactLevel.Document, "outline", TypeId, null, "{}", Hash));
        Should.Throw<ArgumentException>(() =>
            new Artifact(Guid.NewGuid(), null, ArtifactLevel.DocumentType, "catalog", TypeId, DocumentId, "{}", Hash));
    }

    [Fact]
    public async Task Permissions_and_the_feature_are_defined_with_localized_names()
    {
        var permissions = GetRequiredService<IPermissionDefinitionManager>();
        var features = GetRequiredService<IFeatureDefinitionManager>();
        var localizer = GetRequiredService<IStringLocalizerFactory>();

        var search = await permissions.GetOrNullAsync(VaultIndexPermissions.Search);
        var profiles = await permissions.GetOrNullAsync(VaultIndexPermissions.Profiles);
        var enable = await features.GetOrNullAsync(VaultIndexFeatures.Enable);
        search.ShouldNotBeNull();
        profiles.ShouldNotBeNull();
        enable.ShouldNotBeNull();
        enable.DefaultValue.ShouldBe("true");

        using (CultureHelper.Use(CultureInfo.GetCultureInfo("ja")))
        {
            var ja = search.DisplayName.Localize(localizer).Value;
            ja.ShouldBe("インデックスの検索");
            enable.DisplayName!.Localize(localizer).Value.ShouldBe("Vault Index を有効にする");
        }

        using (CultureHelper.Use(CultureInfo.GetCultureInfo("en")))
        {
            profiles.DisplayName.Localize(localizer).Value.ShouldBe("Manage index profiles");
        }
    }

    private async Task<TEntity> SingleAsync<TEntity>() where TEntity : class, Volo.Abp.Domain.Entities.IEntity<Guid>
        => (await Repo<TEntity>().GetListAsync()).Single();

    private IRepository<TEntity, Guid> Repo<TEntity>() where TEntity : class, Volo.Abp.Domain.Entities.IEntity<Guid>
        => GetRequiredService<IRepository<TEntity, Guid>>();
}
