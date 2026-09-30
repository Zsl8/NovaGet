using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaGet.App.Services;
using NovaGet.Core;
using NovaGet.Core.CommandLine;
using Serilog;

namespace NovaGet.App;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly CommandLineOptions _startupOptions;

    public App(IHost host, CommandLineOptions startupOptions)
    {
        _host = host;
        _startupOptions = startupOptions;

        // Closing the main window hides it to the tray; only Exit ends the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public IServiceProvider Services => _host.Services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        _host.StartAsync().GetAwaiter().GetResult();

        var controller = _host.Services.GetRequiredService<IAppController>();
        controller.Initialize(showMainWindow: !_startupOptions.StartInTray);
        if (_startupOptions.HasActions)
        {
            controller.HandleCommandLine(_startupOptions);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stopping services");
        }

        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nNovaGet will keep running. Details were written to the log.",
            AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
