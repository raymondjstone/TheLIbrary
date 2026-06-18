using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TheLIbrary.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillBookCreatedAtFromPublishYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Correct implausible future publish years (OpenLibrary data errors like
            // 2098, 2207): anything more than 3 years beyond the current year is
            // clamped to the current year before anything else keys off it.
            migrationBuilder.Sql(@"
                UPDATE Books
                SET FirstPublishYear = YEAR(SYSUTCDATETIME())
                WHERE FirstPublishYear > YEAR(SYSUTCDATETIME()) + 3;");

            // Books that predate added-date tracking have a null CreatedAt. Default
            // their "added" date to 1 Jan of the (now-corrected) publish year so the
            // Recent Releases "by month" grouping has something to show for the
            // existing catalogue (books added by the refresh job from now on keep
            // their real timestamp from the column default). Guarded to the valid
            // DATEFROMPARTS year range.
            migrationBuilder.Sql(@"
                UPDATE Books
                SET CreatedAt = DATEFROMPARTS(FirstPublishYear, 1, 1)
                WHERE CreatedAt IS NULL
                  AND FirstPublishYear IS NOT NULL
                  AND FirstPublishYear BETWEEN 1 AND 9999;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way data backfill; nothing to revert.
        }
    }
}
