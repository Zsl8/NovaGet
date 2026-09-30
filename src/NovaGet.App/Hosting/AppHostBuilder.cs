using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.Views;
using NovaGet.Core;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Ipc;
using NovaGet.Core.Paths;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;
using NovaGet.Data;
using Serilog;

namespace NovaGet.App.Hosting;

/// <summary>Composition root: every service the app uses is registered here.</summary>
internal static class AppHostBuilder
{
    public static Microsoft.Extensions.Hosting.IHost Build(AppPaths paths, CommandLineOptions startupOptions)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // No appsettings.json, environment variables or console logging: settings.json is the only config.
            DisableDefaults = true,
            ApplicationName = AppInfo.ProductName,
            ContentRootPath = paths.ExecutableDir,
        });

        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSerilog(Log.Logger, dispose: false);

        // Core
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(startupOptions);
        builder.Services.AddSingleton<ISecretProtector>(_ => SecretProtector.CreateDefault());
        builder.Services.AddSingleton<ISettingsService, SettingsService>();

        // Data
        builder.Services.AddNovaGetData(paths.DatabaseFile);

        // Download engine (options are re-read from settings whenever a download starts)
        builder.Services.AddSingleton<HttpClientProvider>();
        builder.Services.AddSingleton<IHttpClientProvider>(sp => sp.GetRequiredService<HttpClientProvider>());
        builder.Services.AddSingleton<ITransferProtocol, HttpTransferProtocol>();
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            var serverExceptions = sp.GetRequiredService<NovaGet.Core.Abstractions.IServerExceptionRepository>();
            return new DownloadEngine(
                sp.GetRequiredService<NovaGet.Core.Abstractions.IDownloadRepository>(),
                sp.GetServices<ITransferProtocol>(),
                () => EngineOptions.FromSettings(settings.Current, paths) with
                {
                    ServerConnectionLimits = serverExceptions.GetAll()
                        .GroupBy(e => e.Host, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().MaxConnections, StringComparer.OrdinalIgnoreCase),
                },
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        });
        builder.Services.AddSingleton<IDownloadEngine>(sp => sp.GetRequiredService<DownloadEngine>());
        builder.Services.AddHostedService<EngineLifetimeService>();

        // App shell
        builder.Services.AddSingleton<IAppController, AppController>();
        builder.Services.AddSingleton<TrayIconService>();
        builder.Services.AddSingleton<IIpcRequestHandler, IpcRequestRouter>();
        builder.Services.AddHostedService<IpcServerHostedService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton(sp => new Lazy<MainWindow>(sp.GetRequiredService<MainWindow>));

        return builder.Build();
    }
}
