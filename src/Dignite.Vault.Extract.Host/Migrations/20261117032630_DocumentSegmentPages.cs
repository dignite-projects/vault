using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations;

/// <inheritdoc />
public partial class DocumentSegmentPages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "FirstPage",
            table: "VaultDocumentSegments",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LastPage",
            table: "VaultDocumentSegments",
            type: "int",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FirstPage",
            table: "VaultDocumentSegments");

        migrationBuilder.DropColumn(
            name: "LastPage",
            table: "VaultDocumentSegments");
    }
}
