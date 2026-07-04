using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTitleBlacklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TitleBlacklist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleBlacklist", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TitleBlacklist_NormalizedTitle",
                table: "TitleBlacklist",
                column: "NormalizedTitle",
                unique: true);

            migrationBuilder.InsertData(
                table: "TitleBlacklist",
                columns: new[] { "Title", "NormalizedTitle", "AddedAt" },
                columnTypes: new[] { "nvarchar(500)", "nvarchar(500)", "datetime2" },
                values: new object[,]
                {
                    { "Scanned", "scanned", DateTime.UtcNow },
                    { "Unknown", "unknown", DateTime.UtcNow },
                    { "@page", "page", DateTime.UtcNow },
                    { "A Del Rey ® Book Published", "del rey book published", DateTime.UtcNow },
                    { "Published", "published", DateTime.UtcNow },
                    { "Illustrations", "illustrations", DateTime.UtcNow },
                    { "book", "book", DateTime.UtcNow },
                    { "books", "books", DateTime.UtcNow },
                    { "Scanned and Semi-proofed", "scanned and semi proofed", DateTime.UtcNow },
                    { "remodeling", "remodeling", DateTime.UtcNow },
                    { "Also", "also", DateTime.UtcNow },
                    { "Edited", "edited", DateTime.UtcNow },
                    { "Edited and Introduction", "edited and introduction", DateTime.UtcNow },
                    { "introduction", "introduction", DateTime.UtcNow },
                    { "A Novel", "novel", DateTime.UtcNow },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TitleBlacklist");
        }
    }
}
