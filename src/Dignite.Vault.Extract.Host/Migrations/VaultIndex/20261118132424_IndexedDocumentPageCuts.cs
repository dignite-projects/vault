using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex;

/// <inheritdoc />
public partial class _20261118132424_IndexedDocumentPageCuts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PageCutsHash",
            table: "VaultIndexedDocuments",
            type: "nchar(64)",
            fixedLength: true,
            maxLength: 64,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PageCutsHash",
            table: "VaultIndexedDocuments");
    }
}
