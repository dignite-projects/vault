using Dignite.Vault.Index.Artifacts;
using Dignite.Vault.Index.Chunks;
using Dignite.Vault.Index.Documents;
using Dignite.Vault.Index.Generations;
using Dignite.Vault.Index.Indexing;
using Dignite.Vault.Index.Profiles;
using Dignite.Vault.Index.Runs;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace Dignite.Vault.Index.EntityFrameworkCore;

/// <summary>
/// Index's tables. They share Extract's <c>Vault</c> prefix, so every name says <c>Index</c> to keep the two
/// modules apart. Provider-neutral: the vector and full-text storage is added by the storage provider's package.
/// </summary>
public static class VaultIndexDbContextModelCreatingExtensions
{
    public static void ConfigureVaultIndex(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        var prefix = VaultIndexDbProperties.DbTablePrefix;
        var schema = VaultIndexDbProperties.DbSchema;

        builder.Entity<IndexProfile>(b =>
        {
            b.ToTable(prefix + "IndexProfiles", schema);
            b.ConfigureByConvention();

            // One profile per type and one default per layer. HasFilter(null) keeps SQL Server's own NULL handling
            // (two NULLs collide), instead of EF's default filter that would let every layer hold any number of
            // defaults.
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId }).IsUnique().HasFilter(null);
        });

        builder.Entity<IndexedDocument>(b =>
        {
            b.ToTable(prefix + "IndexedDocuments", schema);
            b.ConfigureByConvention();

            // The id is Extract's document id, assigned by the caller, never generated here.
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ContentHash).HasMaxLength(IndexingConsts.ContentHashLength).IsFixedLength();
            b.Property(x => x.ProfileStamp).HasMaxLength(IndexingConsts.MaxProfileStampLength);
            b.Property(x => x.Title).HasMaxLength(IndexingConsts.MaxTitleLength);
            b.Property(x => x.Language).HasMaxLength(IndexingConsts.MaxLanguageLength);
            b.Ignore(x => x.IsHidden);

            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
            b.HasIndex(x => new { x.TenantId, x.Status });

            // While a new generation is built: the documents not in it yet, which the rebuild walk pages through and
            // whose absence lets the generation take over.
            b.HasIndex(x => new { x.TenantId, x.GenerationId });
        });

        builder.Entity<Chunk>(b =>
        {
            b.ToTable(prefix + "IndexChunks", schema);
            b.ConfigureByConvention();

            b.Property(x => x.HeadingPath).HasMaxLength(IndexingConsts.MaxHeadingPathLength);
            b.Property(x => x.Text).IsRequired();
            b.Property(x => x.ContentHash).IsRequired().HasMaxLength(IndexingConsts.ContentHashLength).IsFixedLength();
            b.Property(x => x.Keywords).HasMaxLength(IndexingConsts.MaxKeywordsLength);
            b.Property(x => x.Questions).HasMaxLength(IndexingConsts.MaxQuestionsLength);

            // DocumentId is unique across tenants, so the ordinal constraint needs no TenantId. Leaving it out also
            // keeps the nullable column off a unique index, where SQL Server would get EF's IS NOT NULL filter and
            // stop constraining Host rows.
            b.HasIndex(x => new { x.DocumentId, x.Ordinal }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
        });

        builder.Entity<Artifact>(b =>
        {
            b.ToTable(prefix + "IndexArtifacts", schema);
            b.ConfigureByConvention();

            b.Property(x => x.TemplateName).IsRequired().HasMaxLength(IndexingConsts.MaxTemplateNameLength);
            b.Property(x => x.Content).IsRequired();
            b.Property(x => x.SourceHash).IsRequired().HasMaxLength(IndexingConsts.ContentHashLength).IsFixedLength();
            b.Property(x => x.Note).HasMaxLength(IndexingConsts.MaxArtifactNoteLength);

            // One artifact per document and template, so two compile jobs racing on a document cannot both insert. The
            // document id is unique across tenants, so TenantId stays off the index, and EF's IS NOT NULL filter on
            // DocumentId leaves type-level artifacts out of it.
            b.HasIndex(x => new { x.DocumentId, x.TemplateName }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId, x.TemplateName });
        });

        builder.Entity<IndexRun>(b =>
        {
            b.ToTable(prefix + "IndexRuns", schema);
            b.ConfigureByConvention();

            b.HasIndex(x => new { x.TenantId, x.DocumentId, x.StartedTime });
        });

        builder.Entity<IndexGeneration>(b =>
        {
            b.ToTable(prefix + "IndexGenerations", schema);
            b.ConfigureByConvention();

            b.Property(x => x.ModelId).IsRequired().HasMaxLength(IndexingConsts.MaxModelIdLength);

            b.HasIndex(x => new { x.TenantId, x.Status });

            // At most one serving generation per layer: two first jobs racing to create it collide here, and the loser
            // is retried and finds the winner. The filter replaces EF's IS NOT NULL one, so on SQL Server the Host's
            // NULL TenantId is constrained too.
            b.HasIndex(x => x.TenantId)
                .HasDatabaseName("IX_VaultIndexGenerations_TenantId_Serving")
                .IsUnique()
                .HasFilter("[Status] = " + (int)IndexGenerationStatus.Serving);

            // At most one building generation per layer, for the same reason: two jobs starting the rebuild for a new
            // model at once.
            // Named, or EF would take it for the serving index on the same column and reconfigure that one.
            b.HasIndex(x => x.TenantId, "IX_VaultIndexGenerations_TenantId_Building")
                .IsUnique()
                .HasFilter("[Status] = " + (int)IndexGenerationStatus.Building);
        });
    }
}
