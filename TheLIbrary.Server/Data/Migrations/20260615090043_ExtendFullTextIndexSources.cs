using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtendFullTextIndexSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookTextIndexes_BookId",
                table: "BookTextIndexes");

            migrationBuilder.AlterColumn<string>(
                name: "FullPath",
                table: "BookTextIndexes",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<int>(
                name: "BookId",
                table: "BookTextIndexes",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "BookTextIndexes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "BookTextIndexes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_BookTextIndexes_BookId",
                table: "BookTextIndexes",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookTextIndexes_FullPath",
                table: "BookTextIndexes",
                column: "FullPath",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookTextIndexes_BookId",
                table: "BookTextIndexes");

            migrationBuilder.DropIndex(
                name: "IX_BookTextIndexes_FullPath",
                table: "BookTextIndexes");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "BookTextIndexes");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "BookTextIndexes");

            migrationBuilder.AlterColumn<string>(
                name: "FullPath",
                table: "BookTextIndexes",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<int>(
                name: "BookId",
                table: "BookTextIndexes",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookTextIndexes_BookId",
                table: "BookTextIndexes",
                column: "BookId",
                unique: true);
        }
    }
}
