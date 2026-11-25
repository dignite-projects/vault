using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations;

/// <inheritdoc />
public partial class _20261125061352_Upgraded_BlobManagement_Preview7 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_DirectoryId",
            table: "BlobFileDescriptors");

        migrationBuilder.DropIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_Name",
            table: "BlobFileDescriptors");

        migrationBuilder.DropIndex(
            name: "IX_BlobDirectoryDescriptors_NameKey",
            table: "BlobDirectoryDescriptors");

        migrationBuilder.DropColumn(
            name: "NameKey",
            table: "BlobDirectoryDescriptors");

        migrationBuilder.AlterColumn<string>(
            name: "Hash",
            table: "BlobFileDescriptors",
            type: "nvarchar(44)",
            maxLength: 44,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "nvarchar(64)",
            oldMaxLength: 64);

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_DirectoryId_Name",
            table: "BlobFileDescriptors",
            columns: new[] { "TenantId", "ContainerName", "DirectoryId", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_TenantId_IsPending_CreationTime",
            table: "BlobFileDescriptors",
            columns: new[] { "TenantId", "IsPending", "CreationTime" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_DirectoryId_Name",
            table: "BlobFileDescriptors");

        migrationBuilder.DropIndex(
            name: "IX_BlobFileDescriptors_TenantId_IsPending_CreationTime",
            table: "BlobFileDescriptors");

        migrationBuilder.AlterColumn<string>(
            name: "Hash",
            table: "BlobFileDescriptors",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "nvarchar(44)",
            oldMaxLength: 44,
            oldNullable: true);

        migrationBuilder.AddColumn<string>(
            name: "NameKey",
            table: "BlobDirectoryDescriptors",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_DirectoryId",
            table: "BlobFileDescriptors",
            columns: new[] { "TenantId", "ContainerName", "DirectoryId" });

        migrationBuilder.CreateIndex(
            name: "IX_BlobFileDescriptors_TenantId_ContainerName_Name",
            table: "BlobFileDescriptors",
            columns: new[] { "TenantId", "ContainerName", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_BlobDirectoryDescriptors_NameKey",
            table: "BlobDirectoryDescriptors",
            column: "NameKey",
            unique: true,
            filter: "[NameKey] IS NOT NULL");
    }
}
