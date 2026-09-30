using Dapper;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;

namespace NovaGet.Data.Repositories;

public sealed class ServerExceptionRepository(SqliteDatabase database) : IServerExceptionRepository
{
    public IReadOnlyList<ServerException> GetAll()
    {
        using var connection = database.Open();
        return [.. connection.Query<ServerException>("SELECT id, host, maxConnections FROM ServerException ORDER BY host;")];
    }

    public long Insert(ServerException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        using var connection = database.Open();
        exception.Id = connection.ExecuteScalar<long>(
            "INSERT INTO ServerException (host, maxConnections) VALUES (@Host, @MaxConnections); SELECT last_insert_rowid();",
            exception);
        return exception.Id;
    }

    public void Update(ServerException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        using var connection = database.Open();
        connection.Execute("UPDATE ServerException SET host = @Host, maxConnections = @MaxConnections WHERE id = @Id;", exception);
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM ServerException WHERE id = @id;", new { id });
    }
}
