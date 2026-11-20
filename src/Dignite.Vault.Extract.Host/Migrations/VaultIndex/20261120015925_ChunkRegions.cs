using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex;

/// <inheritdoc />
public partial class _20261120015925_ChunkRegions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Content",
            table: "VaultIndexChunks",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Regions",
            table: "VaultIndexChunks",
            type: "nvarchar(max)",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Content",
            table: "VaultIndexChunks");

        migrationBuilder.DropColumn(
            name: "Regions",
            table: "VaultIndexChunks");
    }
}
