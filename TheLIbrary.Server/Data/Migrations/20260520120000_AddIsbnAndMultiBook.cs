using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsbnAndMultiBook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Isbn",
                table: "Books",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Isbn",
                table: "LocalBookFiles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdditionalBookIds",
                table: "LocalBookFiles",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Books_Isbn",
                table: "Books",
                column: "Isbn",
                filter: "[Isbn] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LocalBookFiles_Isbn",
                table: "LocalBookFiles",
                column: "Isbn",
                filter: "[Isbn] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_LocalBookFiles_Isbn", table: "LocalBookFiles");
            migrationBuilder.DropIndex(name: "IX_Books_Isbn", table: "Books");
            migrationBuilder.DropColumn(name: "AdditionalBookIds", table: "LocalBookFiles");
            migrationBuilder.DropColumn(name: "Isbn", table: "LocalBookFiles");
            migrationBuilder.DropColumn(name: "Isbn", table: "Books");
        }
    }
}
