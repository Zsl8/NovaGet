using Dapper;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data;
using NovaGet.Data.Migrations;

namespace NovaGet.Core.Tests.Data;

public sealed class MigratorTests
{
    [Fact]
    public void Fresh_database_reaches_latest_version_with_seed_data()
    {
        using var temp = new TempDirectory();
        var db = new SqliteDatabase(temp.Combine("sub", "novaget.db"));

        var result = new DatabaseMigrator(db).Migrate();

        Assert.Equal(0, result.FromVersion);
        Assert.Equal(SchemaMigrations.LatestVersion, result.ToVersion);
        Assert.Null(result.BackupFile);

        using var connection = db.Open();
        Assert.Equal(SchemaMigrations.LatestVersion, DatabaseMigrator.GetVersion(connection));
        Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode;"));
        Assert.Equal(
            ["General", "Compressed", "Documents", "Music", "Programs", "Video"],
            connection.Query<string>("SELECT name FROM Category ORDER BY sortOrder;"));
        Assert.Equal(
            ["Main download queue", "Synchronization queue"],
            connection.Query<string>("SELECT name FROM Queue ORDER BY id;"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT isSyncQueue FROM Queue WHERE id = 2;"));
    }

    [Fact]
    public void Running_twice_is_a_no_op()
    {
        using var temp = new TempDirectory();
        var db = new SqliteDatabase(temp.Combine("novaget.db"));
        new DatabaseMigrator(db).Migrate();

        var second = new DatabaseMigrator(db).Migrate();

        Assert.Equal(second.FromVersion, second.ToVersion);
        using var connection = db.Open();
        Assert.Equal(6, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM Category;"));
    }

    [Fact]
    public void Upgrade_backs_up_then_applies_pending_migrations()
    {
        using var temp = new TempDirectory();
        var db = new SqliteDatabase(temp.Combine("novaget.db"));
        new DatabaseMigrator(db).Migrate();
        using (var c = db.Open())
        {
            c.Execute("INSERT INTO Download (url, originalUrl, fileName, savePath, addedAt) VALUES ('u', 'u', 'f', 'p', '2026-01-01');");
        }

        var next = new Migration(SchemaMigrations.LatestVersion + 1, "Test column", "ALTER TABLE Download ADD COLUMN testColumn TEXT NULL;");
        var result = new DatabaseMigrator(db, [.. SchemaMigrations.All, next]).Migrate();

        Assert.Equal(SchemaMigrations.LatestVersion, result.FromVersion);
        Assert.Equal(next.Version, result.ToVersion);
        Assert.NotNull(result.BackupFile);
        Assert.True(File.Exists(result.BackupFile));

        var backup = new SqliteDatabase(result.BackupFile!);
        using (var b = backup.Open())
        {
            Assert.Equal(SchemaMigrations.LatestVersion, DatabaseMigrator.GetVersion(b));
            Assert.Equal(1, b.ExecuteScalar<long>("SELECT COUNT(*) FROM Download;"));
        }

        using var connection = db.Open();
        Assert.Equal(next.Version, DatabaseMigrator.GetVersion(connection));
        connection.Execute("UPDATE Download SET testColumn = 'x';");
    }

    [Fact]
    public void Failed_migration_rolls_back_and_keeps_version()
    {
        using var temp = new TempDirectory();
        var db = new SqliteDatabase(temp.Combine("novaget.db"));
        new DatabaseMigrator(db).Migrate();

        var broken = new Migration(SchemaMigrations.LatestVersion + 1, "Broken", "CREATE TABLE X (id INTEGER); SELECT * FROM does_not_exist;");
        Assert.ThrowsAny<Exception>(() => new DatabaseMigrator(db, [.. SchemaMigrations.All, broken]).Migrate());

        using var connection = db.Open();
        Assert.Equal(SchemaMigrations.LatestVersion, DatabaseMigrator.GetVersion(connection));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'X';"));
    }

    [Fact]
    public void Newer_database_is_refused()
    {
        using var temp = new TempDirectory();
        var db = new SqliteDatabase(temp.Combine("novaget.db"));
        new DatabaseMigrator(db).Migrate();
        using (var c = db.Open())
        {
            c.Execute("UPDATE schema_version SET version = 999;");
        }

        var ex = Assert.Throws<DatabaseTooNewException>(() => new DatabaseMigrator(db).Migrate());
        Assert.Equal(999, ex.DatabaseVersion);
    }

    [Fact]
    public void Non_contiguous_migrations_are_rejected()
    {
        using var temp = new TempDirectory();
        var db = new SqliteDatabase(temp.Combine("novaget.db"));

        Assert.Throws<ArgumentException>(() => new DatabaseMigrator(db, [new Migration(1, "a", "SELECT 1;"), new Migration(3, "c", "SELECT 1;")]));
    }
}
