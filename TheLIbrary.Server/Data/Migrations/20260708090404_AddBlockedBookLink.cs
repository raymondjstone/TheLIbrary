using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockedBookLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockedBookLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FullPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    BookId = table.Column<int>(type: "int", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedBookLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlockedBookLinks_FullPath_BookId",
                table: "BlockedBookLinks",
                columns: new[] { "FullPath", "BookId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedBookLinks");
        }
    }
}
