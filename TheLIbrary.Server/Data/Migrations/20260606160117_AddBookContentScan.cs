using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBookContentScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookContentScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FullPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AuthorId = table.Column<int>(type: "int", nullable: true),
                    Isbn = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Author = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Series = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SeriesPosition = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AlsoByTitles = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ScannedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reviewed = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookContentScans", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookContentScans");
        }
    }
}
