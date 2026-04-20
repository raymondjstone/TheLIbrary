using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Authors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OpenLibraryKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    CalibreFolderName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExclusionReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    WorkCount = table.Column<int>(type: "int", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Authors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Books",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OpenLibraryWorkKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    FirstPublishYear = table.Column<int>(type: "int", nullable: true),
                    CoverId = table.Column<int>(type: "int", nullable: true),
                    AuthorId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Books_Authors_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Authors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LocalBookFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuthorFolder = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    TitleFolder = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    FullPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    BookId = table.Column<int>(type: "int", nullable: true),
                    AuthorId = table.Column<int>(type: "int", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalBookFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocalBookFiles_Authors_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Authors",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LocalBookFiles_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Authors_CalibreFolderName",
                table: "Authors",
                column: "CalibreFolderName");

            migrationBuilder.CreateIndex(
                name: "IX_Authors_Name",
                table: "Authors",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Authors_OpenLibraryKey",
                table: "Authors",
                column: "OpenLibraryKey",
                unique: true,
                filter: "[OpenLibraryKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Books_AuthorId_NormalizedTitle",
                table: "Books",
                columns: new[] { "AuthorId", "NormalizedTitle" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_OpenLibraryWorkKey",
                table: "Books",
                column: "OpenLibraryWorkKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalBookFiles_AuthorId_NormalizedTitle",
                table: "LocalBookFiles",
                columns: new[] { "AuthorId", "NormalizedTitle" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalBookFiles_BookId",
                table: "LocalBookFiles",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalBookFiles_FullPath",
                table: "LocalBookFiles",
                column: "FullPath",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalBookFiles");

            migrationBuilder.DropTable(
                name: "Books");

            migrationBuilder.DropTable(
                name: "Authors");
        }
    }
}
