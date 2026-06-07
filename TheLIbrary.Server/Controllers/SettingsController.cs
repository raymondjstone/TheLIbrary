using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Pushover;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly CoverCacheState _coverCache;
    public SettingsController(LibraryDbContext db, IWebHostEnvironment env, CoverCacheState coverCache)
    {
        _db = db;
        _env = env;
        _coverCache = coverCache;
    }

    public sealed record IncomingDto(string? Path, bool Exists);
    public sealed record UpdateIncoming(string? Path);

    [HttpGet("incoming")]
    public async Task<IncomingDto> GetIncoming(CancellationToken ct)
    {
        var s = await _db.AppSettings
            .FirstOrDefaultAsync(x => x.Key == AppSettingKeys.IncomingFolder, ct);
        var path = string.IsNullOrWhiteSpace(s?.Value) ? null : s!.Value;
        return new IncomingDto(path, path is not null && Directory.Exists(path));
    }

    [HttpPut("incoming")]
    public async Task<IncomingDto> SetIncoming([FromBody] UpdateIncoming body, CancellationToken ct)
    {
        var path = body.Path?.Trim() ?? "";
        var s = await _db.AppSettings
            .FirstOrDefaultAsync(x => x.Key == AppSettingKeys.IncomingFolder, ct);
        if (s is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = path });
        }
        else s.Value = path;
        await _db.SaveChangesAsync(ct);
        return new IncomingDto(string.IsNullOrWhiteSpace(path) ? null : path,
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    public sealed record UnknownFolderDto(string? Path, bool Exists, string DefaultDescription);
    public sealed record UpdateUnknownFolder(string? Path);
    public sealed record UnknownFolderMigrationDto(string? Path, bool Exists, string DefaultDescription, int FoldersMoved, int FilesMoved, int DbRowsUpdated, IReadOnlyList<string> Warnings);

    [HttpGet("unknown-folder")]
    public async Task<UnknownFolderDto> GetUnknownFolder(CancellationToken ct)
    {
        var path = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        return new UnknownFolderDto(
            path,
            path is not null && Directory.Exists(path),
            "default: <library-location>/__unknown");
    }

    // Updating the custom __unknown path. When the value changes, every existing
    // author folder in the OLD location (custom path or per-library default) is
    // moved into the NEW location and matching LocalBookFiles rows are rewritten
    // so on-disk and in-DB state stay aligned.
    [HttpPut("unknown-folder")]
    public async Task<ActionResult<UnknownFolderMigrationDto>> SetUnknownFolder(
        [FromBody] UpdateUnknownFolder body,
        CancellationToken ct)
    {
        var newPath = body.Path?.Trim() ?? "";
        var oldCustomPath = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);

        if (newPath.Length > 0)
        {
            try { Directory.CreateDirectory(newPath); }
            catch (Exception ex) { return BadRequest(new { error = $"Cannot create destination: {ex.Message}" }); }
        }

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        // Resolve OLD source roots (where contents currently live).
        var oldRoots = oldCustomPath is not null
            ? new[] { oldCustomPath }
            : locations.Select(l => Path.Combine(l, CalibreScanner.UnknownAuthorFolder)).ToArray();

        // Destination: the new custom path, or the primary library location's
        // default __unknown if the setting is being cleared.
        string newRoot;
        if (newPath.Length > 0)
        {
            newRoot = newPath;
        }
        else
        {
            var primary = await _db.LibraryLocations
                .Where(l => l.Enabled && l.IsPrimary)
                .Select(l => l.Path)
                .FirstOrDefaultAsync(ct)
                ?? locations.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(primary))
                return BadRequest(new { error = "No primary library location is set." });
            newRoot = Path.Combine(primary, CalibreScanner.UnknownAuthorFolder);
        }

        int foldersMoved = 0, filesMoved = 0, dbRowsUpdated = 0;
        var warnings = new List<string>();
        var pathRewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var oldRoot in oldRoots)
        {
            if (!Directory.Exists(oldRoot)) continue;
            if (string.Equals(
                    Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(newRoot);

            foreach (var authorDir in Directory.GetDirectories(oldRoot))
            {
                var folderName = Path.GetFileName(authorDir);
                var dest = UniqueDirectory(newRoot, folderName);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(authorDir, "*", SearchOption.AllDirectories))
                    {
                        pathRewrites[file] = Path.Combine(dest, Path.GetRelativePath(authorDir, file));
                    }
                    Directory.Move(authorDir, dest);
                    foldersMoved++;
                    filesMoved += Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count();
                }
                catch (Exception ex)
                {
                    foreach (var k in pathRewrites.Keys.Where(k => k.StartsWith(authorDir, StringComparison.OrdinalIgnoreCase)).ToList())
                        pathRewrites.Remove(k);
                    warnings.Add($"{folderName}: {ex.Message}");
                }
            }

            try
            {
                if (Directory.Exists(oldRoot) && !Directory.EnumerateFileSystemEntries(oldRoot).Any())
                    Directory.Delete(oldRoot);
            }
            catch { /* best effort */ }
        }

        if (pathRewrites.Count > 0)
        {
            var oldFullPaths = pathRewrites.Keys.ToList();
            var affected = await _db.LocalBookFiles
                .Where(f => oldFullPaths.Contains(f.FullPath))
                .ToListAsync(ct);
            foreach (var row in affected)
            {
                if (pathRewrites.TryGetValue(row.FullPath, out var newFullPath))
                {
                    row.FullPath = newFullPath;
                    dbRowsUpdated++;
                }
            }
        }

        var settingRow = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.UnknownFolder, ct);
        if (settingRow is null)
            _db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.UnknownFolder, Value = newPath });
        else
            settingRow.Value = newPath;
        await _db.SaveChangesAsync(ct);

        var resolvedPath = newPath.Length > 0 ? newPath : null;
        return new UnknownFolderMigrationDto(
            resolvedPath,
            resolvedPath is not null && Directory.Exists(resolvedPath),
            "default: <library-location>/__unknown",
            foldersMoved,
            filesMoved,
            dbRowsUpdated,
            warnings);
    }

    private static string UniqueDirectory(string parent, string preferredLeaf)
    {
        var dest = Path.Combine(parent, preferredLeaf);
        if (!Directory.Exists(dest)) return dest;
        for (int n = 1; ; n++)
        {
            dest = Path.Combine(parent, $"{preferredLeaf}_{n}");
            if (!Directory.Exists(dest)) return dest;
        }
    }

    // Resilient recursive walk — skip nothing (Hidden/System/symlinks all
    // count) and don't abort on an unreadable subfolder.
    private static readonly EnumerationOptions UnknownWalk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    // Sentinel extension key for files that have no extension at all.
    private const string NoExtensionKey = "(none)";

    private async Task<IReadOnlyList<string>> EnabledLibraryRootsAsync(CancellationToken ct)
        => await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

    public sealed record UnknownFileTypeRow(string Extension, int Count);
    public sealed record UnknownFileTypesDto(
        IReadOnlyList<string> Roots,
        IReadOnlyList<string> MissingRoots,
        int Total,
        IReadOnlyList<UnknownFileTypeRow> Types);

    // Counts every file in the __unknown quarantine folder grouped by extension
    // (lowercased; "(none)" for extensionless files). No per-file stat, so it's
    // a straight directory walk — but at hundreds of thousands of files it can
    // still take a little while, hence it's a user-triggered scan.
    [HttpGet("unknown-folder/file-types")]
    public async Task<ActionResult<UnknownFileTypesDto>> UnknownFileTypes(CancellationToken ct)
    {
        var roots = await UnknownFolderResolver.GetSourceRootsAsync(_db, await EnabledLibraryRootsAsync(ct), ct);
        var missing = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { missing.Add(root); continue; }
            foreach (var file in Directory.EnumerateFiles(root, "*", UnknownWalk))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file);
                var key = string.IsNullOrEmpty(ext) ? NoExtensionKey : ext.ToLowerInvariant();
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                total++;
            }
        }

        var types = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new UnknownFileTypeRow(kv.Key, kv.Value))
            .ToList();
        return Ok(new UnknownFileTypesDto(roots, missing, total, types));
    }

    public sealed record PurgeUnknownTypeRequest(string Extension);
    public sealed record PurgeUnknownTypeResult(string Extension, int Deleted, int Failed, IReadOnlyList<string> Errors);

    // Deletes every file of the given extension under the __unknown quarantine
    // root(s). "(none)" purges extensionless files. Files are collected first,
    // then deleted, so the lazy directory walk isn't mutated mid-enumeration.
    // Only files are removed; empty folders are left for the flatten job.
    [HttpPost("unknown-folder/purge")]
    public async Task<ActionResult<PurgeUnknownTypeResult>> PurgeUnknownFileType(
        [FromBody] PurgeUnknownTypeRequest body, CancellationToken ct)
    {
        var raw = body.Extension?.Trim();
        if (string.IsNullOrEmpty(raw)) return BadRequest(new { error = "Extension is required." });

        var noExt = string.Equals(raw, NoExtensionKey, StringComparison.OrdinalIgnoreCase);
        var ext = noExt ? "" : (raw.StartsWith('.') ? raw : "." + raw).ToLowerInvariant();

        var roots = await UnknownFolderResolver.GetSourceRootsAsync(_db, await EnabledLibraryRootsAsync(ct), ct);

        var toDelete = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", UnknownWalk))
            {
                ct.ThrowIfCancellationRequested();
                var fext = Path.GetExtension(file);
                var match = noExt
                    ? string.IsNullOrEmpty(fext)
                    : string.Equals(fext, ext, StringComparison.OrdinalIgnoreCase);
                if (match) toDelete.Add(file);
            }
        }

        var deleted = 0;
        var failed = 0;
        var errors = new List<string>();
        foreach (var file in toDelete)
        {
            ct.ThrowIfCancellationRequested();
            try { System.IO.File.Delete(file); deleted++; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                failed++;
                if (errors.Count < 50) errors.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }

        // Keep the ebook index in step: if we purged an ebook extension, drop the
        // matching UnknownFiles rows (junk extensions are never indexed there).
        if (!noExt && deleted > 0 && CalibreScanner.EbookExtensions.Contains(ext))
        {
            for (var i = 0; i < toDelete.Count; i += 1000)
            {
                var slice = toDelete.Skip(i).Take(1000).ToList();
                await _db.UnknownFiles.Where(u => slice.Contains(u.FullPath)).ExecuteDeleteAsync(ct);
            }
        }

        return Ok(new PurgeUnknownTypeResult(noExt ? NoExtensionKey : ext, deleted, failed, errors));
    }

    // Pushover credentials are stored in AppSettings. Both keys must be set
    // for notifications to fire; either being blank disables the feature.
    public sealed record PushoverDto(string AppToken, string UserKey, bool Configured);
    public sealed record UpdatePushover(string? AppToken, string? UserKey);
    public sealed record PushoverTestResult(bool Sent, string? Error);

    [HttpGet("pushover")]
    public async Task<PushoverDto> GetPushover(CancellationToken ct)
    {
        var rows = await _db.AppSettings
            .AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.PushoverAppToken
                     || s.Key == AppSettingKeys.PushoverUserKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        rows.TryGetValue(AppSettingKeys.PushoverAppToken, out var token);
        rows.TryGetValue(AppSettingKeys.PushoverUserKey, out var user);
        return new PushoverDto(
            token ?? "",
            user ?? "",
            !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(user));
    }

    [HttpPut("pushover")]
    public async Task<ActionResult<PushoverDto>> SetPushover(
        [FromBody] UpdatePushover body, CancellationToken ct)
    {
        var token = body.AppToken?.Trim() ?? "";
        var user = body.UserKey?.Trim() ?? "";
        await UpsertSettingAsync(AppSettingKeys.PushoverAppToken, token, ct);
        await UpsertSettingAsync(AppSettingKeys.PushoverUserKey, user, ct);
        await _db.SaveChangesAsync(ct);
        return new PushoverDto(
            token, user,
            !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(user));
    }

    // Fire a test push so the user can verify credentials before relying on
    // them for nightly book alerts. The body is allowed to override the
    // stored values so a typo can be tested without saving first.
    [HttpPost("pushover/test")]
    public async Task<PushoverTestResult> TestPushover(
        [FromBody] UpdatePushover? body,
        [FromServices] PushoverClient client,
        CancellationToken ct)
    {
        (string? Token, string? User)? overrideCreds = null;
        if (body is not null
            && (!string.IsNullOrWhiteSpace(body.AppToken) || !string.IsNullOrWhiteSpace(body.UserKey)))
        {
            overrideCreds = (body.AppToken?.Trim(), body.UserKey?.Trim());
        }
        var result = await client.SendAsync(
            overrideCreds,
            title: "TheLibrary test alert",
            message: "Pushover credentials look good — new-book notifications will use this device.",
            url: null,
            ct);
        return new PushoverTestResult(result.Sent, result.Error);
    }

    public sealed record OpenLibraryIdentityDto(
        string AppName, string ContactEmail, bool Identified, string UserAgent);
    public sealed record UpdateOpenLibraryIdentity(string? AppName, string? ContactEmail);

    // The OpenLibrary User-Agent identity (app name + contact email). Stored in
    // the database so it stays out of the git repo and is set per deployment.
    [HttpGet("openlibrary")]
    public ActionResult<OpenLibraryIdentityDto> GetOpenLibrary([FromServices] OpenLibrarySettings ol)
        => new OpenLibraryIdentityDto(ol.AppName, ol.ContactEmail, ol.IsIdentified, ol.UserAgent);

    [HttpPut("openlibrary")]
    public async Task<ActionResult<OpenLibraryIdentityDto>> SetOpenLibrary(
        [FromBody] UpdateOpenLibraryIdentity body,
        [FromServices] OpenLibrarySettings ol,
        CancellationToken ct)
    {
        var email = body.ContactEmail?.Trim() ?? "";
        if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
            return BadRequest(new { error = "Contact email doesn't look like an email address." });

        await ol.UpdateAsync(body.AppName, email, ct);
        return new OpenLibraryIdentityDto(ol.AppName, ol.ContactEmail, ol.IsIdentified, ol.UserAgent);
    }

    public sealed record RefreshLimitsDto(int MaxAuthorsPerRun, int MaxEarlyWhenNoneDue, int MaxEarlyDaysAhead);
    public sealed record RefreshCadenceDto(int RecentDays, int MidDays, int DormantDays, int OldOrEmptyDays);
    public sealed record DuplicateFormatPreferenceDto(string[] Formats);

    // Limits for the refresh-due-works job: how many authors it refreshes per
    // run (0 = no limit), and how many to refresh early when none are due.
    [HttpGet("refresh-limits")]
    public async Task<RefreshLimitsDto> GetRefreshLimits(CancellationToken ct)
    {
        var rows = await _db.AppSettings
            .Where(s => s.Key == AppSettingKeys.RefreshMaxAuthorsPerRun
                     || s.Key == AppSettingKeys.RefreshEarlyWhenNoneDue
                     || s.Key == AppSettingKeys.RefreshEarlyMaxDaysAhead)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new RefreshLimitsDto(
            ReadInt(rows, AppSettingKeys.RefreshMaxAuthorsPerRun, 0),
            ReadInt(rows, AppSettingKeys.RefreshEarlyWhenNoneDue, 200),
            ReadInt(rows, AppSettingKeys.RefreshEarlyMaxDaysAhead, 0));
    }

    [HttpPut("refresh-limits")]
    public async Task<ActionResult<RefreshLimitsDto>> SetRefreshLimits(
        [FromBody] RefreshLimitsDto body, CancellationToken ct)
    {
        if (body.MaxAuthorsPerRun < 0 || body.MaxEarlyWhenNoneDue < 0 || body.MaxEarlyDaysAhead < 0)
            return BadRequest(new { error = "Values cannot be negative." });

        await UpsertSettingAsync(AppSettingKeys.RefreshMaxAuthorsPerRun,
            body.MaxAuthorsPerRun.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshEarlyWhenNoneDue,
            body.MaxEarlyWhenNoneDue.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshEarlyMaxDaysAhead,
            body.MaxEarlyDaysAhead.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return new RefreshLimitsDto(body.MaxAuthorsPerRun, body.MaxEarlyWhenNoneDue, body.MaxEarlyDaysAhead);
    }

    [HttpGet("refresh-cadence")]
    public async Task<RefreshCadenceDto> GetRefreshCadence(CancellationToken ct)
    {
        var rows = await _db.AppSettings
            .Where(s => s.Key == AppSettingKeys.RefreshCadenceRecentDays
                     || s.Key == AppSettingKeys.RefreshCadenceMidDays
                     || s.Key == AppSettingKeys.RefreshCadenceDormantDays
                     || s.Key == AppSettingKeys.RefreshCadenceOldOrEmptyDays)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var defaults = TheLibrary.Server.Services.Sync.AuthorRefresher.RefreshCadenceSettings.Defaults;
        return new RefreshCadenceDto(
            ReadInt(rows, AppSettingKeys.RefreshCadenceRecentDays, defaults.RecentDays),
            ReadInt(rows, AppSettingKeys.RefreshCadenceMidDays, defaults.MidDays),
            ReadInt(rows, AppSettingKeys.RefreshCadenceDormantDays, defaults.DormantDays),
            ReadInt(rows, AppSettingKeys.RefreshCadenceOldOrEmptyDays, defaults.OldOrEmptyDays));
    }

    [HttpPut("refresh-cadence")]
    public async Task<ActionResult<RefreshCadenceDto>> SetRefreshCadence(
        [FromBody] RefreshCadenceDto body, CancellationToken ct)
    {
        if (body.RecentDays <= 0 || body.MidDays <= 0 || body.DormantDays <= 0 || body.OldOrEmptyDays <= 0)
            return BadRequest(new { error = "Cadence values must be greater than zero." });

        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceRecentDays, body.RecentDays.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceMidDays, body.MidDays.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceDormantDays, body.DormantDays.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceOldOrEmptyDays, body.OldOrEmptyDays.ToString(), ct);
        await _db.SaveChangesAsync(ct);

        return new RefreshCadenceDto(body.RecentDays, body.MidDays, body.DormantDays, body.OldOrEmptyDays);
    }

    [HttpGet("duplicate-format-preference")]
    public async Task<DuplicateFormatPreferenceDto> GetDuplicateFormatPreference(CancellationToken ct)
    {
        var row = await _db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.DuplicateFormatPreference, ct);
        var formats = ParseFormats(row?.Value);
        return new DuplicateFormatPreferenceDto(formats.Length > 0 ? formats : BooksController.DefaultFormatPreference);
    }

    [HttpPut("duplicate-format-preference")]
    public async Task<ActionResult<DuplicateFormatPreferenceDto>> SetDuplicateFormatPreference(
        [FromBody] DuplicateFormatPreferenceDto body,
        CancellationToken ct)
    {
        var cleaned = (body.Formats ?? Array.Empty<string>())
            .Select(f => f?.Trim().TrimStart('.').ToLowerInvariant())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cleaned.Length == 0)
            return BadRequest(new { error = "At least one format is required." });

        await UpsertSettingAsync(AppSettingKeys.DuplicateFormatPreference, string.Join(';', cleaned), ct);
        await _db.SaveChangesAsync(ct);
        return new DuplicateFormatPreferenceDto(cleaned);
    }

    public sealed record ArchiveFolderDto(string FolderName);

    [HttpGet("archive-folder")]
    public async Task<ArchiveFolderDto> GetArchiveFolder(CancellationToken ct)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.DedupeArchiveFolder, ct);
        return new ArchiveFolderDto(string.IsNullOrWhiteSpace(row?.Value) ? "__archive" : row!.Value.Trim());
    }

    [HttpPut("archive-folder")]
    public async Task<ActionResult<ArchiveFolderDto>> SetArchiveFolder(
        [FromBody] ArchiveFolderDto body, CancellationToken ct)
    {
        var name = body.FolderName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "Folder name cannot be empty." });
        // Prevent path traversal regardless of form.
        if (name.Contains(".."))
            return BadRequest(new { error = "Folder name must not contain '..'." });
        // Two accepted forms: a simple leaf name (created inside each library
        // root) or a full absolute path (a single fixed archive location).
        // Anything in between — a relative path with separators — is rejected.
        if (!Path.IsPathFullyQualified(name) && (name.Contains('/') || name.Contains('\\')))
            return BadRequest(new { error = "Use either a simple folder name or a full absolute path." });
        await UpsertSettingAsync(AppSettingKeys.DedupeArchiveFolder, name, ct);
        await _db.SaveChangesAsync(ct);
        return new ArchiveFolderDto(name);
    }

    public sealed record CoverHoverDto(bool Enabled, double Scale);

    [HttpGet("cover-hover")]
    public async Task<CoverHoverDto> GetCoverHover(CancellationToken ct)
    {
        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.CoverHoverEnabled || s.Key == AppSettingKeys.CoverHoverScale)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var enabled = rows.TryGetValue(AppSettingKeys.CoverHoverEnabled, out var e)
            && string.Equals(e, "true", StringComparison.OrdinalIgnoreCase);
        var scale = rows.TryGetValue(AppSettingKeys.CoverHoverScale, out var sv)
            && double.TryParse(sv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? s : 1.0;
        return new CoverHoverDto(enabled, ClampScale(scale));
    }

    [HttpPut("cover-hover")]
    public async Task<ActionResult<CoverHoverDto>> SetCoverHover([FromBody] CoverHoverDto body, CancellationToken ct)
    {
        var scale = ClampScale(body.Scale);
        await UpsertSettingAsync(AppSettingKeys.CoverHoverEnabled, body.Enabled ? "true" : "false", ct);
        await UpsertSettingAsync(AppSettingKeys.CoverHoverScale,
            scale.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await _db.SaveChangesAsync(ct);
        return new CoverHoverDto(body.Enabled, scale);
    }

    // 1 = default size, up to 4 = quadruple. Anything out of range (or unset)
    // snaps back to a sane value.
    private static double ClampScale(double scale)
        => double.IsFinite(scale) ? Math.Clamp(scale, 1.0, 4.0) : 1.0;

    public sealed record CoverCacheFolderDto(string Path, string Default, bool Writable, int BatchSize);

    [HttpGet("cover-cache-folder")]
    public async Task<CoverCacheFolderDto> GetCoverCacheFolder(CancellationToken ct)
    {
        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.CachedCoversFolder || s.Key == AppSettingKeys.CacheMetadataBatchSize)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var stored = rows.TryGetValue(AppSettingKeys.CachedCoversFolder, out var p) ? p?.Trim() : null;
        var libraryPath = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).FirstOrDefaultAsync(ct);
        var dflt = CoverCacheResolver.DefaultFor(libraryPath, _env);
        var effective = string.IsNullOrWhiteSpace(stored) ? dflt : stored;
        var batch = rows.TryGetValue(AppSettingKeys.CacheMetadataBatchSize, out var bv)
            && int.TryParse(bv, out var b) && b > 0 ? b : OpenLibraryMetadataCacheService.DefaultBatchSize;
        return new CoverCacheFolderDto(effective, dflt, IsWritable(effective), batch);
    }

    // Dedicated request shape: only the fields the client actually sends. (The
    // response DTO has non-nullable Path/Default which [ApiController] would treat
    // as implicitly required, rejecting the PUT with a 400 before it ran.)
    public sealed record CoverCacheUpdate(string? Path, int? BatchSize);

    [HttpPut("cover-cache-folder")]
    public async Task<ActionResult<CoverCacheFolderDto>> SetCoverCacheFolder(
        [FromBody] CoverCacheUpdate body, CancellationToken ct)
    {
        var path = (body.Path ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path))
            return BadRequest(new { error = "Path cannot be empty." });
        if (path.Contains(".."))
            return BadRequest(new { error = "Path must not contain '..'." });
        if (!Path.IsPathFullyQualified(path) && !path.StartsWith('/'))
            return BadRequest(new { error = "Use a full absolute path." });
        var requested = body.BatchSize ?? 0;
        var batch = Math.Clamp(requested <= 0 ? OpenLibraryMetadataCacheService.DefaultBatchSize : requested, 1, 100_000);

        await UpsertSettingAsync(AppSettingKeys.CachedCoversFolder, path, ct);
        await UpsertSettingAsync(AppSettingKeys.CacheMetadataBatchSize,
            batch.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await _db.SaveChangesAsync(ct);

        // Apply immediately so the serving controller and cache job use it now.
        _coverCache.Directory = path;
        var libraryPath = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).FirstOrDefaultAsync(ct);
        return new CoverCacheFolderDto(path, CoverCacheResolver.DefaultFor(libraryPath, _env), IsWritable(path), batch);
    }

    public sealed record IntegritySettingsDto(int MaxBooksPerRun, string[] ReplacementFormats);
    public sealed record IntegritySettingsUpdate(int? MaxBooksPerRun, string[]? ReplacementFormats);

    [HttpGet("integrity")]
    public async Task<IntegritySettingsDto> GetIntegritySettings(CancellationToken ct)
    {
        var rows = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.IntegrityMaxBooksPerRun
                     || s.Key == AppSettingKeys.IntegrityReplacementFormats)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var max = rows.TryGetValue(AppSettingKeys.IntegrityMaxBooksPerRun, out var mv)
            && int.TryParse(mv, out var n) && n > 0 ? n : BookIntegrityService.DefaultMaxBooksPerRun;
        var formats = ParseFormats(
            rows.TryGetValue(AppSettingKeys.IntegrityReplacementFormats, out var fv) && !string.IsNullOrWhiteSpace(fv)
                ? fv : DamagedController.DefaultReplacementFormats);
        return new IntegritySettingsDto(max, formats);
    }

    [HttpPut("integrity")]
    public async Task<ActionResult<IntegritySettingsDto>> SetIntegritySettings(
        [FromBody] IntegritySettingsUpdate body, CancellationToken ct)
    {
        var requested = body.MaxBooksPerRun ?? 0;
        var max = Math.Clamp(
            requested <= 0 ? BookIntegrityService.DefaultMaxBooksPerRun : requested, 1, 100_000);

        var formats = ParseFormats(string.Join(';', body.ReplacementFormats ?? Array.Empty<string>()));
        if (formats.Length == 0) formats = ParseFormats(DamagedController.DefaultReplacementFormats);

        await UpsertSettingAsync(AppSettingKeys.IntegrityMaxBooksPerRun,
            max.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await UpsertSettingAsync(AppSettingKeys.IntegrityReplacementFormats, string.Join(';', formats), ct);
        await _db.SaveChangesAsync(ct);
        return new IntegritySettingsDto(max, formats);
    }

    public sealed record ContentScanSettingsDto(int MaxPerRun);
    public sealed record ContentScanSettingsUpdate(int? MaxPerRun);

    [HttpGet("content-scan")]
    public async Task<ContentScanSettingsDto> GetContentScanSettings(CancellationToken ct)
    {
        var raw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.ContentScanMaxPerRun)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        var max = int.TryParse(raw, out var n) && n > 0 ? n : ContentScanService.DefaultMaxPerRun;
        return new ContentScanSettingsDto(max);
    }

    [HttpPut("content-scan")]
    public async Task<ActionResult<ContentScanSettingsDto>> SetContentScanSettings(
        [FromBody] ContentScanSettingsUpdate body, CancellationToken ct)
    {
        var requested = body.MaxPerRun ?? 0;
        var max = Math.Clamp(requested <= 0 ? ContentScanService.DefaultMaxPerRun : requested, 1, 100_000);
        await UpsertSettingAsync(AppSettingKeys.ContentScanMaxPerRun,
            max.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        await _db.SaveChangesAsync(ct);
        return new ContentScanSettingsDto(max);
    }

    // Best-effort write check: can we create the directory and a temp file there?
    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            System.IO.File.WriteAllText(probe, "");
            System.IO.File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> rows, string key, int fallback)
        => rows.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n >= 0 ? n : fallback;

    private static string[] ParseFormats(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => f.TrimStart('.').ToLowerInvariant())
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private async Task UpsertSettingAsync(string key, string value, CancellationToken ct)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}
