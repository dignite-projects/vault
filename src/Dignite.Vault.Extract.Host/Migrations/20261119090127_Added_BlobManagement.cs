using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations;

/// <inheritdoc />
public partial class _20261119090127_Added_BlobManagement : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BlobDirectoryDescriptors",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ContainerName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Path = table.Column<string>(type: "nvarchar(527)", maxLength: 527, nullable: false),
                Order = table.Column<int>(type: "int", nullable: false),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                NameKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastModifierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                DeleterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DeletionTime = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BlobDirectoryDescriptors", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BlobFileDescriptors",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ContainerName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                BlobName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                MimeType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Size = table.Column<long>(type: "bigint", nullable: false),
                NameKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                DirectoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                IsPublic = table.Column<bool>(type: "bit", nullable: false),
                IsPending = table.Column<bool>(type: "bit", nullable: false),
                CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastModifierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DeleterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DeletionTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BlobFileDescriptors", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BlobDirectoryDescriptors_NameKey",
            table: "BlobDirectoryDescriptors",
            column: "NameKey",
            unique: true,
            filter: "[NameKey] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_BlobDirectoryDescriptors_ParentId",
            table: "BlobDirectoryDescriptors",
            column: "ParentId");

        migrationBuilder.CreateIndex(
            name: "IX_BlobDirectoryDescriptors_Path",
            table: "BlobDirectoryDescriptors",
            column: "Path");

        migrationBuilder.CreateIndex(
            name: "IX_BlobDirectoryDescriptors_TenantId_ContainerName_ParentId",
            table: "BlobDirectoryDescriptors",
            columns: new[] { "TenantId", "ContainerName", "ParentId" });

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_DirectoryId",
            table: "BlobFileDescriptors",
            column: "DirectoryId");

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_NameKey",
            table: "BlobFileDescriptors",
            column: "NameKey",
            unique: true,
            filter: "[NameKey] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_BlobName",
            table: "BlobFileDescriptors",
            columns: new[] { "TenantId", "ContainerName", "BlobName" });

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_DirectoryId",
            table: "BlobFileDescriptors",
            columns: new[] { "TenantId", "ContainerName", "DirectoryId" });

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_Name",
            table: "BlobFileDescriptors",
            columns: new[] { "TenantId", "ContainerName", "Name" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BlobDirectoryDescriptors");

        migrationBuilder.DropTable(
            name: "BlobFileDescriptors");
    }
}
