using Dapper;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;
using NovaGet.Core.Security;

namespace NovaGet.Data.Repositories;

public sealed class SiteLoginRepository(SqliteDatabase database, ISecretProtector protector) : ISiteLoginRepository
{
    public IReadOnlyList<SiteLogin> GetAll()
    {
        using var connection = database.Open();
        return [.. connection.Query<SiteLogin>("SELECT id, urlPattern, user, passwordDpapi AS Password FROM SiteLogin ORDER BY id;")
            .Select(l =>
            {
                l.Password = protector.Unprotect(l.Password) ?? string.Empty;
                return l;
            })];
    }

    public long Insert(SiteLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        using var connection = database.Open();
        login.Id = connection.ExecuteScalar<long>(
            "INSERT INTO SiteLogin (urlPattern, user, passwordDpapi) VALUES (@UrlPattern, @User, @Password); SELECT last_insert_rowid();",
            new { login.UrlPattern, login.User, Password = protector.Protect(login.Password) });
        return login.Id;
    }

    public void Update(SiteLogin login)
    {
        ArgumentNullException.ThrowIfNull(login);
        using var connection = database.Open();
        connection.Execute(
            "UPDATE SiteLogin SET urlPattern = @UrlPattern, user = @User, passwordDpapi = @Password WHERE id = @Id;",
            new { login.Id, login.UrlPattern, login.User, Password = protector.Protect(login.Password) });
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        connection.Execute("DELETE FROM SiteLogin WHERE id = @id;", new { id });
    }
}
