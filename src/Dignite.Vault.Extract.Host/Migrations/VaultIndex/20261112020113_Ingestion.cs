using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <inheritdoc />
    public partial class Ingestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProfileVersion",
                table: "VaultIndexedDocuments");

            migrationBuilder.AddColumn<int>(
                name: "FailedAttempts",
                table: "VaultIndexedDocuments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "GenerationId",
                table: "VaultIndexedDocuments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IndexingStartedTime",
                table: "VaultIndexedDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileStamp",
                table: "VaultIndexedDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VaultIndexChunkVectors",
                columns: table => new
                {
                    GenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChunkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(1024)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultIndexChunkVectors", x => new { x.GenerationId, x.ChunkId })
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexGenerations_TenantId_Serving",
                table: "VaultIndexGenerations",
                column: "TenantId",
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexChunkVectors_DocumentId",
                table: "VaultIndexChunkVectors",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexChunkVectors_TenantId_GenerationId_DocumentTypeId_ChunkId",
                table: "VaultIndexChunkVectors",
                columns: new[] { "TenantId", "GenerationId", "DocumentTypeId", "ChunkId" })
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VaultIndexChunkVectors");

            migrationBuilder.DropIndex(
                name: "IX_VaultIndexGenerations_TenantId_Serving",
                table: "VaultIndexGenerations");

            migrationBuilder.DropColumn(
                name: "FailedAttempts",
                table: "VaultIndexedDocuments");

            migrationBuilder.DropColumn(
                name: "GenerationId",
                table: "VaultIndexedDocuments");

            migrationBuilder.DropColumn(
                name: "IndexingStartedTime",
                table: "VaultIndexedDocuments");

            migrationBuilder.DropColumn(
                name: "ProfileStamp",
                table: "VaultIndexedDocuments");

            migrationBuilder.AddColumn<int>(
                name: "ProfileVersion",
                table: "VaultIndexedDocuments",
                type: "int",
                nullable: true);
        }
    }
}
