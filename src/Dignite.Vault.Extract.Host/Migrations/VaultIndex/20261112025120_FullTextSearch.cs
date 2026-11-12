using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <summary>
    /// The keyword leg: a Japanese (LCID 1041) full-text index on the chunk text, kept current automatically. Its own
    /// migration because full-text DDL cannot run inside a transaction: alone, a failure leaves nothing half-applied,
    /// and both statements are guarded so a rerun is harmless.
    /// <para>
    /// Full-Text Search is a separate SQL Server component (absent from LocalDB and the plain container image), so the
    /// index is created only where it is installed. Without it search runs on the vector leg alone and the application
    /// logs a warning; after installing it, roll back to the previous migration and apply this one again.
    /// </para>
    /// </summary>
    public partial class FullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dynamic SQL, because CREATE FULLTEXT must be the only statement of its batch branch.
            migrationBuilder.Sql("""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'VaultIndexCatalog')
                        EXEC('CREATE FULLTEXT CATALOG VaultIndexCatalog');
                    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('VaultIndexChunks'))
                        EXEC('CREATE FULLTEXT INDEX ON VaultIndexChunks ([Text] LANGUAGE 1041) KEY INDEX PK_VaultIndexChunks ON VaultIndexCatalog WITH CHANGE_TRACKING AUTO');
                END
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('VaultIndexChunks'))
                    EXEC('DROP FULLTEXT INDEX ON VaultIndexChunks');
                IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'VaultIndexCatalog')
                    EXEC('DROP FULLTEXT CATALOG VaultIndexCatalog');
                """, suppressTransaction: true);
        }
    }
}
