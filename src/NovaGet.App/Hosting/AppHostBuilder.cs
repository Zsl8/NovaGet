using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.ViewModels.Scheduler;
using NovaGet.App.Views;
using NovaGet.Core;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Http;
using NovaGet.Core.Ipc;
using NovaGet.Core.Paths;
using NovaGet.Core.Security;
using NovaGet.Core.Services.Queues;
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
        if (OperatingSystem.IsWindows())
        {
            builder.Services.AddSingleton<NovaGet.Core.Network.IPacResolver, WinHttpPacResolver>();
        }

        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new HttpClientProvider(
                () => settings.Current.Proxy,
                sp.GetRequiredService<ISecretProtector>(),
                sp.GetService<NovaGet.Core.Network.IPacResolver>());
        });
        builder.Services.AddSingleton<IHttpClientProvider>(sp => sp.GetRequiredService<HttpClientProvider>());
        builder.Services.AddSingleton(sp => new SiteCredentials(sp.GetRequiredService<NovaGet.Core.Abstractions.ISiteLoginRepository>()));
        builder.Services.AddSingleton<ISiteCredentials>(sp => sp.GetRequiredService<SiteCredentials>());
        builder.Services.AddSingleton<ITransferProtocol>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new HttpTransferProtocol(
                sp.GetRequiredService<IHttpClientProvider>(),
                sp.GetRequiredService<ISiteCredentials>(),
                () => settings.Current.Connection.UseWindowsAuthentication);
        });
        builder.Services.AddSingleton<ITransferProtocol>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new NovaGet.Core.Engine.Ftp.FtpTransferProtocol(
                sp.GetRequiredService<ISiteCredentials>(),
                () => settings.Current.Proxy,
                sp.GetRequiredService<ISecretProtector>());
        });
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
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                sp.GetRequiredService<NovaGet.Core.Engine.Streams.IStreamMuxer>());
        });
        builder.Services.AddSingleton<NovaGet.Core.Engine.Streams.IStreamMuxer>(_ =>
            new NovaGet.Core.Engine.Streams.FfmpegMuxer(NovaGet.Core.Engine.Streams.FfmpegMuxer.Locate(AppContext.BaseDirectory)));
        builder.Services.AddSingleton<NovaGet.Core.Engine.Streams.IStreamProber>(sp =>
            new NovaGet.Core.Engine.Streams.StreamManifestLoader(sp.GetServices<ITransferProtocol>()));
        builder.Services.AddSingleton<IDownloadEngine>(sp => sp.GetRequiredService<DownloadEngine>());
        builder.Services.AddSingleton<IDownloadProber, DownloadProber>();
        builder.Services.AddHostedService<EngineLifetimeService>();

        // Download list
        builder.Services.AddSingleton<NovaGet.Core.Services.IDownloadService, NovaGet.Core.Services.DownloadService>();

        // Queues and scheduler
        builder.Services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            return new QueueManager(
                sp.GetRequiredService<NovaGet.Core.Services.IDownloadService>(),
                sp.GetRequiredService<NovaGet.Core.Abstractions.IQueueRepository>(),
                sp.GetRequiredService<IDownloadProber>(),
                () => EngineOptions.FromSettings(settings.Current, paths),
                TimeProvider.System,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<QueueManager>>());
        });
        builder.Services.AddSingleton<IQueueManager>(sp => sp.GetRequiredService<QueueManager>());
        builder.Services.AddHostedService<QueueLifetimeService>();
        builder.Services.AddSingleton<WakeTaskService>();
        builder.Services.AddSingleton<QueueUiService>();
        builder.Services.AddSingleton(sp => new NovaGet.Core.Services.DownloadQuotaService(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IDownloadEngine>(),
            sp.GetRequiredService<NovaGet.Core.Services.IDownloadService>(),
            sp.GetRequiredService<IQueueManager>(),
            System.IO.Path.Combine(paths.RoamingDir, "quota.json"),
            TimeProvider.System,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NovaGet.Core.Services.DownloadQuotaService>>()));
        builder.Services.AddSingleton<QuotaUiService>();
        builder.Services.AddSingleton<ClipboardMonitor>();
        builder.Services.AddTransient(sp => new SchedulerViewModel(
            sp.GetRequiredService<NovaGet.Core.Abstractions.IQueueRepository>(),
            sp.GetRequiredService<NovaGet.Core.Services.IDownloadService>(),
            sp.GetRequiredService<IDownloadEngine>(),
            sp.GetRequiredService<IQueueManager>(),
            sp.GetRequiredService<IAppController>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<QueueUiService>()));
        builder.Services.AddSingleton<Func<SchedulerViewModel>>(sp => sp.GetRequiredService<SchedulerViewModel>);

        // App shell
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IDialUpService, RasDialUpService>();
        builder.Services.AddSingleton<DownloadUiService>();
        builder.Services.AddSingleton<SoundService>();
        builder.Services.AddSingleton<OptionsService>();
        builder.Services.AddSingleton<NovaGet.Core.Services.SettingsPackageService>();
        builder.Services.AddSingleton<IAppController, AppController>();
        builder.Services.AddSingleton(sp => new Lazy<IAppController>(sp.GetRequiredService<IAppController>));
        builder.Services.AddSingleton<TrayIconService>();
        builder.Services.AddSingleton<BrowserIntegrationService>();
        builder.Services.AddSingleton<IIpcRequestHandler, IpcRequestRouter>();
        builder.Services.AddHostedService<IpcServerHostedService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton(sp => new Lazy<MainWindow>(sp.GetRequiredService<MainWindow>));

        return builder.Build();
    }
}
