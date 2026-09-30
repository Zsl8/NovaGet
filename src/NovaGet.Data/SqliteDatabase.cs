using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace NovaGet.Data;

/// <summary>Connection factory for novaget.db. Each caller opens its own (pooled) connection.</summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    static SqliteDatabase()
    {
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
    }

    public SqliteDatabase(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string FilePath { get; }

    /// <summary>Opens a connection with foreign keys on and WAL-friendly durability.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Stores DateTime values as round-trip ISO-8601 UTC text and reads them back as UTC.</summary>
    private sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = ToUtc(value).ToString("O", CultureInfo.InvariantCulture);
        }

        public override DateTime Parse(object value) => value switch
        {
            DateTime dt => ToUtc(dt),
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            long ticks => new DateTime(ticks, DateTimeKind.Utc),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTime."),
        };

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
