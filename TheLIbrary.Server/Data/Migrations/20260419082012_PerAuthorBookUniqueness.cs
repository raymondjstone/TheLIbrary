using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerAuthorBookUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Books_OpenLibraryWorkKey",
                table: "Books");

            migrationBuilder.CreateIndex(
                name: "IX_Books_AuthorId_OpenLibraryWorkKey",
                table: "Books",
                columns: new[] { "AuthorId", "OpenLibraryWorkKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Books_OpenLibraryWorkKey",
                table: "Books",
                column: "OpenLibraryWorkKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Books_AuthorId_OpenLibraryWorkKey",
                table: "Books");

            migrationBuilder.DropIndex(
                name: "IX_Books_OpenLibraryWorkKey",
                table: "Books");

            migrationBuilder.CreateIndex(
                name: "IX_Books_OpenLibraryWorkKey",
                table: "Books",
                column: "OpenLibraryWorkKey",
                unique: true);
        }
    }
}
