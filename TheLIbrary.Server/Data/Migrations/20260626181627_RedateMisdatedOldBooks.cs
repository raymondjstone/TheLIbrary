using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RedateMisdatedOldBooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time correction for books that were PROMOTED IN PLACE from a manual
            // placeholder (author refresh / promote-manual-books) before those paths
            // re-dated CreatedAt: the placeholder's mint date stuck, so a past-year
            // book shows up as a brand-new release in Recent Releases. Re-date any
            // book whose CreatedAt lands in a LATER year than its publish year to 1 Jan
            // of that publish year — the same rule Book.CreatedAtForPublishYear applies
            // on insert. Idempotent.
            migrationBuilder.Sql(@"
                UPDATE Books
                SET CreatedAt = DATEFROMPARTS(FirstPublishYear, 1, 1)
                WHERE FirstPublishYear IS NOT NULL
                  AND FirstPublishYear BETWEEN 1 AND YEAR(SYSUTCDATETIME()) - 1
                  AND CreatedAt IS NOT NULL
                  AND YEAR(CreatedAt) > FirstPublishYear;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
