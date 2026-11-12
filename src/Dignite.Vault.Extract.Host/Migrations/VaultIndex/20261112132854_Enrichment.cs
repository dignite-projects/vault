using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <inheritdoc />
    public partial class Enrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Enrichment",
                table: "VaultIndexProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                table: "VaultIndexChunks",
                type: "nvarchar(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Questions",
                table: "VaultIndexChunks",
                type: "nvarchar(1100)",
                maxLength: 1100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Enrichment",
                table: "VaultIndexProfiles");

            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "VaultIndexChunks");

            migrationBuilder.DropColumn(
                name: "Questions",
                table: "VaultIndexChunks");
        }
    }
}
