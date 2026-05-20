using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedToAuthorId",
                table: "Authors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPenName",
                table: "Authors",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Authors_LinkedToAuthorId",
                table: "Authors",
                column: "LinkedToAuthorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Authors_Authors_LinkedToAuthorId",
                table: "Authors",
                column: "LinkedToAuthorId",
                principalTable: "Authors",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Authors_Authors_LinkedToAuthorId",
                table: "Authors");

            migrationBuilder.DropIndex(
                name: "IX_Authors_LinkedToAuthorId",
                table: "Authors");

            migrationBuilder.DropColumn(
                name: "IsPenName",
                table: "Authors");

            migrationBuilder.DropColumn(
                name: "LinkedToAuthorId",
                table: "Authors");
        }
    }
}
