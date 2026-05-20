using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers AuthorFolderNameResolver's collision rule: only authors that share a
// normalised name AND are not linked together AND all have an OL key receive
// the disambiguating "_<OLKey>" suffix.
public class AuthorFolderNameResolverTests
{
    private static Author A(int id, string name, string? olKey = null,
        int? linkedTo = null, bool isPenName = false) =>
        new()
        {
            Id = id,
            Name = name,
            OpenLibraryKey = olKey,
            LinkedToAuthorId = linkedTo,
            IsPenName = isPenName,
        };

    // ── No collision → no suffix ─────────────────────────────────────────────

    [Fact]
    public void SingleAuthor_KeepsBareName()
    {
        var list = new[] { A(1, "Terry Brooks", "OL123A") };
        Assert.Equal("Terry Brooks", AuthorFolderNameResolver.Resolve(list[0], list));
    }

    [Fact]
    public void DistinctNames_KeepBareNames()
    {
        var list = new[] {
            A(1, "Terry Brooks", "OL123A"),
            A(2, "Isaac Asimov", "OL456A"),
        };
        Assert.Equal("Terry Brooks", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("Isaac Asimov", AuthorFolderNameResolver.Resolve(list[1], list));
    }

    // ── Collision with full OL keys → both get suffix ────────────────────────

    [Fact]
    public void Collision_BothHaveOlKey_BothGetSuffix()
    {
        var list = new[] {
            A(1, "John Smith", "OL111A"),
            A(2, "John Smith", "OL222A"),
        };
        Assert.Equal("John Smith_OL111A", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("John Smith_OL222A", AuthorFolderNameResolver.Resolve(list[1], list));
    }

    [Fact]
    public void Collision_ThreeAuthorsSameName_AllThreeGetSuffix()
    {
        var list = new[] {
            A(1, "John Smith", "OL111A"),
            A(2, "John Smith", "OL222A"),
            A(3, "John Smith", "OL333A"),
        };
        Assert.Equal("John Smith_OL111A", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("John Smith_OL222A", AuthorFolderNameResolver.Resolve(list[1], list));
        Assert.Equal("John Smith_OL333A", AuthorFolderNameResolver.Resolve(list[2], list));
    }

    // ── Collision but a member is missing its OL key → no suffix yet ─────────

    [Fact]
    public void Collision_OneMemberMissingOlKey_NoSuffix()
    {
        // We refuse to disambiguate the group until every member has an OL
        // key, so the layout doesn't change shape across runs as keys land.
        var list = new[] {
            A(1, "John Smith", "OL111A"),
            A(2, "John Smith", olKey: null),
        };
        Assert.Equal("John Smith", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("John Smith", AuthorFolderNameResolver.Resolve(list[1], list));
    }

    // ── Linked authors don't count as collisions ─────────────────────────────

    [Fact]
    public void LinkedChild_NotCountedAsCollision()
    {
        // Author 2 is a duplicate of author 1 — user said they're the same person.
        // Even though they share a normalised name, neither should be disambiguated.
        var list = new[] {
            A(1, "John Smith", "OL111A"),
            A(2, "John Smith", "OL222A", linkedTo: 1),
        };
        Assert.Equal("John Smith", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("John Smith", AuthorFolderNameResolver.Resolve(list[1], list));
    }

    [Fact]
    public void LinkedCanonical_NotCountedAsCollision()
    {
        // Same as above but from the canonical's perspective.
        var list = new[] {
            A(1, "John Smith", "OL111A"),
            A(2, "John Smith", "OL222A", linkedTo: 1, isPenName: true),
        };
        Assert.Equal("John Smith", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("John Smith", AuthorFolderNameResolver.Resolve(list[1], list));
    }

    [Fact]
    public void SiblingPenNames_NotCountedAsCollision()
    {
        // Two pen names of the SAME canonical share a normalised name —
        // resolver must treat them as already-merged, not as a fresh collision.
        var list = new[] {
            A(1, "Real Author", "OL000A"),
            A(2, "Pseudonym A", "OL111A", linkedTo: 1, isPenName: true),
            A(3, "Pseudonym A", "OL222A", linkedTo: 1, isPenName: true),
        };
        Assert.Equal("Pseudonym A", AuthorFolderNameResolver.Resolve(list[1], list));
        Assert.Equal("Pseudonym A", AuthorFolderNameResolver.Resolve(list[2], list));
    }

    // ── Mixed collision: linked pair + one unlinked twin → unlinked stays bare,
    //    the two linked authors share a name with the twin but only the twin is
    //    "really" a collision target.

    [Fact]
    public void Collision_DuplicateAndUnlinkedTwin()
    {
        // Author 2 is linked into author 1.
        // Author 3 also happens to be called "John Smith" but is unlinked.
        // → Author 3 vs Author 1 is a real collision, so both get suffixed.
        //    Author 2 is dragged along because it shares the name with author 3
        //    (and is NOT linked to 3), so author 3 + author 2 also collide.
        var list = new[] {
            A(1, "John Smith", "OL111A"),
            A(2, "John Smith", "OL222A", linkedTo: 1),
            A(3, "John Smith", "OL333A"),
        };
        // Authors 1 and 3 are unlinked twins → both suffixed.
        Assert.Equal("John Smith_OL111A", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("John Smith_OL333A", AuthorFolderNameResolver.Resolve(list[2], list));
        // Author 2 is linked to 1, but unlinked to 3 → also suffixed because
        // its group with 3 has 2+ unlinked members.
        Assert.Equal("John Smith_OL222A", AuthorFolderNameResolver.Resolve(list[1], list));
    }

    // ── Case-insensitivity / diacritic-stripping in the name key ─────────────

    [Fact]
    public void DiacriticVariants_StillCollide()
    {
        // NormalizeAuthor decomposes diacritics, so "García" and "Garcia"
        // collapse to the same name key and these two count as a collision.
        // Note: characters that aren't formal "combining marks" — like Polish ł
        // — won't decompose, so this test deliberately uses an accent that does.
        var list = new[] {
            A(1, "Juan García", "OL111A"),
            A(2, "Juan Garcia", "OL222A"),
        };
        Assert.Equal("Juan García_OL111A", AuthorFolderNameResolver.Resolve(list[0], list));
        Assert.Equal("Juan Garcia_OL222A", AuthorFolderNameResolver.Resolve(list[1], list));
    }

    // ── FindCollisionGroup convenience ───────────────────────────────────────

    [Fact]
    public void FindCollisionGroup_ReturnsAllMembers()
    {
        var list = new[] {
            A(1, "John Smith", "OL111A"),
            A(2, "John Smith", "OL222A"),
            A(3, "Isaac Asimov", "OL999A"),
        };
        var group = AuthorFolderNameResolver.FindCollisionGroup(list[0], list);
        Assert.Equal(2, group.Count);
        Assert.Contains(group, a => a.Id == 1);
        Assert.Contains(group, a => a.Id == 2);
    }

    [Fact]
    public void FindCollisionGroup_NoCollision_ReturnsOnlySelf()
    {
        var list = new[] { A(1, "Solo Author", "OL111A") };
        var group = AuthorFolderNameResolver.FindCollisionGroup(list[0], list);
        Assert.Single(group);
        Assert.Equal(1, group[0].Id);
    }

    [Fact]
    public void EmptyName_ReturnsEmpty()
    {
        var list = new[] { A(1, "") };
        Assert.Equal("", AuthorFolderNameResolver.Resolve(list[0], list));
    }
}
