using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex;

/// <inheritdoc />
public partial class _20261120051540_IndexedDocumentFieldContext : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FieldContext",
            table: "VaultIndexedDocuments",
            type: "nvarchar(600)",
            maxLength: 600,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FieldContext",
            table: "VaultIndexedDocuments");
    }
}
