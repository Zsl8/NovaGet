namespace NovaGet.Core.Settings;

/// <summary>Repairs out-of-range values from hand-edited or older settings files.</summary>
public static class SettingsNormalizer
{
    public static void Normalize(AppSettings s)
    {
        s.General ??= new();
        s.General.IntegratedBrowsers ??= [];
        s.General.ContextMenu ??= new();
        s.General.WebPlayerPanel ??= new();
        s.General.WebPlayerPanel.ExcludedSites ??= [];
        s.General.Language = string.IsNullOrWhiteSpace(s.General.Language) ? "en" : s.General.Language.Trim();
        s.General.ToolbarSkin = string.IsNullOrWhiteSpace(s.General.ToolbarSkin) ? "Default" : s.General.ToolbarSkin;

        s.FileTypes ??= new();
        s.FileTypes.AutoCaptureExtensions ??= FileTypeSettings.DefaultAutoCaptureExtensions;
        s.FileTypes.ExcludedSites ??= [];
        s.FileTypes.ExcludedAddresses ??= [];

        s.SaveTo ??= new();
        s.SaveTo.RecentFolders ??= [];
        TrimList(s.SaveTo.RecentFolders, 20);

        s.Downloads ??= new();
        s.Downloads.VirusScan ??= new();
        s.Downloads.AddressHistory ??= [];
        s.Downloads.CustomUserAgent ??= string.Empty;
        TrimList(s.Downloads.AddressHistory, 20);

        s.Connection ??= new();
        s.Connection.DownloadLimit ??= new();
        s.Connection.HostSpeedLimits = s.Connection.HostSpeedLimits is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(s.Connection.HostSpeedLimits, StringComparer.OrdinalIgnoreCase);
        s.Connection.DefaultMaxConnections = NearestAllowedConnections(s.Connection.DefaultMaxConnections);
        s.Connection.TimeoutSeconds = Math.Clamp(s.Connection.TimeoutSeconds, 5, 600);
        s.Connection.MaxRetries = Math.Clamp(s.Connection.MaxRetries, 0, 1000);
        s.Connection.RetryDelaySeconds = Math.Clamp(s.Connection.RetryDelaySeconds, 1, 3600);
        s.Connection.DownloadLimit.MaxMegabytes = Math.Max(1, s.Connection.DownloadLimit.MaxMegabytes);
        s.Connection.DownloadLimit.PeriodHours = Math.Clamp(s.Connection.DownloadLimit.PeriodHours, 1, 24 * 31);

        s.Proxy ??= new();
        s.Proxy.Http ??= new();
        s.Proxy.Https ??= new();
        s.Proxy.Ftp ??= new();
        s.Proxy.Socks ??= new();
        s.Proxy.PacUrl ??= string.Empty;
        s.Proxy.BypassList ??= string.Empty;

        s.DialUp ??= new();
        s.DialUp.RedialAttempts = Math.Clamp(s.DialUp.RedialAttempts, 1, 100);
        s.DialUp.RedialDelaySeconds = Math.Clamp(s.DialUp.RedialDelaySeconds, 1, 3600);

        s.Sounds ??= new();
        s.Sounds.DownloadComplete ??= new();
        s.Sounds.DownloadFailed ??= new();
        s.Sounds.QueueStarted ??= new();
        s.Sounds.QueueStopped ??= new();
        s.Sounds.DownloadAdded ??= new();

        s.Advanced ??= new();
        s.Advanced.MinSegmentSizeKB = Math.Clamp(s.Advanced.MinSegmentSizeKB, 16, 64 * 1024);
        s.Advanced.WriteBufferSizeKB = Math.Clamp(s.Advanced.WriteBufferSizeKB, 16, 16 * 1024);

        s.SpeedLimiter ??= new();
        s.SpeedLimiter.MaxKBps = Math.Clamp(s.SpeedLimiter.MaxKBps, 1, 10_000_000);

        s.Ui ??= new();
        s.Ui.MainWindow ??= new();
        s.Ui.Scheduler ??= new();
        s.Ui.Columns ??= [];
        s.Ui.ToolbarButtons ??= [];
        s.Ui.SortColumn ??= "AddedAt";
        if (s.Ui.MainWindow.Width < 640 || s.Ui.MainWindow.Height < 400)
        {
            s.Ui.MainWindow.Width = Math.Max(s.Ui.MainWindow.Width, 900);
            s.Ui.MainWindow.Height = Math.Max(s.Ui.MainWindow.Height, 560);
        }

        s.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    /// <summary>Snaps a connection count to the nearest value offered in the UI (1, 2, 4, 8, 16, 24, 32).</summary>
    public static int NearestAllowedConnections(int value)
    {
        var best = ConnectionSettings.AllowedConnectionCounts[0];
        foreach (var candidate in ConnectionSettings.AllowedConnectionCounts)
        {
            if (Math.Abs(candidate - value) < Math.Abs(best - value))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static void TrimList(List<string> list, int max)
    {
        list.RemoveAll(string.IsNullOrWhiteSpace);
        if (list.Count > max)
        {
            list.RemoveRange(max, list.Count - max);
        }
    }
}
