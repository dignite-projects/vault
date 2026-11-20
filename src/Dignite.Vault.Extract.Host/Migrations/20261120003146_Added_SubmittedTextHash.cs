using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations;

/// <inheritdoc />
public partial class _20261120003146_Added_SubmittedTextHash : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SubmittedTextHash",
            table: "VaultDocuments",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_VaultDocuments_SubmittedTextHash",
            table: "VaultDocuments",
            column: "SubmittedTextHash");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_VaultDocuments_SubmittedTextHash",
            table: "VaultDocuments");

        migrationBuilder.DropColumn(
            name: "SubmittedTextHash",
            table: "VaultDocuments");
    }
}
