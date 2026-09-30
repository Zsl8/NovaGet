namespace NovaGet.Core.CommandLine;

/// <summary>
/// Parsed IDM-compatible switches:
/// <c>NovaGet.exe [/d URL] [/s] [/p local_path] [/f local_file_name] [/q] [/h] [/n] [/a] [/tray] [/startqueue "name"] [/stopqueue "name"]</c>.
/// </summary>
public sealed class CommandLineOptions
{
    /// <summary><c>/d URL</c> — download this address.</summary>
    public string? Url { get; set; }

    /// <summary><c>/p</c> — save folder.</summary>
    public string? SaveFolder { get; set; }

    /// <summary><c>/f</c> — local file name.</summary>
    public string? FileName { get; set; }

    /// <summary><c>/n</c> — silent: no questions, no dialogs.</summary>
    public bool Silent { get; set; }

    /// <summary><c>/q</c> — exit after this download finishes successfully.</summary>
    public bool ExitWhenDone { get; set; }

    /// <summary><c>/h</c> — hang up the connection after a successful download.</summary>
    public bool HangUpWhenDone { get; set; }

    /// <summary><c>/a</c> — add to the main queue without starting.</summary>
    public bool AddToQueueOnly { get; set; }

    /// <summary><c>/s</c> — start the main queue.</summary>
    public bool StartMainQueue { get; set; }

    /// <summary><c>/tray</c> — start hidden in the tray.</summary>
    public bool StartInTray { get; set; }

    /// <summary><c>/startqueue "name"</c> (repeatable).</summary>
    public List<string> StartQueues { get; } = [];

    /// <summary><c>/stopqueue "name"</c> (repeatable).</summary>
    public List<string> StopQueues { get; } = [];

    /// <summary><c>/exit</c> — ask the running instance to quit (used by the installer).</summary>
    public bool Exit { get; set; }

    /// <summary><c>/cleanup</c> — remove run-time registrations (wake tasks etc.) and exit; used by the uninstaller.</summary>
    public bool Cleanup { get; set; }

    /// <summary>Problems found while parsing (unknown switches, missing values).</summary>
    public List<string> Errors { get; } = [];

    /// <summary>True when the command line asks the app to do something beyond starting up.</summary>
    public bool HasActions =>
        Url is not null || StartMainQueue || StartQueues.Count > 0 || StopQueues.Count > 0 || Exit;
}
