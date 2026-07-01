using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

var builder = WebApplication.CreateBuilder(args);
var disableHangfire = builder.Configuration.GetValue<bool>("Testing:DisableHangfire");
var skipStartupTasks = builder.Configuration.GetValue<bool>("Testing:SkipStartupTasks");
var useInMemoryDatabase = builder.Configuration.GetValue<bool>("Testing:UseInMemoryDatabase");
var inMemoryDatabaseName = builder.Configuration["Testing:InMemoryDatabaseName"] ?? "thelibrary-tests";

builder.Services.AddControllers(o =>
        o.Filters.Add<TheLibrary.Server.Infrastructure.ApiExceptionFilter>())
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
builder.Services.AddOpenApi();

var connStr = builder.Configuration.GetConnectionString("Library");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException(
        "Missing ConnectionStrings:Library. Set it with `dotnet user-secrets set \"ConnectionStrings:Library\" \"<conn>\"` or via the ConnectionStrings__Library env var.");

builder.Services.AddDbContext<LibraryDbContext>(opt =>
{
    if (useInMemoryDatabase)
    {
        opt.UseInMemoryDatabase(inMemoryDatabaseName);
    }
    else
    {
        opt.UseSqlServer(connStr, sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 5);
            sql.CommandTimeout(300);
        });
    }
});

// OpenLibrary identity (User-Agent app name + contact email) is stored in the
// database and editable from the Settings page. The rate limiter reads it to
// pick the 3 req/sec identified pace over the 1 req/sec anonymous one.
builder.Services.AddSingleton<OpenLibrarySettings>();
builder.Services.AddSingleton<OpenLibraryRateLimiter>();
builder.Services.AddHttpClient<OpenLibraryClient>();
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.GoogleBooksRateLimiter>();
builder.Services.AddHttpClient<TheLibrary.Server.Services.OpenLibrary.GoogleBooksClient>();
// ISBN-resolution fallback chain (tried in this order after OpenLibrary): Google
// Books (free, capped), Hardcover (free), ISBNdb (paid). Each is off unless its
// credential is set. The IEnumerable<IIsbnFallbackProvider> preserves registration
// order.
builder.Services.AddHttpClient(TheLibrary.Server.Services.OpenLibrary.HardcoverFallbackProvider.HttpClientName,
    c => c.BaseAddress = new Uri("https://api.hardcover.app/v1/"));
builder.Services.AddHttpClient(TheLibrary.Server.Services.OpenLibrary.LocFallbackProvider.HttpClientName,
    c => c.BaseAddress = new Uri("http://lx2.loc.gov:210/"));
builder.Services.AddHttpClient(TheLibrary.Server.Services.OpenLibrary.IsbndbFallbackProvider.HttpClientName,
    c => c.BaseAddress = new Uri("https://api2.isbndb.com/"));
builder.Services.AddTransient<TheLibrary.Server.Services.OpenLibrary.GoogleBooksFallbackProvider>();
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.HardcoverFallbackProvider>();
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.LocFallbackProvider>();
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.IsbndbFallbackProvider>();
// Chain order (free sources first, paid last): Google → Hardcover → LoC → ISBNdb.
builder.Services.AddTransient<TheLibrary.Server.Services.OpenLibrary.IIsbnFallbackProvider>(
    sp => sp.GetRequiredService<TheLibrary.Server.Services.OpenLibrary.GoogleBooksFallbackProvider>());
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.IIsbnFallbackProvider>(
    sp => sp.GetRequiredService<TheLibrary.Server.Services.OpenLibrary.HardcoverFallbackProvider>());
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.IIsbnFallbackProvider>(
    sp => sp.GetRequiredService<TheLibrary.Server.Services.OpenLibrary.LocFallbackProvider>());
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.IIsbnFallbackProvider>(
    sp => sp.GetRequiredService<TheLibrary.Server.Services.OpenLibrary.IsbndbFallbackProvider>());
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IFileSystem, SystemFileSystem>();
builder.Services.AddSingleton<TheLibrary.Server.Services.OpenLibrary.CoverCacheState>();
builder.Services.AddSingleton<OpenLibraryMetadataCacheService>();
builder.Services.AddScoped<TheLibrary.Server.Services.OpenLibrary.IsbnResolutionService>();
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddScoped<CalibreScanner>();
builder.Services.AddScoped<AuthorDumpSeeder>();
builder.Services.AddScoped<TheLibrary.Server.Services.Incoming.IncomingProcessor>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Incoming.IncomingService>();
builder.Services.AddScoped<TheLibrary.Server.Services.AuthorUpdates.AuthorUpdateProcessor>();
builder.Services.AddScoped<TheLibrary.Server.Services.Sync.AuthorRefresher>();
builder.Services.AddScoped<TheLibrary.Server.Services.Sync.ManualBookService>();
builder.Services.AddScoped<TheLibrary.Server.Services.Sync.ManualAuthorService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.AuthorPruneService>();
builder.Services.AddScoped<TheLibrary.Server.Services.Download.NzbGrabService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Search.FullTextSearchService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.AuthorRefreshCoordinator>();
builder.Services.Configure<TheLibrary.Server.Services.Remarkable.RemarkableOptions>(
    builder.Configuration.GetSection("Remarkable"));
builder.Services.Configure<TheLibrary.Server.Services.Calibre.CalibreOptions>(
    builder.Configuration.GetSection("Calibre"));
