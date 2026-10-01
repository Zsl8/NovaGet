using NovaGet.Core.Abstractions;
using NovaGet.Core.Models;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Services;

/// <summary>Options → Advanced → Export settings / Import settings / Reset all to default.</summary>
public sealed class SettingsPackageService(
    ISettingsService settings,
    ICategoryRepository categories,
    IServerExceptionRepository serverExceptions)
{
    public string Export() => SettingsTransfer.Export(new SettingsPackage
    {
        Settings = settings.Current,
        Categories = categories.GetAll(),
        ServerExceptions = serverExceptions.GetAll(),
    });

    /// <summary>
    /// Applies an exported file: options replace the current ones (window layout and histories stay), categories
    /// are matched by name and updated or added, server exceptions are matched by host. Passwords are not in the
    /// file, so a proxy or dial-up entry with the same user keeps its saved password.
    /// </summary>
    public void Import(string json)
    {
        var package = SettingsTransfer.Import(json);
        var imported = package.Settings;
        var current = settings.Current;
        KeepPassword(imported.Proxy.Http, current.Proxy.Http);
        KeepPassword(imported.Proxy.Https, current.Proxy.Https);
        KeepPassword(imported.Proxy.Ftp, current.Proxy.Ftp);
        if (string.IsNullOrEmpty(imported.Proxy.Socks.ProtectedPassword) && imported.Proxy.Socks.User == current.Proxy.Socks.User)
        {
            imported.Proxy.Socks.ProtectedPassword = current.Proxy.Socks.ProtectedPassword;
        }

        if (string.IsNullOrEmpty(imported.DialUp.ProtectedPassword) && imported.DialUp.User == current.DialUp.User)
        {
            imported.DialUp.ProtectedPassword = current.DialUp.ProtectedPassword;
        }

        ImportCategories(package.Categories);
        ImportServerExceptions(package.ServerExceptions);
        settings.Update(s => OptionsApplier.Apply(s, imported));
    }

    public void ResetToDefaults() => settings.Replace(OptionsApplier.Defaults(settings.Current));

    private void ImportCategories(IReadOnlyList<Category> imported)
    {
        var existing = categories.GetAll();
        foreach (var category in imported)
        {
            var match = existing.FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                match.Extensions = category.Extensions;
                match.DefaultSaveDir = category.DefaultSaveDir;
                categories.Update(match);
            }
            else if (!category.IsBuiltIn)
            {
                categories.Insert(new Category
                {
                    Name = category.Name,
                    Extensions = category.Extensions,
                    DefaultSaveDir = category.DefaultSaveDir,
                });
                existing = categories.GetAll();
            }
        }
    }

    private void ImportServerExceptions(IReadOnlyList<ServerException> imported)
    {
        var existing = serverExceptions.GetAll();
        foreach (var exception in imported)
        {
            var match = existing.FirstOrDefault(e => string.Equals(e.Host, exception.Host, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                serverExceptions.Insert(new ServerException { Host = exception.Host, MaxConnections = exception.MaxConnections });
            }
            else
            {
                match.MaxConnections = exception.MaxConnections;
                serverExceptions.Update(match);
            }
        }
    }

    private static void KeepPassword(ProxyServerSettings imported, ProxyServerSettings current)
    {
        if (string.IsNullOrEmpty(imported.ProtectedPassword) && imported.Host == current.Host && imported.User == current.User)
        {
            imported.ProtectedPassword = current.ProtectedPassword;
        }
    }
}
