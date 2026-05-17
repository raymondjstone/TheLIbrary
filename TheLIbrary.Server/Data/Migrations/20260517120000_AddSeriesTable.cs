using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLibrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create Series table
            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PrimaryAuthorId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Series_Authors_PrimaryAuthorId",
                        column: x => x.PrimaryAuthorId,
                        principalTable: "Authors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Series_NormalizedName",
                table: "Series",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_Series_PrimaryAuthorId",
                table: "Series",
                column: "PrimaryAuthorId");

            // 2. Create SeriesAuthors join table
            migrationBuilder.CreateTable(
                name: "SeriesAuthors",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "int", nullable: false),
                    AuthorId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesAuthors", x => new { x.SeriesId, x.AuthorId });
                    table.ForeignKey(
                        name: "FK_SeriesAuthors_Authors_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Authors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeriesAuthors_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesAuthors_AuthorId",
                table: "SeriesAuthors",
                column: "AuthorId");

            // 3. Add SeriesId column to Books
            migrationBuilder.AddColumn<int>(
                name: "SeriesId",
                table: "Books",
                type: "int",
                nullable: true);

            // 4. Data migration: populate Series table from distinct Book.Series strings
            migrationBuilder.Sql(@"
INSERT INTO [Series] ([Name], [NormalizedName])
SELECT DISTINCT LTRIM(RTRIM([Series])), LOWER(LTRIM(RTRIM([Series])))
FROM [Books]
WHERE [Series] IS NOT NULL AND LTRIM(RTRIM([Series])) != '';

UPDATE b SET b.[SeriesId] = s.[Id]
FROM [Books] b
JOIN [Series] s ON LOWER(LTRIM(RTRIM(b.[Series]))) = s.[NormalizedName]
WHERE b.[Series] IS NOT NULL AND LTRIM(RTRIM(b.[Series])) != '';
");

            // 5. Drop the old Series string column from Books
            migrationBuilder.DropColumn(
                name: "Series",
                table: "Books");

            // 6. Add FK and index on Books.SeriesId
            migrationBuilder.CreateIndex(
                name: "IX_Books_SeriesId",
                table: "Books",
                column: "SeriesId");

            migrationBuilder.AddForeignKey(
                name: "FK_Books_Series_SeriesId",
                table: "Books",
                column: "SeriesId",
                principalTable: "Series",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop FK and index on Books.SeriesId
            migrationBuilder.DropForeignKey(
                name: "FK_Books_Series_SeriesId",
                table: "Books");

            migrationBuilder.DropIndex(
                name: "IX_Books_SeriesId",
                table: "Books");

            // Re-add the Series string column to Books
            migrationBuilder.AddColumn<string>(
                name: "Series",
                table: "Books",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            // Populate the string column from the Series table
            migrationBuilder.Sql(@"
UPDATE b SET b.[Series] = s.[Name]
FROM [Books] b
JOIN [Series] s ON b.[SeriesId] = s.[Id];
");

            // Drop SeriesId from Books
            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "Books");

            // Drop join table and Series table
            migrationBuilder.DropTable(name: "SeriesAuthors");
            migrationBuilder.DropTable(name: "Series");
        }
    }
}
