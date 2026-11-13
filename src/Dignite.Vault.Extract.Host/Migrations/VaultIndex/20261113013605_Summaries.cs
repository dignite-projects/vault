using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <inheritdoc />
    public partial class Summaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultIndexArtifacts_TenantId_DocumentId_TemplateName",
                table: "VaultIndexArtifacts");

            migrationBuilder.AddColumn<int>(
                name: "Compilation",
                table: "VaultIndexProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "VaultIndexedDocuments",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokenCount",
                table: "VaultIndexArtifacts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "VaultIndexArtifacts",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokenCount",
                table: "VaultIndexArtifacts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexArtifacts_DocumentId_TemplateName",
                table: "VaultIndexArtifacts",
                columns: new[] { "DocumentId", "TemplateName" },
                unique: true,
                filter: "[DocumentId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultIndexArtifacts_DocumentId_TemplateName",
                table: "VaultIndexArtifacts");

            migrationBuilder.DropColumn(
                name: "Compilation",
                table: "VaultIndexProfiles");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "VaultIndexedDocuments");

            migrationBuilder.DropColumn(
                name: "InputTokenCount",
                table: "VaultIndexArtifacts");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "VaultIndexArtifacts");

            migrationBuilder.DropColumn(
                name: "OutputTokenCount",
                table: "VaultIndexArtifacts");

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexArtifacts_TenantId_DocumentId_TemplateName",
                table: "VaultIndexArtifacts",
                columns: new[] { "TenantId", "DocumentId", "TemplateName" });
        }
    }
}
