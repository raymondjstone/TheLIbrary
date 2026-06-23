using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;

namespace TheLibrary.Server.Services.Sync;

public sealed record UntrackedAssignOutcome(
    bool Assigned, int? AuthorId, string? AuthorName, int? BookId, string? Path, string? Reason);

public sealed record EnsuredOpenLibraryBook(Book? Book, string? Error);

// Aim 2: take an UNTRACKED __unknown file, work out whose it is, and file it
// under that author. Scoped — shares the request's (or job scope's) DbContext so
// the caller's change tracking stays coherent. Extracted from AuthorsController
// so the same logic serves both the Identified-page endpoints and the
// "assign-authors" scheduled job.
public sealed class UntrackedAuthorAssigner
{
    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly IFileSystem _fs;

    public UntrackedAuthorAssigner(LibraryDbContext db, OpenLibraryClient ol, IFileSystem fs)
    {
        _db = db;
        _ol = ol;
        _fs = fs;
    }

    // Files one scan row's file under its author. The author is resolved the most
    // reliable way available:
    //   1. the OpenLibrary work for the guessed ISBN/title — gives a real author
    //      (matched/created by OL key) and also links the book; else
    //   2. an existing author whose name matches the guessed author; else
    //   3. a new Pending author created from the guessed name — but only when the
    //      name is a real OpenLibrary author.
    // The file is then moved into that author's folder and tracked as one of
    // their unmatched files. Saves its own changes.
    public async Task<UntrackedAssignOutcome> AssignAsync(BookContentScan scan, CancellationToken ct)
    {
        var sourcePath = scan.FullPath;
        if (!_fs.FileExists(sourcePath) && !_fs.DirectoryExists(sourcePath))
            return new UntrackedAssignOutcome(false, null, null, null, null, "File no longer exists on disk.");

        var existingFile = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == sourcePath, ct);
        if (existingFile?.AuthorId is not null)
            return new UntrackedAssignOutcome(false, null, null, null, null, "This file is already linked to an author.");

        var (root, rootError) = await ResolveDestinationRootAsync(sourcePath, ct);
        if (root is null)
            return new UntrackedAssignOutcome(false, null, null, null, null, rootError);

        // The scan row's guesses come from prose parsing or an (often truncated)
        // filename. The file's own embedded metadata is usually the cleaner
        // signal — when its author validates against the catalogue, its author
        // and full title drive the OpenLibrary search instead.
        var embedded = FileMetadataReader.TryRead(sourcePath);
        var embeddedAuthor = await AuthorNameValidator.ValidateAsync(_db, embedded?.Author, ct);
        // CleanForSearch: a query string carrying control characters (binary
        // junk from a corrupt header, or an old scan row that stored one) gets
        // 403'd by OpenLibrary's WAF and used to fail the whole request.
        var searchTitle = CleanForSearch(embeddedAuthor is not null && !string.IsNullOrWhiteSpace(embedded!.Title)
            ? embedded.Title : scan.Title);
        var searchAuthor = CleanForSearch(embeddedAuthor ?? scan.Author);
        var searchIsbn = CleanForSearch(!string.IsNullOrWhiteSpace(scan.Isbn) ? scan.Isbn
            : embeddedAuthor is not null ? embedded!.Isbn : null);

        // Content + embedded metadata gave us NOTHING (no title and no author) —
        // the common case for quarantined .txt/.pdf/.mobi with no front matter.
        // The FILENAME usually still carries "<Title> - <Author>"
        // ("Zero Sight - B. Justin Shier.mobi"), so interpret it for both. Skip
        // the literal "Unknown" placeholder the importer stamps on truly-unknown
        // files. The downstream OpenLibrary search + author validation reject a
        // junk guess, so this only ever helps — it can't mis-file.
        if (string.IsNullOrWhiteSpace(searchTitle) && string.IsNullOrWhiteSpace(searchAuthor))
        {
            var guess = FilenameGuesser.Interpret(sourcePath).FirstOrDefault(g =>
                !string.IsNullOrWhiteSpace(g.Title) && !string.IsNullOrWhiteSpace(g.Author)
                && !string.Equals(g.Author, "Unknown", StringComparison.OrdinalIgnoreCase));
            if (guess is not null)
            {
                searchTitle = CleanForSearch(guess.Title);
                searchAuthor = CleanForSearch(guess.Author);
            }
        }

