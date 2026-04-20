using System.Text.Json.Serialization;

namespace TheLibrary.Server.Services.OpenLibrary;

public sealed class AuthorSearchResponse
{
    [JsonPropertyName("numFound")] public int NumFound { get; set; }
    [JsonPropertyName("docs")] public List<AuthorSearchDoc> Docs { get; set; } = new();
}

public sealed class AuthorSearchDoc
{
    [JsonPropertyName("key")] public string? Key { get; set; } // "OL1234A"
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("alternate_names")] public List<string>? AlternateNames { get; set; }
    [JsonPropertyName("work_count")] public int? WorkCount { get; set; }
    [JsonPropertyName("top_work")] public string? TopWork { get; set; }
    [JsonPropertyName("birth_date")] public string? BirthDate { get; set; }
    [JsonPropertyName("death_date")] public string? DeathDate { get; set; }
}

public sealed class WorkSearchResponse
{
    [JsonPropertyName("numFound")] public int NumFound { get; set; }
    [JsonPropertyName("start")] public int Start { get; set; }
    [JsonPropertyName("docs")] public List<WorkSearchDoc> Docs { get; set; } = new();
}

public sealed class WorkSearchDoc
{
    [JsonPropertyName("key")] public string? Key { get; set; } // "/works/OL1234W"
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("first_publish_year")] public int? FirstPublishYear { get; set; }
    [JsonPropertyName("cover_i")] public int? CoverId { get; set; }
    [JsonPropertyName("language")] public List<string>? Language { get; set; }
    [JsonPropertyName("author_key")] public List<string>? AuthorKeys { get; set; }
    [JsonPropertyName("author_name")] public List<string>? AuthorNames { get; set; }
    [JsonPropertyName("edition_count")] public int? EditionCount { get; set; }
}

// One entry from /recentchanges/.../merge-authors.json. Only the data payload
// (master survivor + the list of duplicates folded into it) is relevant to
// the local sync task — the other fields are diagnostic.
public sealed class AuthorMergeChange
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("data")] public AuthorMergeData? Data { get; set; }
}

public sealed class AuthorMergeData
{
    // "/authors/OL1234A" — the surviving author after the merge.
    [JsonPropertyName("master")] public string? Master { get; set; }
    // "/authors/OL5678A" keys that were redirected into master.
    [JsonPropertyName("duplicates")] public List<string>? Duplicates { get; set; }
}
