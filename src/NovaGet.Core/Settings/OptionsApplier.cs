namespace NovaGet.Core.Settings;

/// <summary>
/// Copies what the Options dialog edits from an edited copy onto the live settings. State that changes while the
/// dialog is open (window layout, histories, the tray's speed limiter, remembered per-server limits, the last
/// update check) is kept from the live settings, so closing the dialog never rolls it back.
/// </summary>
public static class OptionsApplier
{
    public static void Apply(AppSettings target, AppSettings edited)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(edited);

        var copy = SettingsJson.Clone(edited);
        SettingsNormalizer.Normalize(copy);

        copy.General.MonitorClipboard = target.General.MonitorClipboard;
        copy.General.ShowDropTarget = target.General.ShowDropTarget;
        copy.SaveTo.RecentFolders = [.. target.SaveTo.RecentFolders];
        copy.Downloads.AddressHistory = [.. target.Downloads.AddressHistory];
        copy.Connection.HostSpeedLimits = new(target.Connection.HostSpeedLimits, StringComparer.OrdinalIgnoreCase);
        copy.Advanced.LastUpdateCheck = target.Advanced.LastUpdateCheck;

        target.General = copy.General;
        target.FileTypes = copy.FileTypes;
        target.SaveTo = copy.SaveTo;
        target.Downloads = copy.Downloads;
        target.Connection = copy.Connection;
        target.Proxy = copy.Proxy;
        target.DialUp = copy.DialUp;
        target.Sounds = copy.Sounds;
        target.Advanced = copy.Advanced;
    }

    /// <summary>
    /// "Reset all to default": every option back to its default. Window layout, histories and the speed limiter
    /// are not options and stay as they are.
    /// </summary>
    public static AppSettings Defaults(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var defaults = new AppSettings();
        var result = SettingsJson.Clone(current);
        Apply(result, defaults);
        result.General.MonitorClipboard = defaults.General.MonitorClipboard;
        result.General.ShowDropTarget = defaults.General.ShowDropTarget;
        return result;
    }
}
