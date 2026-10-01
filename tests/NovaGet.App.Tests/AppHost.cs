using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaGet.App.Hosting;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Paths;
using NovaGet.Data.Migrations;

namespace NovaGet.App.Tests;

/// <summary>The real composition root over a throwaway profile folder.</summary>
public sealed class AppHost : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novaget-ui-tests", Guid.NewGuid().ToString("N"));

    public AppHost(Action<IServiceCollection>? overrides = null)
    {
        Paths = AppPaths.ForRoot(_root);
        Paths.EnsureCreated();
        Host = AppHostBuilder.Build(Paths, new CommandLineOptions(), overrides);
        Host.Services.GetRequiredService<DatabaseMigrator>().Migrate();
    }

    public AppPaths Paths { get; }

    public IHost Host { get; }

    public T Get<T>()
        where T : notnull => Host.Services.GetRequiredService<T>();

    public void Dispose()
    {
        Host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