builder.Services.AddSingleton<TheLibrary.Server.Services.Calibre.CalibreConverter>();
builder.Services.AddScoped<TheLibrary.Server.Services.Remarkable.RemarkableClient>();
builder.Services.AddSingleton<BackgroundTaskCoordinator>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.SeriesOrganizerService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.AuthorFolderDisambiguatorService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.SameNameAuthorService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.PhysicalAuthorStarService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.SeriesCoAuthorStarService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.OtherEditionMarkerService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.ReadEditionPropagationService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.UnzipService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.UnknownFolderFlattenerService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.UnknownDuplicateRemovalService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.AuthorDuplicateRemovalService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.DuplicateAutoArchiveService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.SeriesWatchService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Download.AutoReplaceDamagedService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.WorkResolutionService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.IsbnResolutionCatchupService>();
builder.Services.AddHttpClient<TheLibrary.Server.Services.Llm.LlmMetadataClient>();
builder.Services.AddHttpClient<TheLibrary.Server.Services.Llm.LlmSpendClient>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Llm.LlmIdentificationService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.ManualBookPromotionService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.UnknownAuthorAdoptionService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.StarredAuthorRefreshService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.ForeignArchiveService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.LinkedAuthorMergeService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Calibre.BookIntegrityChecker>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.BookIntegrityService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.StaleFileCleanupService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Calibre.BookTextReader>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.ContentScanService>();
builder.Services.AddScoped<TheLibrary.Server.Services.Sync.UntrackedAuthorAssigner>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Sync.UntrackedAuthorAssignmentService>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Pushover.PushoverClient>();

// Hangfire uses the same SQL Server database for its job store so a restart
// doesn't drop the schedule. WorkerCount=1 is the mutual-exclusion lever:
// only one scheduled job can ever execute at once, regardless of which task
// it is. A manual UI trigger that clashes still hits BackgroundTaskCoordinator
// and returns 409.
if (!disableHangfire)
{
    builder.Services.AddHangfire(cfg => cfg
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connStr, new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.FromSeconds(15),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        }));
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = 1;
        options.ServerName = $"thelibrary:{Environment.MachineName}";
    });

    builder.Services.AddScoped<ScheduledJobs>();
    builder.Services.AddSingleton<ScheduleService>();
}

var app = builder.Build();

// Apply migrations automatically at startup, then seed a default library
// location from Calibre:Root if no locations have been configured yet.
if (!skipStartupTasks)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
    await db.Database.MigrateAsync();

    // Load the OpenLibrary User-Agent identity from the database into the
    // in-memory singleton so the first API call already carries it.
    await scope.ServiceProvider.GetRequiredService<OpenLibrarySettings>().LoadAsync();

    // Resolve the effective cover-cache directory (saved setting, else derived
    // from the library location) into the in-memory holder used by the serving
    // controller and the cache job.
    scope.ServiceProvider.GetRequiredService<TheLibrary.Server.Services.OpenLibrary.CoverCacheState>().Directory =
        await TheLibrary.Server.Services.OpenLibrary.CoverCacheResolver.ResolveAsync(db, app.Environment);

    if (!await db.LibraryLocations.AnyAsync())
    {
        var seedPath = builder.Configuration["Calibre:Root"];
        if (!string.IsNullOrWhiteSpace(seedPath))
        {
            db.LibraryLocations.Add(new LibraryLocation
            {
                Label = "Default",
                Path = seedPath,
                Enabled = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    // Push the stored schedule config into Hangfire's recurring-job registry.
    // Disabled jobs get removed, enabled ones get (re)registered with the
    // current cron. Reapplied on every startup so config changes made while
    // the app was down still take effect.
    if (!disableHangfire)
    {
        var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
        await schedules.ClearFailedJobsAsync();
        await schedules.ApplyAllAsync();

        // Kick off a catch-up pass on startup: bring in new files (incoming), rebuild
        // the quarantine index (reprocess-unknown), then run the main sync. They queue
        // on the single worker + coordinator and run one at a time; the normal cron
        // schedules (applied above) continue as usual afterwards.
        schedules.TriggerNow(ScheduleJobIds.Incoming);
        schedules.TriggerNow(ScheduleJobIds.ReprocessUnknown);
        schedules.TriggerNow(ScheduleJobIds.Sync);
    }
}

// index.html must NEVER be browser-cached: it names the current fingerprinted JS/CSS
// bundles, so a cached copy keeps loading old (possibly broken) assets after a deploy.
// Fingerprinted assets (/assets/index-*.js) have a file extension and stay cacheable;
// extensionless GETs (the SPA root "/" and client routes) serve index.html → no-cache.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method) && !Path.HasExtension(context.Request.Path.Value ?? ""))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Remove("ETag");
            return Task.CompletedTask;
        });
    }
    await next();
});

app.UseDefaultFiles();
app.MapStaticAssets();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthorization();

// Dashboard is available at /hangfire for operational visibility. No auth
// filter here — deployment is single-user, self-hosted on a trusted network.
if (!disableHangfire)
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new AllowAllDashboardAuthorizationFilter() }
    });
}

app.MapControllers();

// Build version shown at the bottom of the side menu. Derived from the running
// assembly's build time, so it refreshes on every server build/deploy without
// baking anything build-varying into the (cache-fingerprinted) SPA bundle.
app.MapGet("/api/version", () =>
{
    string version;
    try
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var built = System.IO.File.GetLastWriteTimeUtc(asm.Location);
        version = built.ToString("yyyy-MM-dd HH:mm") + " UTC";
    }
    catch
    {
        version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    }
    return Results.Ok(new { version });
});

// Unmatched /api routes must 404 as JSON — never fall through to the SPA's
// index.html, which a fetch() would then try to JSON.parse ("Unexpected token
// '<'"). This catch-all is lowest-precedence, so real controllers still win.
app.MapMethods("/api/{**rest}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH" },
    (string rest) => Results.NotFound(new { error = $"No API endpoint for /api/{rest}" }));

app.MapFallbackToFile("/index.html");

app.Run();

public partial class Program { }
