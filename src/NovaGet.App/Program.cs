using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NovaGet.App.Hosting;
using NovaGet.Core;
using NovaGet.Core.CommandLine;
using NovaGet.Core.Ipc;
using NovaGet.Core.Paths;
using NovaGet.Data.Migrations;
using Serilog;

namespace NovaGet.App;

public static partial class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitDatabaseError = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = CommandLineParser.Parse(args);
        if (options.Cleanup)
        {
            // Uninstaller hook: never start the UI.
            return UninstallCleanup.Run();
        }

        // The guard must be released on this (UI) thread, so it lives for the whole of Main.
        using var guard = SingleInstanceGuard.TryAcquire(AppInfo.MutexName);
        if (guard is null)
        {
            return ForwardToRunningInstance(args, options);
        }

        if (options.Exit)
        {
            return ExitOk; // "/exit" with nothing running: nothing to close.
        }

        var paths = AppPaths.Resolve();
        paths.EnsureCreated();
        Log.Logger = AppLogging.CreateLogger(paths);

        try
        {
            Log.Information("{Product} {Version} starting (portable: {Portable}, args: {Args})",
                AppInfo.ProductName, AppInfo.InformationalVersion, paths.IsPortable, args);
            foreach (var error in options.Errors)
            {
                Log.Warning("Command line: {Error}", error);
            }

            using var host = AppHostBuilder.Build(paths, options);
            if (!TryMigrateDatabase(host.Services))
            {
                return ExitDatabaseError;
            }

            var app = new App(host, options);
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "NovaGet terminated unexpectedly");
            MessageBox.Show(
                $"NovaGet could not start:\n\n{ex.Message}\n\nDetails were written to the log in:\n{paths.LogsDir}",
                AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            return ExitError;
        }
        finally
        {
            Log.Information("{Product} stopped", AppInfo.ProductName);
            Log.CloseAndFlush();
        }
    }

    private static bool TryMigrateDatabase(IServiceProvider services)
    {
        try
        {
            var result = services.GetRequiredService<DatabaseMigrator>().Migrate();
            if (result.FromVersion != result.ToVersion)
            {
                Log.Information("Database migrated from v{From} to v{To} (backup: {Backup})", result.FromVersion, result.ToVersion, result.BackupFile);
            }

            return true;
        }
        catch (DatabaseTooNewException ex)
        {
            Log.Error(ex, "Database is newer than this build");
            MessageBox.Show(ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Hands the command line to the instance that is already running, then exits.</summary>
    private static int ForwardToRunningInstance(string[] args, CommandLineOptions options)
    {
        var request = new IpcRequest
        {
            Type = options.Exit ? IpcRequestTypes.Exit
                : args.Length == 0 ? IpcRequestTypes.Activate
                : IpcRequestTypes.CommandLine,
            Args = [.. args],
        };

        // We were just launched by the user, so we may pass our foreground right to the running instance.
        _ = AllowSetForegroundWindow(AsfwAny);

        // The first instance may still be starting up; keep trying for a few seconds.
        var response = Task.Run(() => PipeClient.SendOnceAsync(AppInfo.PipeName, request, TimeSpan.FromSeconds(5)))
            .GetAwaiter().GetResult();
        return response?.Ok == true ? ExitOk : ExitError;
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int dwProcessId);
}
