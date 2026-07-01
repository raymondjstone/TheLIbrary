using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Data;

public class LibraryDbContext : DbContext
{
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options) { }

    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<LocalBookFile> LocalBookFiles => Set<LocalBookFile>();
    public DbSet<LibraryLocation> LibraryLocations => Set<LibraryLocation>();
    public DbSet<OpenLibraryAuthor> OpenLibraryAuthors => Set<OpenLibraryAuthor>();
    public DbSet<IgnoredFolder> IgnoredFolders => Set<IgnoredFolder>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<RemarkableAuth> RemarkableAuths => Set<RemarkableAuth>();
    public DbSet<AuthorBlacklist> AuthorBlacklist => Set<AuthorBlacklist>();
    public DbSet<NzbSite> NzbSites => Set<NzbSite>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<SeriesAuthor> SeriesAuthors => Set<SeriesAuthor>();
    public DbSet<PhysicalBookUnmatched> PhysicalBookUnmatched => Set<PhysicalBookUnmatched>();
    public DbSet<UnknownFile> UnknownFiles => Set<UnknownFile>();
    public DbSet<UnknownFileCheck> UnknownFileChecks => Set<UnknownFileCheck>();
    public DbSet<BookContentScan> BookContentScans => Set<BookContentScan>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<BookCollection> BookCollections => Set<BookCollection>();
    public DbSet<BookTextIndex> BookTextIndexes => Set<BookTextIndex>();
    public DbSet<TextIndexWord> TextIndexWords => Set<TextIndexWord>();
    public DbSet<ActivityLogEntry> ActivityLog => Set<ActivityLogEntry>();
    public DbSet<IsbnResolution> IsbnResolutions => Set<IsbnResolution>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ActivityLogEntry>(e => e.HasIndex(x => x.At));

        b.Entity<Author>(e =>
        {
            // DB-assigned creation timestamp; lets any insert path stamp it
            // without touching every call site, and backfills existing rows.
            e.Property(x => x.CreatedAt).HasDefaultValueSql(
                Database.IsSqlServer() ? "SYSUTCDATETIME()" : "CURRENT_TIMESTAMP");
            e.HasIndex(x => x.OpenLibraryKey).IsUnique().HasFilter("[OpenLibraryKey] IS NOT NULL");
            e.HasIndex(x => x.CalibreFolderName);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.LinkedToAuthorId);
            // Self-referencing FK uses ClientSetNull (NO ACTION in SQL) to avoid
            // SQL Server's "may cause cycles or multiple cascade paths" rejection.
            e.HasOne(x => x.LinkedTo)
                .WithMany(x => x.LinkedFrom)
                .HasForeignKey(x => x.LinkedToAuthorId)
                .OnDelete(DeleteBehavior.ClientSetNull);
        });

        b.Entity<Book>(e =>
        {
            // OL works can have multiple authors; uniqueness is per-author,
            // not global, so each watchlisted co-author gets their own row.
            e.HasIndex(x => new { x.AuthorId, x.OpenLibraryWorkKey }).IsUnique();
            e.HasIndex(x => x.OpenLibraryWorkKey);
            e.HasIndex(x => new { x.AuthorId, x.NormalizedTitle });
            e.HasIndex(x => x.Isbn).HasFilter("[Isbn] IS NOT NULL");
            // DB-assigned creation timestamp for new rows; existing rows stay null
            // (so the Recent Releases "by month" view shows only genuinely-new books).
            e.Property(x => x.CreatedAt).HasDefaultValueSql(
                Database.IsSqlServer() ? "SYSUTCDATETIME()" : "CURRENT_TIMESTAMP");
            e.HasIndex(x => x.CreatedAt);
            e.HasOne(x => x.Author)
                .WithMany(a => a.Books)
                .HasForeignKey(x => x.AuthorId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Series)
                .WithMany(s => s.Books)
                .HasForeignKey(x => x.SeriesId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Series>(e =>
        {
            e.HasIndex(x => x.NormalizedName);
            e.HasIndex(x => x.ParentSeriesId);
            e.HasOne(x => x.PrimaryAuthor)
                .WithMany()
                .HasForeignKey(x => x.PrimaryAuthorId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ParentSeries)
                .WithMany(x => x.ChildSeries)
                .HasForeignKey(x => x.ParentSeriesId)
                .OnDelete(DeleteBehavior.ClientSetNull);
        });

        b.Entity<SeriesAuthor>(e =>
        {
            e.HasKey(x => new { x.SeriesId, x.AuthorId });
            e.HasOne(x => x.Series).WithMany(s => s.SeriesAuthors).HasForeignKey(x => x.SeriesId);
            e.HasOne(x => x.Author).WithMany(a => a.SeriesAuthors).HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LibraryLocation>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
        });

        b.Entity<LocalBookFile>(e =>
        {
            e.HasIndex(x => x.FullPath).IsUnique();
            e.HasIndex(x => new { x.AuthorId, x.NormalizedTitle });
            e.HasIndex(x => x.Isbn).HasFilter("[Isbn] IS NOT NULL");
            e.HasOne(x => x.Book)
                .WithMany(x => x.LocalFiles)
                .HasForeignKey(x => x.BookId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Author)
                .WithMany()
                .HasForeignKey(x => x.AuthorId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<IgnoredFolder>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(300);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<AuthorBlacklist>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.NormalizedName).HasMaxLength(300);
            e.Property(x => x.FolderName).HasMaxLength(300);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });

        b.Entity<NzbSite>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.UrlTemplate).HasMaxLength(500);
            e.Property(x => x.Order).HasDefaultValue(99);
            e.Property(x => x.Active).HasDefaultValue(true);
        });

        b.Entity<PhysicalBookUnmatched>(e =>
        {
            e.Property(x => x.Author).HasMaxLength(512);
            e.Property(x => x.Title).HasMaxLength(1024);
            e.Property(x => x.SeriesPos).HasMaxLength(100);
            e.Property(x => x.Isbn).HasMaxLength(20);
            e.HasIndex(x => new { x.Author, x.Title });
        });

        b.Entity<UnknownFile>(e =>
        {
            e.HasIndex(x => x.FullPath).IsUnique();
            e.HasIndex(x => x.NormalizedTitle);
        });

        b.Entity<Collection>(e =>
        {
            e.HasIndex(x => x.NormalizedName).IsUnique();
        });

        b.Entity<BookCollection>(e =>
        {
            e.HasKey(x => new { x.CollectionId, x.BookId });
            e.HasOne(x => x.Collection).WithMany(c => c.Books)
                .HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Book).WithMany(bk => bk.Collections)
                .HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BookTextIndex>(e =>
        {
            e.HasIndex(x => x.FullPath).IsUnique();
            e.HasIndex(x => x.BookId);
            // Off-row unbounded text on SQL Server; left as the provider default
            // (TEXT) elsewhere so the model stays provider-agnostic (e.g. SQLite
            // in relational tests) instead of emitting SQL-Server-only syntax.
            if (Database.IsSqlServer())
                e.Property(x => x.Content).HasColumnType("nvarchar(max)");
            e.HasOne(x => x.Book).WithMany()
                .HasForeignKey(x => x.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TextIndexWord>(e =>
        {
            e.Property(x => x.Word).HasMaxLength(64);
            e.HasKey(x => new { x.Word, x.TextIndexId });   // clustered → prefix seek on Word
            e.HasOne(x => x.TextIndex).WithMany()
                .HasForeignKey(x => x.TextIndexId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OpenLibraryAuthor>(e =>
        {
            e.Property(x => x.OlKey).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.NormalizedName).HasMaxLength(300);
            e.Property(x => x.PersonalName).HasMaxLength(300);
            if (Database.IsSqlServer())
                e.Property(x => x.AlternateNames).HasColumnType("nvarchar(max)");
            e.Property(x => x.BirthDate).HasMaxLength(100);
            e.Property(x => x.DeathDate).HasMaxLength(100);
            e.HasIndex(x => x.OlKey).IsUnique();
            e.HasIndex(x => x.NormalizedName);
        });
    }
}