        // An author guess with NO title is a dead end: the OL work search never
        // runs, and an author missing from the local OL dump can then never be
        // confirmed — such rows used to be retried forever. Recover the title
        // from the embedded metadata (unvalidated is fine, it only feeds the
        // search) or from the filename interpretation that AGREES with the
        // author guess ("Through Death - Parker Jaysen.azw3").
        if (string.IsNullOrWhiteSpace(searchTitle))
        {
            if (!string.IsNullOrWhiteSpace(embedded?.Title))
                searchTitle = CleanForSearch(embedded!.Title);
            if (searchTitle is null && !string.IsNullOrWhiteSpace(searchAuthor))
            {
                var wanted = TitleNormalizer.NormalizeAuthor(searchAuthor);
                searchTitle = FilenameGuesser.Interpret(sourcePath)
                    .FirstOrDefault(g => g.Title is not null && g.Author is not null
                        && TitleNormalizer.NormalizeAuthor(g.Author) == wanted)?.Title;
            }
        }

        // 1. Try OpenLibrary (ISBN, else title + author) — best author + a book.
        Author? author = null;
        Book? book = null;
        WorkSearchResponse? search = null;
        if (!string.IsNullOrWhiteSpace(searchIsbn))
            search = await _ol.SearchByIsbnAsync(searchIsbn, ct);
        if ((search?.Docs is null || search.Docs.Count == 0) && !string.IsNullOrWhiteSpace(searchTitle))
            search = await _ol.SearchWorksAsync(searchTitle!, searchAuthor, ct);
        var doc = search?.Docs?.FirstOrDefault();
        if (doc is not null && !string.IsNullOrWhiteSpace(doc.Key))
        {
            author = await ResolveTargetAuthorAsync(
                null, doc.AuthorKeys?.FirstOrDefault(),
                doc.AuthorNames?.FirstOrDefault(),
                doc.AuthorNames is { Count: > 0 } ? string.Join(", ", doc.AuthorNames) : null, ct);
            if (author is not null)
            {
                var add = await EnsureOpenLibraryBookAsync(
                    author.Id, doc.Key, doc.Title, doc.FirstPublishYear, doc.CoverId, owned: false, ct);
                book = add.Book; // a book-creation hiccup shouldn't block the author assignment
            }
        }

        // 2/3. Fall back to the guessed author name — reuse an existing author, or
        //      create a Pending one.
        if (author is null && !string.IsNullOrWhiteSpace(searchAuthor))
            author = await ResolveAuthorByNameAsync(searchAuthor!.Trim(), ct);

        if (author is null)
            return new UntrackedAssignOutcome(false, null, null, null, null,
                string.IsNullOrWhiteSpace(searchAuthor)
                    ? "Couldn't determine an author — no usable ISBN, title, or author was guessed for this file."
                    : $"Couldn't confirm \"{searchAuthor}\" — not found via OpenLibrary, so no author was created.");

        var file = existingFile ?? new LocalBookFile();
        if (existingFile is null) _db.LocalBookFiles.Add(file);

        var finalPath = await MoveUntrackedPathToAuthorFolderAsync(sourcePath, root, null, author, ct);
        file.AuthorId = author.Id;
        file.BookId = book?.Id;
        file.ManuallyUnmatched = false;
        file.AuthorFolder = author.CalibreFolderName ?? author.Name;
        file.TitleFolder = Directory.Exists(finalPath)
            ? Path.GetFileName(finalPath)
            : Path.GetFileNameWithoutExtension(finalPath);
        file.FullPath = finalPath;
        file.NormalizedTitle = TitleNormalizer.Normalize(file.TitleFolder);
        file.ResetIntegrity(); // moved into the author folder — re-check it there

