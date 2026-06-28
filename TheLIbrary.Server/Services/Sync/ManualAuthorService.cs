using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Sync;

// Shared creation path for authors the user adds by hand — people OpenLibrary
// doesn't list yet, or that we simply haven't matched. The author carries a
// synthetic "XX…A" key so a later promote pass can swap in the real OL key once
// OL is found to list them. Created Active with CreationSource "manual" so it
// behaves like any other author everywhere (lists, starring, owning books, and
// spared by the prune job, which only touches Pending/NotFound rows).
public sealed class ManualAuthorService
{
    private readonly LibraryDbContext _db;

    public ManualAuthorService(LibraryDbContext db) { _db = db; }

    // Conflict == true marks the "already exists" case (Author is the existing
    // row) so the caller can map it to HTTP 409 and point the user at it.
    public sealed record Result(Author? Author, string? Error, bool Conflict);

    public async Task<Result> CreateAsync(string? name, CancellationToken ct)
    {
        var cleanName = name?.Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
            return new Result(null, "Author name is required", false);
        if (cleanName.Length > 512)
            return new Result(null, "Author name is too long", false);

        // Don't quietly mint a duplicate of an author already in the library —
        // the user almost certainly wants the existing one. A case-insensitive
        // exact-name match (the DB collation handles case) catches the common
        // case cheaply; near-name variants can be linked/merged afterwards.
        var existing = await _db.Authors.FirstOrDefaultAsync(a => a.Name == cleanName, ct);
        if (existing is not null)
            return new Result(existing, $"An author named \"{existing.Name}\" already exists.", true);

        // Allocate a globally-unique synthetic author key.
        string key;
        do { key = ManualAuthorKey.NewCandidate(); }
        while (await _db.Authors.AnyAsync(a => a.OpenLibraryKey == key, ct));

        var author = new Author
        {
            OpenLibraryKey = key,
            Name = cleanName,
            Status = AuthorStatus.Active,   // behaves like a normal OL author
            CreationSource = "manual",
            Priority = 0,
            CreatedAt = DateTime.UtcNow,
            // null → the promote pass treats it as due to look up soonest; the OL
            // refresh ignores it (synthetic key) until promotion swaps in a real one.
            NextFetchAt = null,
        };
        _db.Authors.Add(author);
        await _db.SaveChangesAsync(ct);

        ActivityLogger.Record(_db, "manual-author-add",
            $"Added manual author \"{cleanName}\"", "user");
        await _db.SaveChangesAsync(ct);

        return new Result(author, null, false);
    }
}
