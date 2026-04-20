using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class FingerprintAndMetadataOnLocalBookFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetadataAuthor",
                table: "LocalBookFiles",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataLanguage",
                table: "LocalBookFiles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataTitle",
                table: "LocalBookFiles",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "LocalBookFiles",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "SizeBytes",
                table: "LocalBookFiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataAuthor",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "MetadataLanguage",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "MetadataTitle",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "SizeBytes",
                table: "LocalBookFiles");
        }
    }
}
