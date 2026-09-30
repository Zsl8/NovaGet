using NovaGet.Core.Security;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data;
using NovaGet.Data.Migrations;

namespace NovaGet.Core.Tests.Data;

/// <summary>A migrated database in a temp folder.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly TempDirectory _temp = new();

    public TestDatabase()
    {
        Database = new SqliteDatabase(_temp.Combine("novaget.db"));
        new DatabaseMigrator(Database).Migrate();
    }

    public SqliteDatabase Database { get; }

    public ISecretProtector Protector { get; } = new DevOnlySecretProtector();

    public void Dispose() => _temp.Dispose();
}
