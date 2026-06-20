using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;

namespace TheLibrary.Server.Tests.Infrastructure;

// A real relational test database (SQLite in-memory) for exercising code paths the
// EF InMemory provider can't run — chiefly ExecuteUpdate / ExecuteDelete and
// anything that depends on relational SQL translation or constraints. The library
// has ~20 ExecuteUpdate/Delete call sites that InMemory silently can't execute, so
// those were effectively untested; this fixture closes that gap.
//
// The database lives only as long as the connection is open, so one connection is
// shared and handed to fresh DbContexts via NewContext() — letting a test write
// through one context and assert through another (no stale change-tracker reads).
internal sealed class RelationalTestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public RelationalTestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public LibraryDbContext NewContext()
        => new(new DbContextOptionsBuilder<LibraryDbContext>()
            .UseSqlite(_connection)
            .Options);

    public void Dispose() => _connection.Dispose();
}
