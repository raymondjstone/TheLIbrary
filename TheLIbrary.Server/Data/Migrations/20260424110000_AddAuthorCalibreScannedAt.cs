using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TheLibrary.Server.Data;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    [DbContext(typeof(LibraryDbContext))]
    [Migration("20260424110000_AddAuthorCalibreScannedAt")]
    public partial class AddAuthorCalibreScannedAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CalibreScannedAt",
                table: "Authors",
                type: "datetime2",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CalibreScannedAt", table: "Authors");
        }
    }
}
