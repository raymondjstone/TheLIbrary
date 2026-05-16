using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRemarkableFolderCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FolderMapJson",
                table: "RemarkableAuths",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderMapJson",
                table: "RemarkableAuths");
        }
    }
}
