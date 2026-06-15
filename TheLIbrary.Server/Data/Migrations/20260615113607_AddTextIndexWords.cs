using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTextIndexWords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TextIndexWords",
                columns: table => new
                {
                    Word = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TextIndexId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextIndexWords", x => new { x.Word, x.TextIndexId });
                    table.ForeignKey(
                        name: "FK_TextIndexWords_BookTextIndexes_TextIndexId",
                        column: x => x.TextIndexId,
                        principalTable: "BookTextIndexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TextIndexWords_TextIndexId",
                table: "TextIndexWords",
                column: "TextIndexId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TextIndexWords");
        }
    }
}
