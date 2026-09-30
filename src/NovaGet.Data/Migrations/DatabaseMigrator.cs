using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NovaGet.Data.Migrations;

public sealed record MigrationResult(int FromVersion, int ToVersion, string? BackupFile);

/// <summary>The database was written by a newer NovaGet than this one.</summary>
public sealed class DatabaseTooNewException(int databaseVersion, int supportedVersion)
    : Exception($"The download database is version {databaseVersion}, but this NovaGet supports up to {supportedVersion}. Please install the latest NovaGet.")
{
    public int DatabaseVersion { get; } = databaseVersion;

    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>
/// Brings novaget.db to the latest schema. Tracks the version in <c>schema_version</c>, applies each
/// pending migration in its own transaction, enables WAL, and backs up an existing database before upgrading it.
/// </summary>
public sealed class DatabaseMigrator
{
    private readonly SqliteDatabase _database;
    private readonly IReadOnlyList<Migration> _migrations;
    private readonly ILogger _logger;

    public DatabaseMigrator(SqliteDatabase database, ILogger<DatabaseMigrator>? logger = null)
        : this(database, SchemaMigrations.All, logger)
    {
    }

    public DatabaseMigrator(SqliteDatabase database, IReadOnlyList<Migration> migrations, ILogger<DatabaseMigrator>? logger = null)
    {
        _database = database;
        _migrations = [.. migrations.OrderBy(m => m.Version)];
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        for (var i = 0; i < _migrations.Count; i++)
        {
            if (_migrations[i].Version != i + 1)
            {
                throw new ArgumentException("Migration versions must start at 1 and be contiguous.", nameof(migrations));
            }
        }
    }

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public MigrationResult Migrate()
    {
        var directory = Path.GetDirectoryName(_database.FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = _database.Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL, appliedAt TEXT NOT NULL);");

        var current = GetVersion(connection);
        if (current > LatestVersion)
        {
            throw new DatabaseTooNewException(current, LatestVersion);
        }

        if (current == LatestVersion)
        {
            return new MigrationResult(current, current, null);
        }

        string? backup = null;
        if (current > 0)
        {
            backup = Backup(connection, current);
        }

        foreach (var migration in _migrations.Where(m => m.Version > current))
        {
            _logger.LogInformation("Applying database migration {Version}: {Name}", migration.Version, migration.Name);
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version (version, appliedAt) VALUES ($v, $at);";
                command.Parameters.AddWithValue("$v", migration.Version);
                command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return new MigrationResult(current, LatestVersion, backup);
    }

    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string Backup(SqliteConnection connection, int version)
    {
        var backupFile = $"{_database.FilePath}.v{version}.bak";
        _logger.LogInformation("Backing up database version {Version} to {Backup}", version, backupFile);
        if (File.Exists(backupFile))
        {
            File.Delete(backupFile);
        }

        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupFile, Pooling = false }.ToString()))
        {
            destination.Open();
            connection.BackupDatabase(destination);
        }

        return backupFile;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
