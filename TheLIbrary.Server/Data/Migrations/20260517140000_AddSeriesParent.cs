using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentSeriesId",
                table: "Series",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PositionInParent",
                table: "Series",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_ParentSeriesId",
                table: "Series",
                column: "ParentSeriesId");

            migrationBuilder.AddForeignKey(
                name: "FK_Series_Series_ParentSeriesId",
                table: "Series",
                column: "ParentSeriesId",
                principalTable: "Series",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Series_Series_ParentSeriesId",
                table: "Series");

            migrationBuilder.DropIndex(
                name: "IX_Series_ParentSeriesId",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "ParentSeriesId",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "PositionInParent",
                table: "Series");
        }
    }
}
