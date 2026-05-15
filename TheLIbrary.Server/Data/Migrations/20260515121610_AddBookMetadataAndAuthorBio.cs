using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBookMetadataAndAuthorBio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReadAt",
                table: "Books",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReadStatus",
                table: "Books",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Series",
                table: "Books",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesPosition",
                table: "Books",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subjects",
                table: "Books",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Wanted",
                table: "Books",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Bio",
                table: "Authors",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "ReadStatus",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Series",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "SeriesPosition",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Subjects",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Wanted",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Bio",
                table: "Authors");
        }
    }
}
