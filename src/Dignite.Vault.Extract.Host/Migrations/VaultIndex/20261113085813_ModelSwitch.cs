using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <inheritdoc />
    public partial class ModelSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexGenerations_TenantId_Building",
                table: "VaultIndexGenerations",
                column: "TenantId",
                unique: true,
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexedDocuments_TenantId_GenerationId",
                table: "VaultIndexedDocuments",
                columns: new[] { "TenantId", "GenerationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VaultIndexGenerations_TenantId_Building",
                table: "VaultIndexGenerations");

            migrationBuilder.DropIndex(
                name: "IX_VaultIndexedDocuments_TenantId_GenerationId",
                table: "VaultIndexedDocuments");
        }
    }
}
