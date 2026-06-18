using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBookCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Books",
                type: "datetime2",
                nullable: true,
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.CreateIndex(
                name: "IX_Books_CreatedAt",
                table: "Books",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Books_CreatedAt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Books");
        }
    }
}
