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

    // Windows and features. Each is wired up by the milestone that builds it; until then it says so.

    /// <summary>"Enter new address to download", optionally pre-filled.</summary>
    void ShowAddUrl(string? url = null);

    void ShowAddBatch(bool fromClipboard);

    void ShowOptions(string? page = null);

    void ShowScheduler(long? queueId = null);

    void ShowGrabber(long? projectId = null);

    void ShowImport(bool ef2);

    void ShowExport(bool ef2, IReadOnlyCollection<long>? ids);

    /// <summary>Starts or resumes a download the user asked for (opens its progress dialog if enabled).</summary>
    void StartDownload(long downloadId);

    /// <summary>Progress dialog of an unfinished download.</summary>
    void ShowProgress(long downloadId);

    void ShowProperties(long downloadId);

    void ShowMoveRename(long downloadId);

    void RefreshAddress(long downloadId);

    void StartQueue(long queueId);

    void StopQueue(long queueId);

    /// <summary>Stops every queue and download (Stop all / Pause all).</summary>
    Task StopAllAsync();

    /// <summary>Asks for a name and adds a queue; null when canceled.</summary>
    NovaGet.Core.Models.DownloadQueue? CreateQueue();

    /// <summary>Deletes a user queue after its files leave it.</summary>
    Task DeleteQueueAsync(long queueId);

    void ToggleDropTarget();

    /// <summary>Links dropped or pasted: one opens Add URL, several the "Download all links" dialog.</summary>
    void AddDropped(IReadOnlyList<Uri> links);

    void CheckForUpdates();

    void ShowHelp(string? topic = null);
}
