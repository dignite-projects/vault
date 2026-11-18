using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex;

/// <inheritdoc />
public partial class ChunkPages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "FirstPage",
            table: "VaultIndexChunks",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LastPage",
            table: "VaultIndexChunks",
            type: "int",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FirstPage",
            table: "VaultIndexChunks");

        migrationBuilder.DropColumn(
            name: "LastPage",
            table: "VaultIndexChunks");
    }
}
