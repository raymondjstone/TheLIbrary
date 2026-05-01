using System.Data;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.OpenLibrary;

// Pulls the OpenLibrary authors bulk dump, stores it in the Books folder
// (parent of Calibre:Root), decompresses it fully, then upserts every author
// row into OpenLibraryAuthors via a temp-table MERGE so existing rows added by
// AuthorUpdateProcessor are updated rather than wiped.
//
// Flow: download .gz → decompress to .txt → bulk-load .txt into #StagingAuthors
//       → MERGE staging into OpenLibraryAuthors → delete .txt.
public sealed class AuthorDumpSeeder
{
    public const string DumpUrl = "https://openlibrary.org/data/ol_dump_authors_latest.txt.gz";
    private const int BatchSize = 20_000;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AuthorDumpSeeder> _log;

    public AuthorDumpSeeder(
        IHttpClientFactory httpFactory,
        IConfiguration cfg,
        ILogger<AuthorDumpSeeder> log)
    {
        _httpFactory = httpFactory;
        _cfg = cfg;
        _log = log;
    }

    public string DumpFilePath => Path.Combine(GetDumpDir(), "ol_dump_authors_latest.txt.gz");
    public string DumpTextPath => Path.Combine(GetDumpDir(), "ol_dump_authors_latest.txt");

    // Returns the Books folder (parent of Calibre:Root), falling back to temp.
    private string GetDumpDir()
    {
        var calibreRoot = _cfg["Calibre:Root"];
        if (!string.IsNullOrWhiteSpace(calibreRoot))
        {
            var parent = Path.GetDirectoryName(
                calibreRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
                return parent;
            }
        }
        var fallback = Path.Combine(Path.GetTempPath(), "TheLibrary");
        Directory.CreateDirectory(fallback);
        return fallback;
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

        await DecompressDumpAsync(
            (done, total) => onProgress(new SeedProgress("Decompressing dump", done, total, 0, 0)),
            ct);

        var connStr = _cfg.GetConnectionString("Library")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Library");

        long parsed = 0;
        long inserted = 0;
        var importedAt = DateTime.UtcNow;
        var batch = CreateDataTable();

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        // Temp table lives for the duration of this connection.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE #StagingAuthors (
                    OlKey          nvarchar(32)   NOT NULL,
                    Name           nvarchar(300)  NOT NULL,
                    NormalizedName nvarchar(300)  NOT NULL,
                    PersonalName   nvarchar(300)  NULL,
                    AlternateNames nvarchar(2000) NULL,
                    BirthDate      nvarchar(100)  NULL,
                    DeathDate      nvarchar(100)  NULL,
                    ImportedAt     datetime2      NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var textPath = DumpTextPath;
        var fileSize = new FileInfo(textPath).Length;
        using var file = File.OpenRead(textPath);
        using var reader = new StreamReader(file, Encoding.UTF8, false, 1 << 20);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            parsed++;

            var row = ParseLine(line, importedAt);
            if (row is not null) batch.Rows.Add(row);

            if (batch.Rows.Count >= BatchSize)
            {
                await BulkInsertIntoStagingAsync(conn, batch, ct);
                inserted += batch.Rows.Count;
                batch.Clear();
                onProgress(new SeedProgress("Importing authors", file.Position, fileSize, parsed, inserted));
            }
            else if (parsed % 100_000 == 0)
            {
                onProgress(new SeedProgress("Importing authors", file.Position, fileSize, parsed, inserted));
            }
        }

        if (batch.Rows.Count > 0)
        {
            await BulkInsertIntoStagingAsync(conn, batch, ct);
            inserted += batch.Rows.Count;
        }

        onProgress(new SeedProgress("Merging into catalog", fileSize, fileSize, parsed, inserted));

        // Index staging table before the MERGE join.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE CLUSTERED INDEX IX_Stage_OlKey ON #StagingAuthors (OlKey);";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                MERGE OpenLibraryAuthors WITH (HOLDLOCK) AS target
                USING #StagingAuthors AS source ON target.OlKey = source.OlKey
                WHEN MATCHED THEN UPDATE SET
                    target.Name            = source.Name,
                    target.NormalizedName  = source.NormalizedName,
                    target.PersonalName    = source.PersonalName,
                    target.AlternateNames  = source.AlternateNames,
                    target.BirthDate       = source.BirthDate,
                    target.DeathDate       = source.DeathDate,
                    target.ImportedAt      = source.ImportedAt
                WHEN NOT MATCHED BY TARGET THEN
                    INSERT (OlKey, Name, NormalizedName, PersonalName, AlternateNames,
                            BirthDate, DeathDate, ImportedAt)
                    VALUES (source.OlKey, source.Name, source.NormalizedName,
                            source.PersonalName, source.AlternateNames,
                            source.BirthDate, source.DeathDate, source.ImportedAt);";
            cmd.CommandTimeout = 1200;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        try { File.Delete(DumpTextPath); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not delete decompressed dump {Path}", DumpTextPath); }

        var final = new SeedProgress("Done", downloaded, downloaded, parsed, inserted);
        onProgress(final);
        _log.LogInformation("Upserted {Inserted} authors from dump ({Parsed} lines parsed)", inserted, parsed);
        return final;
    }

    // Decompresses DumpFilePath → DumpTextPath. Always starts fresh (deletes
    // any leftover .txt from a previous partial run).
    private async Task DecompressDumpAsync(Action<long, long?> onProgress, CancellationToken ct)
    {
        var gzPath = DumpFilePath;
        var txtPath = DumpTextPath;
        var gzSize = new FileInfo(gzPath).Length;

        if (File.Exists(txtPath)) File.Delete(txtPath);

        await using var src = File.OpenRead(gzPath);
        await using var gz = new GZipStream(src, CompressionMode.Decompress);
        await using var dst = new FileStream(txtPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        var buf = new byte[1 << 20];
        int read;
        while ((read = await gz.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            onProgress(src.Position, gzSize);
        }
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

    private static async Task BulkInsertIntoStagingAsync(SqlConnection conn, DataTable table, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, externalTransaction: null)
        {
            DestinationTableName = "#StagingAuthors",
            BatchSize = 10_000,
            BulkCopyTimeout = 600,
        };
        foreach (DataColumn col in table.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(table, ct);
    }
}