        // The file now lives under an author, so the scan row follows it. Keep the
        // row when it carries a series catalogue (now tagged with its author) so the
        // same Build-series action as the tracked rows can still be run on it;
        // otherwise it's done, so clear it from the review list.
        scan.FullPath = finalPath;
        scan.AuthorId = author.Id;
        scan.Source = "unmatched";
        scan.Reviewed = scan.SeriesCatalogJson is null;

        await _db.SaveChangesAsync(ct);
        return new UntrackedAssignOutcome(true, author.Id, author.Name, book?.Id, finalPath, null);
    }

    // Files a scan row's file under a USER-CHOSEN OpenLibrary work — the
    // Identified page's "Find on OL" match. Unlike AssignAsync there is no
    // searching or guessing: the user picked the exact work, so the author is
    // resolved/created from the work doc, the Book is ensured, and the file
    // moves into the author's folder linked to that book. The scan row follows
    // the file and leaves the review list (unless it carries a series
    // catalogue, which stays for apply-catalog — same rule as AssignAsync).
    public async Task<UntrackedAssignOutcome> AssignToWorkAsync(
        BookContentScan scan,
        string? workKey,
        string? title,
        int? firstPublishYear,
        int? coverId,
        string? authors,
        string? primaryAuthorKey,
        string? primaryAuthorName,
        CancellationToken ct)
    {
        var sourcePath = scan.FullPath;
        if (!_fs.FileExists(sourcePath) && !_fs.DirectoryExists(sourcePath))
            return new UntrackedAssignOutcome(false, null, null, null, null, "File no longer exists on disk.");

        var author = await ResolveTargetAuthorAsync(null, primaryAuthorKey, primaryAuthorName, authors, ct);
        if (author is null)
            return new UntrackedAssignOutcome(false, null, null, null, null,
                "Could not determine the OpenLibrary author for this work.");

        var add = await EnsureOpenLibraryBookAsync(author.Id, workKey, title, firstPublishYear, coverId, owned: false, ct);
        if (add.Error is not null)
            return new UntrackedAssignOutcome(false, null, null, null, null, add.Error);

        var (root, rootError) = await ResolveDestinationRootAsync(sourcePath, ct);
        if (root is null)
            return new UntrackedAssignOutcome(false, null, null, null, null, rootError);

        var existingFile = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == sourcePath, ct);
        var file = existingFile ?? new LocalBookFile();
        if (existingFile is null) _db.LocalBookFiles.Add(file);

        var finalPath = await MoveUntrackedPathToAuthorFolderAsync(sourcePath, root, null, author, ct);
        file.AuthorId = author.Id;
        file.BookId = add.Book!.Id;
        file.ManuallyUnmatched = false;
        file.AuthorFolder = author.CalibreFolderName ?? author.Name;
        file.TitleFolder = Directory.Exists(finalPath)
            ? Path.GetFileName(finalPath)
            : Path.GetFileNameWithoutExtension(finalPath);
        file.FullPath = finalPath;
        file.NormalizedTitle = TitleNormalizer.Normalize(file.TitleFolder);
        file.ResetIntegrity();

        scan.FullPath = finalPath;
        scan.AuthorId = author.Id;
        scan.Author = author.Name;
        if (!string.IsNullOrWhiteSpace(title)) scan.Title = title.Length <= 500 ? title : title[..500];
        scan.Source = "unmatched";
        scan.Reviewed = scan.SeriesCatalogJson is null;

        await _db.SaveChangesAsync(ct);
        return new UntrackedAssignOutcome(true, author.Id, author.Name, add.Book.Id, finalPath, null);
    }

    // The reserved catch-all author for files whose real author can't be
    // determined. Kept out of OpenLibrary refresh (no key, Status=NotFound) and
    // out of pruning (CreationSource "manual"); it still owns files and can carry
    // books linked to real OL works.
    public const string UnknownAuthorName = "Unknown Author";

    public async Task<Author> EnsureUnknownAuthorAsync(CancellationToken ct)
    {
        var existing = await _db.Authors.FirstOrDefaultAsync(
            a => a.Name == UnknownAuthorName && a.OpenLibraryKey == null, ct);
        if (existing is not null) return existing;

        var author = new Author
        {
            Name = UnknownAuthorName,
            CalibreFolderName = UnknownAuthorName,
            Status = AuthorStatus.NotFound,   // never refreshed / key-resolved
            CreationSource = "manual",        // never pruned
            // Far future so the refresh-works selector (which picks NextFetchAt==null)
            // never schedules it; the refresher also hard-skips it by name.
            NextFetchAt = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        _db.Authors.Add(author);
        await _db.SaveChangesAsync(ct);
        return author;
    }

    // Files an untracked scan's file under a CHOSEN author (no OpenLibrary
    // resolution, no book) — used to drop a file into the "Unknown Author" bucket.
    // The file moves into the author's folder and is left unmatched (no book), so
    // the row stays reviewable and a book can still be attached afterwards.
    public async Task<UntrackedAssignOutcome> AssignToAuthorAsync(BookContentScan scan, Author author, CancellationToken ct)
    {
        var sourcePath = scan.FullPath;
        if (!_fs.FileExists(sourcePath) && !_fs.DirectoryExists(sourcePath))
            return new UntrackedAssignOutcome(false, null, null, null, null, "File no longer exists on disk.");

        var (root, rootError) = await ResolveDestinationRootAsync(sourcePath, ct);
        if (root is null)
            return new UntrackedAssignOutcome(false, null, null, null, null, rootError);

        var existingFile = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == sourcePath, ct);
        var file = existingFile ?? new LocalBookFile();
        if (existingFile is null) _db.LocalBookFiles.Add(file);

        var finalPath = await MoveUntrackedPathToAuthorFolderAsync(sourcePath, root, null, author, ct);
        file.AuthorId = author.Id;
        file.BookId = null;
        file.ManuallyUnmatched = false;
        file.AuthorFolder = author.CalibreFolderName ?? author.Name;
        file.TitleFolder = Directory.Exists(finalPath) ? Path.GetFileName(finalPath) : Path.GetFileNameWithoutExtension(finalPath);
        file.FullPath = finalPath;
        file.NormalizedTitle = TitleNormalizer.Normalize(file.TitleFolder);
        file.ResetIntegrity();

        scan.FullPath = finalPath;
        scan.AuthorId = author.Id;
        scan.Source = "unmatched";
        scan.Reviewed = false;   // keep it reviewable so a book can still be matched
        await _db.SaveChangesAsync(ct);
        return new UntrackedAssignOutcome(true, author.Id, author.Name, null, finalPath, null);
    }

    // Attaches a user-chosen OpenLibrary work to a file that is ALREADY filed under
    // an author, keeping that author (no move, no re-resolution). This is what lets
    // a file parked under "Unknown Author" still be matched to the right book on
    // OpenLibrary without changing its author.
    public async Task<UntrackedAssignOutcome> LinkBookKeepingCurrentAuthorAsync(
        BookContentScan scan, string? workKey, string? title, int? firstPublishYear, int? coverId, CancellationToken ct)
    {
        if (scan.AuthorId is not int authorId)
            return new UntrackedAssignOutcome(false, null, null, null, null, "File is not filed under an author yet.");

        var add = await EnsureOpenLibraryBookAsync(authorId, workKey, title, firstPublishYear, coverId, owned: false, ct);
        if (add.Error is not null)
            return new UntrackedAssignOutcome(false, null, null, null, null, add.Error);

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == scan.FullPath, ct);
        if (file is not null) file.BookId = add.Book!.Id;

        if (!string.IsNullOrWhiteSpace(title)) scan.Title = title!.Length <= 500 ? title : title[..500];
        scan.Reviewed = scan.SeriesCatalogJson is null;   // done unless a catalogue keeps it
        await _db.SaveChangesAsync(ct);

        var name = await _db.Authors.Where(a => a.Id == authorId).Select(a => a.Name).FirstOrDefaultAsync(ct);
        return new UntrackedAssignOutcome(true, authorId, name, add.Book!.Id, scan.FullPath, null);
    }

    // Which enabled library root should a file's author folder live under?
    // Files in the custom quarantine folder are intentionally outside every
    // library location — the primary (or first enabled) location stands in.
    // Resolves the library root that an untracked file should be filed under,
    // given its current on-disk path. A file living under __unknown (especially
    // a CUSTOM __unknown path that sits outside every library location) must NOT
    // be filed relative to __unknown — its author folder belongs under a real
    // library root, so fall back to the primary/first enabled location.
    public async Task<(string? Root, string? Error)> ResolveDestinationRootAsync(
        string sourcePath, CancellationToken ct)
    {
        var locations = await _db.LibraryLocations.AsNoTracking().Where(l => l.Enabled).ToListAsync(ct);
        var roots = locations.Select(l => l.Path).ToList();
        var root = roots.FirstOrDefault(r => sourcePath.StartsWith(
            r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (root is not null) return (root, null);

        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        var isUnderCustom = customUnknown is not null && sourcePath.StartsWith(
            customUnknown.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        if (!isUnderCustom)
            return (null, "File is outside all enabled library locations.");

        root = locations.FirstOrDefault(l => l.IsPrimary)?.Path ?? roots.FirstOrDefault();
        return root is null
            ? (null, "No enabled library location is configured.")
            : (root, null);
    }

    // Resolves a guessed author name to an Author for the untracked-assign
    // fallback (used only when OpenLibrary couldn't resolve the work itself):
    //   1. reuse an existing watchlist Author with that name; else
    //   2. only if the name matches a real OpenLibrary author (the OL authors
    //      catalogue), create a Pending Author tied to that OL key + name.
    // A name that isn't a known OL author yields null — we never invent an author
    // out of an unverifiable guess.
    private async Task<Author?> ResolveAuthorByNameAsync(string name, CancellationToken ct)
    {
        var existing = await _db.Authors.FirstOrDefaultAsync(a => a.Name == name, ct);
        if (existing is not null) return existing;

        var normalized = TitleNormalizer.NormalizeAuthor(name);
        var olRow = await _db.OpenLibraryAuthors
            .FirstOrDefaultAsync(o => o.NormalizedName == normalized, ct);
        if (olRow is null) return null; // not a known OL author — don't create

        // Reuse the watchlist author for that OL key if we already have one.
        var byKey = await _db.Authors.FirstOrDefaultAsync(a => a.OpenLibraryKey == olRow.OlKey, ct);
        if (byKey is not null) return byKey;

        var author = new Author { Name = olRow.Name, OpenLibraryKey = olRow.OlKey, Status = AuthorStatus.Pending, CreationSource = "assign" };
        _db.Authors.Add(author);
        await _db.SaveChangesAsync(ct);
        return author;
    }

    // Resolves the author an OpenLibrary work should be filed under: the current
    // author when the keys agree, an existing watchlist author owning the key,
    // else a new Pending author named from the work doc (fetching the OL author
    // record when the doc carries no name).
    public async Task<Author?> ResolveTargetAuthorAsync(
        Author? currentAuthor,
        string? primaryAuthorKey,
        string? primaryAuthorName,
        string? fallbackAuthors,
        CancellationToken ct)
    {
        var key = primaryAuthorKey?.Trim();
        if (string.IsNullOrWhiteSpace(key)) return currentAuthor;
        if (key.StartsWith("/authors/", StringComparison.OrdinalIgnoreCase))
            key = key[("/authors/".Length)..];

        if (currentAuthor is not null && string.Equals(currentAuthor.OpenLibraryKey, key, StringComparison.OrdinalIgnoreCase))
            return currentAuthor;

        var existing = await _db.Authors.FirstOrDefaultAsync(a => a.OpenLibraryKey == key, ct);
        if (existing is not null) return existing;

        var name = primaryAuthorName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = fallbackAuthors?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var fetched = await _ol.FetchAuthorAsync(key, ct);
                name = fetched?.Name?.Trim();
            }
            catch (OpenLibraryRequestFailedException)
            {
                name = null;
            }
        }

        if (string.IsNullOrWhiteSpace(name)) return null;

        var author = new Author
        {
            Name = name,
            OpenLibraryKey = key,
            Status = AuthorStatus.Pending,
            CreationSource = "assign",
        };
        _db.Authors.Add(author);
        await _db.SaveChangesAsync(ct);
        return author;
    }

    // Creates (or reuses) the author's Book row for an OpenLibrary work key.
    public async Task<EnsuredOpenLibraryBook> EnsureOpenLibraryBookAsync(
        int authorId,
        string? rawWorkKey,
        string? rawTitle,
        int? firstPublishYear,
        int? coverId,
        bool owned,
        CancellationToken ct)
    {
        var workKey = rawWorkKey?.Trim();
        if (string.IsNullOrWhiteSpace(workKey))
            return new EnsuredOpenLibraryBook(null, "OpenLibrary work key is required");
        if (workKey.StartsWith("/works/", StringComparison.OrdinalIgnoreCase))
            workKey = workKey[("/works/".Length)..];

        var existing = await _db.Books.FirstOrDefaultAsync(
            b => b.AuthorId == authorId && b.OpenLibraryWorkKey == workKey,
            ct);
        if (existing is not null)
        {
            if (owned && !existing.ManuallyOwned)
            {
                existing.ManuallyOwned = true;
                existing.ManuallyOwnedAt = DateTime.UtcNow;
            }
            return new EnsuredOpenLibraryBook(existing, null);
        }

        var cleanTitle = rawTitle?.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return new EnsuredOpenLibraryBook(null, "Title is required");

        var book = new Book
        {
            AuthorId = authorId,
            OpenLibraryWorkKey = workKey,
            Title = cleanTitle,
            NormalizedTitle = TitleNormalizer.Normalize(cleanTitle),
            FirstPublishYear = firstPublishYear,
            CoverId = coverId,
            ManuallyOwned = owned,
            ManuallyOwnedAt = owned ? DateTime.UtcNow : null,
            Subjects = "",
            // Past publish year → dated to 1 Jan of that year, not "now", so an
            // old title filed today doesn't surface as a new release.
            CreatedAt = Book.CreatedAtForPublishYear(firstPublishYear),
        };

        _db.Books.Add(book);
        await _db.SaveChangesAsync(ct);
        return new EnsuredOpenLibraryBook(book, null);
    }

    // Resolves the WORK for a file that is ALREADY filed under an author but has
    // no Book link, using its ISBN — the case AssignAsync deliberately skips
    // (it bails on author-linked files). The edition endpoint is tried first
    // (resolves ~2x the ISBNs the search index does), then the ISBN search. The
    // work is linked under the file's EXISTING author; the file is NOT moved (it
    // is already in the right folder). A lenient title check guards against a
    // mis-extracted/placeholder ISBN pointing at an unrelated work. Does not
    // save — the caller persists. Returns true when BookId was set.
    public async Task<bool> TryLinkWorkByIsbnAsync(LocalBookFile file, string? isbn, string? knownTitle, CancellationToken ct)
    {
        if (file.AuthorId is null) return false;
        var clean = CleanForSearch(isbn);
        if (string.IsNullOrWhiteSpace(clean)) return false;

        string? workKey = null, title = null; int? coverId = null, year = null;

        var edition = await _ol.ResolveEditionByIsbnAsync(clean!, ct);
        if (edition is not null)
        {
            workKey = edition.WorkKey; title = edition.Title; coverId = edition.CoverId;
        }
        else
        {
            var doc = (await _ol.SearchByIsbnAsync(clean!, ct))?.Docs?.FirstOrDefault();
            if (doc is not null && !string.IsNullOrWhiteSpace(doc.Key))
            {
                workKey = doc.Key; title = doc.Title; coverId = doc.CoverId; year = doc.FirstPublishYear;
            }
        }
        if (string.IsNullOrWhiteSpace(workKey)) return false;

        // Guard: if we have a confident local title AND it shares nothing with the
        // resolved work's title, the ISBN is likely wrong — don't mis-link.
        if (!string.IsNullOrWhiteSpace(knownTitle) && !string.IsNullOrWhiteSpace(title)
            && !TitlesShareSignal(knownTitle!, title!))
            return false;

        var add = await EnsureOpenLibraryBookAsync(file.AuthorId.Value, workKey, title, year, coverId, owned: false, ct);
        if (add.Book is null) return false;
        file.BookId = add.Book.Id;
        return true;
    }

    // Lenient agreement test: do two titles share at least one significant token,
    // or is one contained in the other? Deliberately permissive — it only rejects
    // a gross mismatch (a wrong ISBN pointing at an unrelated book), not subtitle
    // or punctuation differences.
    private static bool TitlesShareSignal(string a, string b)
    {
        var na = TitleNormalizer.Normalize(a);
        var nb = TitleNormalizer.Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return true; // nothing to disagree on
        if (na.Contains(nb) || nb.Contains(na)) return true;
        var ta = na.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToHashSet();
        var tb = nb.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4);
        return ta.Count == 0 || tb.Any(ta.Contains);
    }

    // Moves an untracked file (or folder) into the target author's folder under
    // the given library root, keeping any relative sub-path and avoiding name
    // collisions. Returns the final path.
    public async Task<string> MoveUntrackedPathToAuthorFolderAsync(
        string sourcePath,
        string rootPath,
        string? relativePath,
        Author targetAuthor,
        CancellationToken ct)
    {
        var targetFolder = SanitizeSegment(targetAuthor.CalibreFolderName ?? targetAuthor.Name);
        if (string.IsNullOrWhiteSpace(targetAuthor.CalibreFolderName))
            targetAuthor.CalibreFolderName = targetFolder;

        var root = rootPath.TrimEnd('\\', '/');
        var relative = NormalizeRelativePath(relativePath);
        var destPath = string.IsNullOrWhiteSpace(relative)
            ? Path.Combine(root, targetFolder, Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            : Path.Combine(root, targetFolder, relative.Replace('/', Path.DirectorySeparatorChar));

        // Already in the right place — don't move onto self (otherwise the
        // UniqueFilePath/UniqueDirectoryPath collision check below would see the
        // existing entry and pointlessly rename it to "_2").
        if (FsPath.SameLocation(sourcePath, destPath))
            return sourcePath;

        if (File.Exists(sourcePath))
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(destDir))
                Directory.CreateDirectory(destDir);
            var final = UniqueFilePath(destPath);
            SafeMove.File(sourcePath, final);
            await PruneEmptyParentsAsync(Path.GetDirectoryName(sourcePath), root, ct);
            return final;
        }

        if (Directory.Exists(sourcePath))
        {
            var destParent = Path.GetDirectoryName(destPath) ?? Path.Combine(root, targetFolder);
            Directory.CreateDirectory(destParent);
            var final = UniqueDirectoryPath(destParent, Path.GetFileName(destPath));
            SafeMove.Directory(sourcePath, final);
            await PruneEmptyParentsAsync(Path.GetDirectoryName(sourcePath), root, ct);
            return final;
        }

        return destPath;
    }

    private static string? CleanForSearch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Any(char.IsControl) ? null : t;
    }

    // ---- Path helpers shared with AuthorsController (single source of truth) ----

    internal static string SanitizeSegment(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(InvalidSegmentChars.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(s) ? "returned" : s;
    }

    private static readonly HashSet<char> InvalidSegmentChars =
        new(Path.GetInvalidFileNameChars());

    internal static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts.Where(p => p != "." && p != ".."));
    }

    internal static string UniqueFilePath(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? "";
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(next) && !Directory.Exists(next)) return next;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
    }

    internal static string UniqueDirectoryPath(string parent, string leaf)
    {
        var candidate = Path.Combine(parent, leaf);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(parent, $"{leaf} ({i})");
            if (!Directory.Exists(next) && !File.Exists(next)) return next;
        }
        return Path.Combine(parent, $"{leaf} ({DateTime.UtcNow:yyyyMMddHHmmss})");
    }

    internal static async Task PruneEmptyParentsAsync(string? startPath, string stopRoot, CancellationToken ct)
    {
        var stop = Path.GetFullPath(stopRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = startPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(stop, StringComparison.OrdinalIgnoreCase) || string.Equals(full, stop, StringComparison.OrdinalIgnoreCase))
                break;
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
            {
                try { Directory.Delete(full); }
                catch { break; }
                current = Path.GetDirectoryName(full);
                continue;
            }
            break;
        }
        await Task.CompletedTask;
    }
}
