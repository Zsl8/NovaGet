using NovaGet.Core.CommandLine;

namespace NovaGet.App.Services;

/// <summary>App-level actions that can be triggered from any thread (IPC, tray, menus).</summary>
public interface IAppController
{
    /// <summary>Creates the tray icon and (unless starting in the tray) shows the main window. UI thread only.</summary>
    void Initialize(bool showMainWindow);

    void ShowMainWindow();

    void HandleCommandLine(CommandLineOptions options);

    /// <summary>Exits the whole app (as opposed to hiding the window to the tray).</summary>
    void RequestExit();

    bool IsExiting { get; }
}
