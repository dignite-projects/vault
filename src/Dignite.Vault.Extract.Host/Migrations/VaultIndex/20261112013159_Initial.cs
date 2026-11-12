using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VaultIndexArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Level = table.Column<int>(type: "int", nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DocumentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    IsStale = table.Column<bool>(type: "bit", nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultIndexArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultIndexChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    HeadingPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultIndexChunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultIndexedDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    IsWithheld = table.Column<bool>(type: "bit", nullable: false),
                    RerunRequested = table.Column<bool>(type: "bit", nullable: false),
                    LastEventTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ContentHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ProfileVersion = table.Column<int>(type: "int", nullable: true),
                    ChunkCount = table.Column<int>(type: "int", nullable: false),
                    LastIndexedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultIndexedDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultIndexGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ModelId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ServingSince = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetiredTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultIndexGenerations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultIndexProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChunkingStrategy = table.Column<int>(type: "int", nullable: false),
                    MaxTokensPerChunk = table.Column<int>(type: "int", nullable: false),
                    OverlapTokens = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultIndexProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultIndexRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    StartedTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChunkCount = table.Column<int>(type: "int", nullable: true),
                    EmbeddingTokenCount = table.Column<long>(type: "bigint", nullable: true),
                    LlmInputTokenCount = table.Column<long>(type: "bigint", nullable: true),
                    LlmOutputTokenCount = table.Column<long>(type: "bigint", nullable: true),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultIndexRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexArtifacts_TenantId_DocumentId_TemplateName",
                table: "VaultIndexArtifacts",
                columns: new[] { "TenantId", "DocumentId", "TemplateName" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexArtifacts_TenantId_DocumentTypeId_TemplateName",
                table: "VaultIndexArtifacts",
                columns: new[] { "TenantId", "DocumentTypeId", "TemplateName" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexChunks_DocumentId_Ordinal",
                table: "VaultIndexChunks",
                columns: new[] { "DocumentId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexChunks_TenantId_DocumentTypeId",
                table: "VaultIndexChunks",
                columns: new[] { "TenantId", "DocumentTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexedDocuments_TenantId_DocumentTypeId",
                table: "VaultIndexedDocuments",
                columns: new[] { "TenantId", "DocumentTypeId" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexedDocuments_TenantId_Status",
                table: "VaultIndexedDocuments",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexGenerations_TenantId_Status",
                table: "VaultIndexGenerations",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexProfiles_TenantId_DocumentTypeId",
                table: "VaultIndexProfiles",
                columns: new[] { "TenantId", "DocumentTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultIndexRuns_TenantId_DocumentId_StartedTime",
                table: "VaultIndexRuns",
                columns: new[] { "TenantId", "DocumentId", "StartedTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VaultIndexArtifacts");

            migrationBuilder.DropTable(
                name: "VaultIndexChunks");

            migrationBuilder.DropTable(
                name: "VaultIndexedDocuments");

            migrationBuilder.DropTable(
                name: "VaultIndexGenerations");

            migrationBuilder.DropTable(
                name: "VaultIndexProfiles");

            migrationBuilder.DropTable(
                name: "VaultIndexRuns");
        }
    }
}
