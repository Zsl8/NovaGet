using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.Views;
using NovaGet.Core;
using NovaGet.Core.CommandLine;
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
