using System.Data;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.OpenLibrary;

// Pulls the OpenLibrary authors bulk dump and bulk-inserts it into
// OpenLibraryAuthors. The dump is ~2 GB compressed with tens of millions of
// rows, so everything streams: download chunks to a .part file (resumable),
// GZipStream → StreamReader → TSV/JSON parse → DataTable → SqlBulkCopy.
// Existing rows are truncated before reseeding so this is idempotent.
public sealed class AuthorDumpSeeder
{
    public const string DumpUrl = "https://openlibrary.org/data/ol_dump_authors_latest.txt.gz";
    private const int BatchSize = 20_000;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _cfg;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthorDumpSeeder> _log;

    public AuthorDumpSeeder(
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        IServiceScopeFactory scopeFactory,
        ILogger<AuthorDumpSeeder> log)
    {
        _httpFactory = httpFactory;
        _cfg = cfg;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public string DumpFilePath
    {
        get
        {
            var dir = _cfg["OpenLibrary:DumpCacheDir"];
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Path.GetTempPath(), "TheLibrary");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ol_dump_authors_latest.txt.gz");
        }
    }

    public sealed record SeedProgress(
        string Stage,
        long DownloadedBytes,
        long? TotalBytes,
        long Parsed,
        long Inserted);

    public async Task<SeedProgress> SeedAsync(Action<SeedProgress> onProgress, CancellationToken ct)
    {
        var downloaded = await EnsureDumpDownloadedAsync(
            (done, total) => onProgress(new SeedProgress("Downloading dump", done, total, 0, 0)),
            ct);

        onProgress(new SeedProgress("Preparing target table", downloaded, downloaded, 0, 0));
        var connStr = _cfg.GetConnectionString("Library")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Library");

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            // TRUNCATE is faster but the uniqueness index + future FKs make
            // DELETE safer. For millions of rows DELETE is still fine here
            // because the table has no dependent FKs.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM OpenLibraryAuthors", ct);
        }

        long parsed = 0;
        long inserted = 0;
        var importedAt = DateTime.UtcNow;
        var table = CreateDataTable();

        using var file = File.OpenRead(DumpFilePath);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8, false, 1 << 20);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            parsed++;

            var row = ParseLine(line, importedAt);
            if (row is not null) table.Rows.Add(row);

            if (table.Rows.Count >= BatchSize)
            {
                await BulkInsertAsync(connStr, table, ct);
                inserted += table.Rows.Count;
                table.Clear();
                onProgress(new SeedProgress("Importing authors", file.Position, downloaded, parsed, inserted));
            }
            else if (parsed % 100_000 == 0)
            {
                onProgress(new SeedProgress("Importing authors", file.Position, downloaded, parsed, inserted));
            }
        }

        if (table.Rows.Count > 0)
        {
            await BulkInsertAsync(connStr, table, ct);
            inserted += table.Rows.Count;
        }

        var final = new SeedProgress("Done", downloaded, downloaded, parsed, inserted);
        onProgress(final);
        _log.LogInformation("Seeded {Inserted} authors from dump ({Parsed} lines parsed)", inserted, parsed);
        return final;
    }

    private async Task<long> EnsureDumpDownloadedAsync(Action<long, long?> onProgress, CancellationToken ct)
    {
        var path = DumpFilePath;
        var tmp = path + ".part";

        using var http = _httpFactory.CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TheLibrary/1.0 (self-hosted collection manager)");

        long? totalSize = null;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, DumpUrl);
            using var hr = await http.SendAsync(head, ct);
            if (hr.IsSuccessStatusCode) totalSize = hr.Content.Headers.ContentLength;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "HEAD request failed; proceeding without known total size");
        }

        if (File.Exists(path) && totalSize.HasValue)
        {
            var existingSize = new FileInfo(path).Length;
            if (existingSize == totalSize.Value)
            {
                onProgress(existingSize, totalSize);
                return existingSize;
            }
            _log.LogInformation("Cached dump size {Have} != server {Want}; redownloading", existingSize, totalSize);
            File.Delete(path);
        }

        long resumeFrom = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, DumpUrl);
        if (resumeFrom > 0)
            req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        totalSize ??= (resp.Content.Headers.ContentLength ?? 0) + resumeFrom;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(
            tmp,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            var total = resumeFrom;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                total += read;
                onProgress(total, totalSize);
            }
        }

        File.Move(tmp, path, overwrite: true);
        return new FileInfo(path).Length;
    }

    private static DataTable CreateDataTable()
    {
        var t = new DataTable();
        t.Columns.Add("OlKey", typeof(string));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("NormalizedName", typeof(string));
        t.Columns.Add("PersonalName", typeof(string));
        t.Columns.Add("AlternateNames", typeof(string));
        t.Columns.Add("BirthDate", typeof(string));
        t.Columns.Add("DeathDate", typeof(string));
        t.Columns.Add("ImportedAt", typeof(DateTime));
        return t;
    }

    // Dump line format: type \t key \t revision \t last_modified \t json
    private static object?[]? ParseLine(string line, DateTime importedAt)
    {
        Span<int> tabs = stackalloc int[4];
        int idx = 0;
        for (int i = 0; i < line.Length && idx < 4; i++)
            if (line[i] == '\t') tabs[idx++] = i;
        if (idx < 4) return null;

        if (!line.AsSpan(0, tabs[0]).SequenceEqual("/type/author".AsSpan()))
            return null;

        var keyRaw = line.AsSpan(tabs[0] + 1, tabs[1] - tabs[0] - 1);
        var olKey = keyRaw.StartsWith("/authors/".AsSpan())
            ? keyRaw["/authors/".Length..].ToString()
            : keyRaw.ToString();

        var json = line.AsSpan(tabs[3] + 1).ToString();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(name)) return null;

            var personal = GetString(root, "personal_name");
            var birth = GetString(root, "birth_date");
            var death = GetString(root, "death_date");

            string? alt = null;
            if (root.TryGetProperty("alternate_names", out var altEl) && altEl.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>(capacity: altEl.GetArrayLength());
                foreach (var item in altEl.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var v = item.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) parts.Add(v);
                    }
                if (parts.Count > 0) alt = string.Join("; ", parts);
            }

            return new object?[]
            {
                Trunc(olKey, 32) ?? (object)DBNull.Value,
                Trunc(name, 300)!,
                Trunc(TitleNormalizer.NormalizeAuthor(name), 300)!,
                (object?)Trunc(personal, 300) ?? DBNull.Value,
                (object?)Trunc(alt, 2000) ?? DBNull.Value,
                (object?)Trunc(birth, 100) ?? DBNull.Value,
                (object?)Trunc(death, 100) ?? DBNull.Value,
                importedAt,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? Trunc(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];

    private static async Task BulkInsertAsync(string connStr, DataTable table, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            DestinationTableName = "OpenLibraryAuthors",
            BatchSize = 10_000,
            BulkCopyTimeout = 600,
        };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(table, ct);
    }
}
