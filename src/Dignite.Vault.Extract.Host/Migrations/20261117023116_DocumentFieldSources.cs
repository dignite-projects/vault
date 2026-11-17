using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations;

/// <inheritdoc />
public partial class DocumentFieldSources : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FieldSources",
            table: "VaultDocuments",
            type: "nvarchar(max)",
            nullable: false,
            defaultValueSql: "'{}'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FieldSources",
            table: "VaultDocuments");
    }
}
