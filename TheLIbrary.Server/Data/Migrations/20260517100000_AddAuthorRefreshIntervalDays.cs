using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorRefreshIntervalDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RefreshIntervalDays",
                table: "Authors",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefreshIntervalDays",
                table: "Authors");
        }
    }
}
