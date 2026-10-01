namespace NovaGet.Core.Settings;

/// <summary>
/// Root of settings.json. Sections mirror the Options dialog tabs (section 9) plus UI state.
/// Every property has a default so a missing or partial file still yields a usable configuration.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();

    public FileTypeSettings FileTypes { get; set; } = new();

    public SaveToSettings SaveTo { get; set; } = new();

    public DownloadSettings Downloads { get; set; } = new();

    public ConnectionSettings Connection { get; set; } = new();

    public ProxySettings Proxy { get; set; } = new();

    public DialUpSettings DialUp { get; set; } = new();

    public SoundSettings Sounds { get; set; } = new();

    public AdvancedSettings Advanced { get; set; } = new();

    public SpeedLimiterSettings SpeedLimiter { get; set; } = new();

    public UiSettings Ui { get; set; } = new();
}

public enum CaptureModifier
{
    None,
    Alt,
    Ctrl,
    Shift,
    Insert,
    CtrlAlt,
    CtrlShift,
    AltShift,
}

public enum WebPanelPosition
{
    TopRight,
    TopLeft,
    Bottom,
}

/// <summary>Options → General.</summary>
public sealed class GeneralSettings
{
    public bool LaunchOnStartup { get; set; } = true;

    /// <summary>Extension captures downloads; when off only the context menu hands links over.</summary>
    public bool UseAdvancedBrowserIntegration { get; set; } = true;

    /// <summary>Browser ids (chrome, edge, firefox, opera, brave, vivaldi) the user wants integrated.</summary>
    public List<string> IntegratedBrowsers { get; set; } = ["chrome", "edge", "firefox", "opera", "brave", "vivaldi"];

    public CaptureModifier PreventCaptureKey { get; set; } = CaptureModifier.Alt;

    public CaptureModifier ForceCaptureKey { get; set; } = CaptureModifier.CtrlAlt;

    public ContextMenuSettings ContextMenu { get; set; } = new();

    public WebPlayerPanelSettings WebPlayerPanel { get; set; } = new();

    public string ToolbarSkin { get; set; } = "Default";

    /// <summary>UI culture name, e.g. <c>en</c> or <c>ar</c>.</summary>
    public string Language { get; set; } = "en";

    public bool MonitorClipboard { get; set; }

    public bool ShowDropTarget { get; set; }

    public bool ShowTraySpeedGraph { get; set; } = true;

    /// <summary>The window's X button hides to the tray instead of exiting.</summary>
    public bool CloseButtonHidesToTray { get; set; } = true;

    /// <summary>SetThreadExecutionState while downloads run (section 16).</summary>
    public bool PreventSleepWhileDownloading { get; set; } = true;
}

public sealed class ContextMenuSettings
{
    public bool DownloadWithNovaGet { get; set; } = true;

    public bool DownloadAllLinks { get; set; } = true;

    public bool DownloadVideo { get; set; } = true;
}

public sealed class WebPlayerPanelSettings
{
    public bool Show { get; set; } = true;

    public bool ShowInPopupWindows { get; set; } = true;

    public WebPanelPosition Position { get; set; } = WebPanelPosition.TopRight;

    public bool ShowOnlyOnHover { get; set; }

    public List<string> ExcludedSites { get; set; } = [];
}

/// <summary>Options → File Types.</summary>
public sealed class FileTypeSettings
{
    public const string DefaultAutoCaptureExtensions =
        "3gp 7z aac ace aif apk arj asf avi bin bz2 exe gz gzip img iso lzh m4a m4v mkv mov mp3 mp4 mpa mpe mpeg mpg " +
        "msi msu ogg ogv pdf plj pps ppt qt r0* r1* ra rar rm rmvb sea sit sitx tar tif tiff wav webm wma wmv z zip";

    /// <summary>Space-separated extension patterns (wildcards allowed, e.g. <c>r0*</c>).</summary>
    public string AutoCaptureExtensions { get; set; } = DefaultAutoCaptureExtensions;

    /// <summary>Host patterns, e.g. <c>*.update.microsoft.com</c>.</summary>
    public List<string> ExcludedSites { get; set; } = ["*.update.microsoft.com", "download.windowsupdate.com"];

    /// <summary>Full address patterns that are never captured.</summary>
    public List<string> ExcludedAddresses { get; set; } = [];
}

/// <summary>Options → Save To (category folders live in the Category table).</summary>
public sealed class SaveToSettings
{
    /// <summary>Null or empty means <see cref="Paths.AppPaths.DefaultTempDir"/>.</summary>
    public string? TempDirectory { get; set; }

    public bool UseSameDirectoryForAllCategories { get; set; }

    public string? SameDirectory { get; set; }

    /// <summary>Most recently used folders for the Save As combobox.</summary>
    public List<string> RecentFolders { get; set; } = [];
}

