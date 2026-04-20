using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var connStr = builder.Configuration.GetConnectionString("Library");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException(
        "Missing ConnectionStrings:Library. Set it with `dotnet user-secrets set \"ConnectionStrings:Library\" \"<conn>\"` or via the ConnectionStrings__Library env var.");

builder.Services.AddDbContext<LibraryDbContext>(opt =>
    opt.UseSqlServer(connStr, sql =>
    {
        sql.EnableRetryOnFailure(maxRetryCount: 5);
        sql.CommandTimeout(60);
    }));

builder.Services.AddSingleton<OpenLibraryRateLimiter>();
builder.Services.AddHttpClient<OpenLibraryClient>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<CalibreScanner>();
builder.Services.AddScoped<AuthorDumpSeeder>();
builder.Services.AddScoped<TheLibrary.Server.Services.Incoming.IncomingProcessor>();
builder.Services.AddSingleton<TheLibrary.Server.Services.Incoming.IncomingService>();
builder.Services.AddScoped<TheLibrary.Server.Services.AuthorUpdates.AuthorUpdateProcessor>();
builder.Services.AddScoped<TheLibrary.Server.Services.Sync.AuthorRefresher>();
builder.Services.Configure<TheLibrary.Server.Services.Remarkable.RemarkableOptions>(
    builder.Configuration.GetSection("Remarkable"));
builder.Services.Configure<TheLibrary.Server.Services.Calibre.CalibreOptions>(
    builder.Configuration.GetSection("Calibre"));
builder.Services.AddSingleton<TheLibrary.Server.Services.Calibre.CalibreConverter>();
builder.Services.AddScoped<TheLibrary.Server.Services.Remarkable.RemarkableClient>();
builder.Services.AddSingleton<BackgroundTaskCoordinator>();
builder.Services.AddSingleton<SyncService>();

// Hangfire uses the same SQL Server database for its job store so a restart
// doesn't drop the schedule. WorkerCount=1 is the mutual-exclusion lever:
// only one scheduled job can ever execute at once, regardless of which task
// it is. A manual UI trigger that clashes still hits BackgroundTaskCoordinator
// and returns 409.
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

var app = builder.Build();

// Apply migrations automatically at startup, then seed a default library
// location from Calibre:Root if no locations have been configured yet.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
    await db.Database.MigrateAsync();

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
    var schedules = scope.ServiceProvider.GetRequiredService<ScheduleService>();
    await schedules.ApplyAllAsync();
}

app.UseDefaultFiles();
app.MapStaticAssets();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthorization();

// Dashboard is available at /hangfire for operational visibility. No auth
// filter here — deployment is single-user, self-hosted on a trusted network.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new AllowAllDashboardAuthorizationFilter() }
});

app.MapControllers();
app.MapFallbackToFile("/index.html");

app.Run();
