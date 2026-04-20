using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenLibraryAuthorCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpenLibraryAuthors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OlKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    PersonalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AlternateNames = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    BirthDate = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DeathDate = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenLibraryAuthors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenLibraryAuthors_NormalizedName",
                table: "OpenLibraryAuthors",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_OpenLibraryAuthors_OlKey",
                table: "OpenLibraryAuthors",
                column: "OlKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenLibraryAuthors");
        }
    }
}