public enum DuplicateDownloadAction
{
    ShowDialog,
    AddNumbered,
    AddAndOverwrite,
    ShowCompleteOrResume,
}

public enum CompletedDoubleClickAction
{
    Properties,
    OpenFile,
    OpenFolder,
}

/// <summary>Options → Downloads.</summary>
public sealed class DownloadSettings
{
    public bool ShowStartDialog { get; set; } = true;

    public bool ShowCompleteDialog { get; set; } = true;

    public bool StartImmediatelyWhileShowingInfoDialog { get; set; }

    public bool ShowProgressDialog { get; set; } = true;

    public bool StartProgressDialogMinimized { get; set; }

    public DuplicateDownloadAction DuplicateAction { get; set; } = DuplicateDownloadAction.ShowDialog;

    public bool ShowQueueSelectionOnDownloadLater { get; set; } = true;

    public VirusScanSettings VirusScan { get; set; } = new();

    public bool UseCustomUserAgent { get; set; }

    public string CustomUserAgent { get; set; } = string.Empty;

    public bool KeepServerFileDate { get; set; }

    /// <summary>Writes the Zone.Identifier alternate data stream (Mark of the Web).</summary>
    public bool MarkAsDownloadedFromInternet { get; set; } = true;

    public CompletedDoubleClickAction CompletedDoubleClick { get; set; } = CompletedDoubleClickAction.Properties;

    /// <summary>Address history for the Add URL combobox (max 20).</summary>
    public List<string> AddressHistory { get; set; } = [];
}

public sealed class VirusScanSettings
{
    public const string WindowsDefenderProgram = @"%ProgramFiles%\Windows Defender\MpCmdRun.exe";
    public const string WindowsDefenderArguments = "-Scan -ScanType 3 -File \"[file]\"";

    public bool Enabled { get; set; }

    public string Program { get; set; } = string.Empty;

    /// <summary><c>[file]</c> is replaced with the downloaded file path.</summary>
    public string Arguments { get; set; } = "-scan \"[file]\"";
}

public enum ConnectionSpeedType
{
    LowSpeed,
    Medium,
    HighSpeed,
    Custom,
}

/// <summary>Options → Connection (per-server exceptions live in the ServerException table).</summary>
public sealed class ConnectionSettings
{
    public static readonly int[] AllowedConnectionCounts = [1, 2, 4, 8, 16, 24, 32];

    public ConnectionSpeedType ConnectionType { get; set; } = ConnectionSpeedType.Medium;

    public int DefaultMaxConnections { get; set; } = 8;

    public DownloadQuotaSettings DownloadLimit { get; set; } = new();

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 20;

    /// <summary>Retry backoff base: 3 s, 6 s, 12 s … capped at 30 s (engine spec §4.5).</summary>
    public int RetryDelaySeconds { get; set; } = 3;

    /// <summary>Answer NTLM/Negotiate (Kerberos) challenges with the Windows account when no login is set.</summary>
    public bool UseWindowsAuthentication { get; set; }

    /// <summary>"Remember speed limit for this server" values from the progress dialog, host → KB/s.</summary>
    public Dictionary<string, int> HostSpeedLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the default connection count for a connection speed preset.</summary>
    public static int ConnectionsFor(ConnectionSpeedType type) => type switch
    {
        ConnectionSpeedType.LowSpeed => 2,
        ConnectionSpeedType.Medium => 8,
        ConnectionSpeedType.HighSpeed => 16,
        _ => 8,
    };
}

public sealed class DownloadQuotaSettings
{
    public bool Enabled { get; set; }

    public int MaxMegabytes { get; set; } = 200;

    public int PeriodHours { get; set; } = 5;

    public bool WarnBeforeStopping { get; set; }
}

public enum ProxyMode
{
    None,
    System,
    Manual,
    AutoConfigScript,
}

public enum SocksVersion
{
    Socks4,
    Socks4a,
    Socks5,
}

/// <summary>Options → Proxy/Socks. Passwords are DPAPI-protected strings.</summary>
public sealed class ProxySettings
{
    public ProxyMode Mode { get; set; } = ProxyMode.System;

    public string PacUrl { get; set; } = string.Empty;

    public ProxyServerSettings Http { get; set; } = new();

    public ProxyServerSettings Https { get; set; } = new();

    public ProxyServerSettings Ftp { get; set; } = new();

    public bool UseSameProxyForAllProtocols { get; set; } = true;

    public SocksSettings Socks { get; set; } = new();

    /// <summary>Semicolon- or space-separated host patterns that bypass the proxy.</summary>
    public string BypassList { get; set; } = "localhost;127.0.0.1;<local>";

    /// <summary>"Use FTP in PASV mode": off means active data connections (EPRT/PORT) when no proxy is used.</summary>
    public bool FtpPassiveMode { get; set; } = true;
}

