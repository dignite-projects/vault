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

            b.HasIndex(x => new { x.TenantId, x.DocumentTypeId });
            b.HasIndex(x => new { x.TenantId, x.Status });
        });

        builder.Entity<Chunk>(b =>
        {
            b.ToTable(prefix + "IndexChunks", schema);
            b.ConfigureByConvention();

            b.Property(x => x.HeadingPath).HasMaxLength(IndexingConsts.MaxHeadingPathLength);
            b.Property(x => x.Text).IsRequired();
            b.Property(x => x.ContentHash).IsRequired().HasMaxLength(IndexingConsts.ContentHashLength).IsFixedLength();

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

            b.HasIndex(x => new { x.TenantId, x.DocumentId, x.TemplateName });
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
        });
    }
}
