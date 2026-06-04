using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalBookFileIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IntegrityCheckedAt",
                table: "LocalBookFiles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IntegrityCheckedSize",
                table: "LocalBookFiles",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrityError",
                table: "LocalBookFiles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IntegrityOk",
                table: "LocalBookFiles",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntegrityPages",
                table: "LocalBookFiles",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntegrityCheckedAt",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "IntegrityCheckedSize",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "IntegrityError",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "IntegrityOk",
                table: "LocalBookFiles");

            migrationBuilder.DropColumn(
                name: "IntegrityPages",
                table: "LocalBookFiles");
        }
    }
}
