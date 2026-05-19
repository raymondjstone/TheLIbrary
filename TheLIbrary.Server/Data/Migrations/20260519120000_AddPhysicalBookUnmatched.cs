using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhysicalBookUnmatched : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhysicalBookUnmatched",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Author = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    SeriesPos = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhysicalBookUnmatched", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhysicalBookUnmatched_Author_Title",
                table: "PhysicalBookUnmatched",
                columns: new[] { "Author", "Title" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhysicalBookUnmatched");
        }
    }
}
