using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// A user-defined shelf ("To read 2026", "Favorites", …). Arbitrary grouping of
// books that cuts across authors/series, separate from the auto genre tags that
// come from OpenLibrary subjects.
public class Collection
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    // TitleNormalizer.Normalize(Name) — enforces case-insensitive uniqueness.
    [MaxLength(200)]
    public string NormalizedName { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public List<BookCollection> Books { get; set; } = new();
}

// Join row: a book's membership in a collection.
public class BookCollection
{
    public int CollectionId { get; set; }
    public Collection Collection { get; set; } = null!;

    public int BookId { get; set; }
    public Book Book { get; set; } = null!;

    public DateTime AddedAt { get; set; }
}
