using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <summary>
    /// Puts a chunk's enrichment (keywords, answerable questions) in the keyword leg: the two columns join the chunk text
    /// in the Japanese full-text index. Its own migration because full-text DDL cannot run inside a transaction, as
    /// <c>FullTextSearch</c> explains, and guarded the same way: without Full-Text Search, or without the index, nothing
    /// happens. After installing Full-Text Search later, apply <c>FullTextSearch</c> and this one again.
    /// </summary>
    public partial class EnrichmentFullText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                    AND EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('VaultIndexChunks'))
                    AND NOT EXISTS (
                        SELECT 1 FROM sys.fulltext_index_columns
                        WHERE object_id = OBJECT_ID('VaultIndexChunks') AND column_id = COLUMNPROPERTY(OBJECT_ID('VaultIndexChunks'), 'Keywords', 'ColumnId'))
                    EXEC('ALTER FULLTEXT INDEX ON VaultIndexChunks ADD ([Keywords] LANGUAGE 1041, [Questions] LANGUAGE 1041)');
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.fulltext_index_columns
                    WHERE object_id = OBJECT_ID('VaultIndexChunks') AND column_id = COLUMNPROPERTY(OBJECT_ID('VaultIndexChunks'), 'Keywords', 'ColumnId'))
                    EXEC('ALTER FULLTEXT INDEX ON VaultIndexChunks DROP ([Keywords], [Questions])');
                """, suppressTransaction: true);
        }
    }
}