public sealed class ProxyServerSettings
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 8080;

    public string User { get; set; } = string.Empty;

    public string ProtectedPassword { get; set; } = string.Empty;
}

public sealed class SocksSettings
{
    public bool Enabled { get; set; }

    public SocksVersion Version { get; set; } = SocksVersion.Socks5;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 1080;

    public string User { get; set; } = string.Empty;

    public string ProtectedPassword { get; set; } = string.Empty;

    public bool ResolveDnsThroughSocks { get; set; } = true;
}

/// <summary>Options → Dial Up / VPN.</summary>
public sealed class DialUpSettings
{
    public string ConnectionName { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    public string ProtectedPassword { get; set; } = string.Empty;

    public bool RedialIfDisconnected { get; set; }

    public int RedialAttempts { get; set; } = 10;

    public int RedialDelaySeconds { get; set; } = 5;

    public bool ConnectBeforeScheduledQueues { get; set; }
}

public enum SoundEvent
{
    DownloadComplete,
    DownloadFailed,
    QueueStarted,
    QueueStopped,
    DownloadAdded,
}

/// <summary>Options → Sounds.</summary>
public sealed class SoundSettings
{
    public SoundEntry DownloadComplete { get; set; } = new() { Enabled = true, Path = "sounds\\complete.wav" };

    public SoundEntry DownloadFailed { get; set; } = new() { Enabled = true, Path = "sounds\\failed.wav" };

    public SoundEntry QueueStarted { get; set; } = new() { Enabled = false, Path = "sounds\\queue-started.wav" };

    public SoundEntry QueueStopped { get; set; } = new() { Enabled = false, Path = "sounds\\queue-finished.wav" };

    public SoundEntry DownloadAdded { get; set; } = new() { Enabled = false, Path = "sounds\\added.wav" };

    public SoundEntry Get(SoundEvent soundEvent) => soundEvent switch
    {
        SoundEvent.DownloadComplete => DownloadComplete,
        SoundEvent.DownloadFailed => DownloadFailed,
        SoundEvent.QueueStarted => QueueStarted,
        SoundEvent.QueueStopped => QueueStopped,
        SoundEvent.DownloadAdded => DownloadAdded,
        _ => throw new ArgumentOutOfRangeException(nameof(soundEvent)),
    };
}

public sealed class SoundEntry
{
    public bool Enabled { get; set; }

    /// <summary>Absolute path, or a path relative to the install directory.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Options → Advanced.</summary>
public sealed class AdvancedSettings
{
    public int MinSegmentSizeKB { get; set; } = 64;

    public int WriteBufferSizeKB { get; set; } = 256;

    public bool PreallocateDiskSpace { get; set; } = true;

    public bool KeepTempFilesAfterCancel { get; set; }

    public bool CheckForUpdatesWeekly { get; set; } = true;

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Per-download "Ignore certificate errors" is only honored when this is on.</summary>
    public bool AllowIgnoringCertificateErrors { get; set; } = true;
}

/// <summary>Downloads → Speed Limiter.</summary>
public sealed class SpeedLimiterSettings
{
    public static readonly int[] TrayPresetsKBps = [64, 128, 256, 512, 1024];

    public bool Enabled { get; set; }

    public int MaxKBps { get; set; } = 256;

    public bool ApplyToSchedulerQueuesOnly { get; set; } = true;
}

public enum ToolbarButtonSize
{
    Large,
    Small,
}

public enum AppTheme
{
    Light,
    Dark,
    System,
}

/// <summary>Window placement, list layout and toolbar layout.</summary>
public sealed class UiSettings
{
    public WindowPlacement MainWindow { get; set; } = new() { Width = 960, Height = 580 };

    public double CategoriesPaneWidth { get; set; } = 190;

    public bool ShowCategories { get; set; } = true;

    public List<ColumnSetting> Columns { get; set; } = [];

    public string SortColumn { get; set; } = "AddedAt";

    public bool SortDescending { get; set; }

    public ToolbarButtonSize ToolbarSize { get; set; } = ToolbarButtonSize.Large;

    public bool ToolbarShowLabels { get; set; } = true;

    /// <summary>Ordered toolbar button ids; empty means the default order with all buttons visible.</summary>
    public List<ToolbarButtonSetting> ToolbarButtons { get; set; } = [];

    public AppTheme Theme { get; set; } = AppTheme.Light;

    public double? DropTargetLeft { get; set; }

    public double? DropTargetTop { get; set; }

    public WindowPlacement Scheduler { get; set; } = new() { Width = 720, Height = 520 };
}

public sealed class WindowPlacement
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

public sealed class ColumnSetting
{
    public string Id { get; set; } = string.Empty;

    public double Width { get; set; }

    public int DisplayIndex { get; set; }

    public bool Visible { get; set; } = true;
}

public sealed class ToolbarButtonSetting
{
    public string Id { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;
}
