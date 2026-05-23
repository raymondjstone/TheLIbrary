using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.OpenLibrary;

namespace TheLibrary.Server.Tests.Infrastructure;

internal sealed class LibraryApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"thelibrary-tests-{Guid.NewGuid():N}";
    private readonly TestHttpMessageHandler _openLibraryHandler;

    public LibraryApiFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? openLibraryHandler = null)
    {
        _openLibraryHandler = new TestHttpMessageHandler(openLibraryHandler ?? ((_, _) => Task.FromResult(TestHttpMessageHandler.Json("{}"))));
        Environment.SetEnvironmentVariable("Testing__DisableHangfire", "true");
        Environment.SetEnvironmentVariable("Testing__SkipStartupTasks", "true");
        Environment.SetEnvironmentVariable("Testing__UseInMemoryDatabase", "true");
        Environment.SetEnvironmentVariable("Testing__InMemoryDatabaseName", _dbName);
        Environment.SetEnvironmentVariable("ConnectionStrings__Library", "ignored-for-tests");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(OpenLibraryClient));
            services.AddHttpClient<OpenLibraryClient>()
                .ConfigurePrimaryHttpMessageHandler(() => _openLibraryHandler);

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Environment.SetEnvironmentVariable("Testing__DisableHangfire", null);
            Environment.SetEnvironmentVariable("Testing__SkipStartupTasks", null);
            Environment.SetEnvironmentVariable("Testing__UseInMemoryDatabase", null);
            Environment.SetEnvironmentVariable("Testing__InMemoryDatabaseName", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__Library", null);
        }
    }
}
