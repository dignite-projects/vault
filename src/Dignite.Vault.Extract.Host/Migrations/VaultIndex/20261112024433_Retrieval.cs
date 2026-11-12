using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dignite.Vault.Extract.Host.Migrations.VaultIndex
{
    /// <inheritdoc />
    public partial class Retrieval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "VaultIndexedDocuments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "VaultIndexChunkVectors",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "VaultIndexChunks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Documents already in the recycle bin or withheld keep that state on their chunks and vectors.
            migrationBuilder.Sql("""
                UPDATE c SET c.IsHidden = 1
                FROM VaultIndexChunks c JOIN VaultIndexedDocuments d ON d.Id = c.DocumentId
                WHERE d.IsArchived = 1 OR d.IsWithheld = 1;
                UPDATE v SET v.IsHidden = 1
                FROM VaultIndexChunkVectors v JOIN VaultIndexedDocuments d ON d.Id = v.DocumentId
                WHERE d.IsArchived = 1 OR d.IsWithheld = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "VaultIndexedDocuments");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "VaultIndexChunkVectors");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "VaultIndexChunks");
        }
    }
}
