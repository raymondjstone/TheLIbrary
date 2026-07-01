namespace TheLibrary.Server.Services.OpenLibrary;

public enum IsbnLookupStatus
{
    Hit,          // this source resolved the ISBN
    Miss,         // this source definitively has no record of it
    Skipped,      // this source isn't configured (no credential) — contributes nothing
    Unavailable,  // couldn't answer right now (rate/quota/transient) — worth retrying later
}

// One fallback source's answer for an ISBN. Title/Author/Year are only meaningful
// when Status is Hit.
public sealed record IsbnLookupResult(
    IsbnLookupStatus Status, string? Title = null, string? Author = null, int? FirstPublishYear = null)
{
    public static readonly IsbnLookupResult Miss = new(IsbnLookupStatus.Miss);
    public static readonly IsbnLookupResult Skipped = new(IsbnLookupStatus.Skipped);
    public static readonly IsbnLookupResult Unavailable = new(IsbnLookupStatus.Unavailable);
    public static IsbnLookupResult Found(string title, string? author, int? year)
        => new(IsbnLookupStatus.Hit, title, author, year);
}

// A secondary ISBN → title/author source, tried in order after OpenLibrary when it
// has no record. Each provider owns its own credential (an AppSettings key) and its
// own rate limiting; the resolver reads the credential and passes it in, so the
// providers stay free of any DB dependency and can be singletons.
public interface IIsbnFallbackProvider
{
    // Short identifier for logs / UI (e.g. "Google Books").
    string Name { get; }

    // The AppSettings key holding this provider's credential. Blank credential ⇒ the
    // provider returns Skipped (it's off).
    string CredentialSettingKey { get; }

    Task<IsbnLookupResult> LookupAsync(string isbn, string? credential, CancellationToken ct);
}

// Thrown by IsbnResolutionService when no source resolved the ISBN AND at least one
// was temporarily Unavailable (rate/quota) — the result is NOT cached, so the ISBN is
// re-attempted on a later run (e.g. the next day, once a daily quota resets).
public sealed class IsbnLookupUnavailableException : Exception
{
    public IsbnLookupUnavailableException(string? message = null)
        : base(message ?? "An ISBN metadata source is temporarily unavailable — retrying later.") { }
}
