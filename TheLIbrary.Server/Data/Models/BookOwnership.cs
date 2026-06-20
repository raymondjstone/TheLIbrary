using System.Linq.Expressions;

namespace TheLibrary.Server.Data.Models;

// Canonical definition of "owned" for a Book. A book counts as owned via any of:
//   - an ebook file (LocalFiles),
//   - a manual physical copy (ManuallyOwned),
//   - a "got but in a different edition" mark (OwnedDifferentEdition).
//
// These expressions are the single source of truth for the LIST-INCLUSION filters
// — chain them onto a query with `.Where(BookOwnership.NotOwned)` (Missing / unowned
// lists) or `.Where(BookOwnership.Owned)` / `.CountAsync(BookOwnership.Owned)`.
//
// NOTE: EF Core can't inline a shared expression into a *projection* (`Owned = …`)
// or a GroupBy *aggregate* (`g.Count(b => …)`) without a composition library, so a
// handful of those sites still spell the predicate out. They carry a
// `// keep in sync with BookOwnership` comment and must be updated together if the
// rule ever changes.
public static class BookOwnership
{
    public static readonly Expression<Func<Book, bool>> Owned =
        b => b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any();

    public static readonly Expression<Func<Book, bool>> NotOwned =
        b => !b.ManuallyOwned && !b.OwnedDifferentEdition && !b.LocalFiles.Any();
}
