using Microsoft.Extensions.DependencyInjection;
using NovaGet.Core.Abstractions;
using NovaGet.Data.Migrations;
using NovaGet.Data.Repositories;

namespace NovaGet.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the SQLite database, migrator and repositories. Requires an <see cref="Core.Security.ISecretProtector"/>.</summary>
    public static IServiceCollection AddNovaGetData(this IServiceCollection services, string databaseFile)
    {
        services.AddSingleton(new SqliteDatabase(databaseFile));
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<IDownloadRepository, DownloadRepository>();
        services.AddSingleton<ICategoryRepository, CategoryRepository>();
        services.AddSingleton<IQueueRepository, QueueRepository>();
        services.AddSingleton<ISiteLoginRepository, SiteLoginRepository>();
        services.AddSingleton<IServerExceptionRepository, ServerExceptionRepository>();
        return services;
    }
}
