using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovaGet.Core.Ipc;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Integration;

/// <summary>What the extension needs to decide captures and draw its menus (sent with <c>hello</c>/<c>getSettings</c>).</summary>
public sealed record ExtensionSettings
{
    /// <summary>Integration is on for the calling browser (Options → General checklist).</summary>
    public bool Enabled { get; init; }

    /// <summary>"Use advanced browser integration": capture downloads, not only the context menu.</summary>
    public bool Capture { get; init; }

    public IReadOnlyList<string> FileTypes { get; init; } = [];

    public IReadOnlyList<string> ExcludedSites { get; init; } = [];

    public IReadOnlyList<string> ExcludedAddresses { get; init; } = [];

    /// <summary>Modifier names: none, alt, ctrl, shift, insert, ctrlAlt, ctrlShift, altShift.</summary>
    public string PreventKey { get; init; } = "alt";

    public string ForceKey { get; init; } = "ctrlAlt";

    public bool MenuDownloadLink { get; init; }

    public bool MenuDownloadAll { get; init; }

    public bool MenuDownloadVideo { get; init; }

    public bool PanelShow { get; init; }

    public bool PanelInPopups { get; init; }

    public string PanelPosition { get; init; } = "topRight";

    public bool PanelOnHover { get; init; }

    public IReadOnlyList<string> PanelExcludedSites { get; init; } = [];

    public string AppVersion { get; init; } = AppInfo.InformationalVersion;

    /// <summary>Changes whenever any of the above changes, so the extension can tell a refresh apart cheaply.</summary>
    public string Revision { get; init; } = string.Empty;

    /// <summary>Sites whose login NovaGet is waiting for (the popup then offers "Send this site's login to NovaGet").</summary>
    public IReadOnlyList<string> LoginRequests { get; init; } = [];

    public static ExtensionSettings From(AppSettings settings, string? browser)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var general = settings.General;
        var enabled = browser is null || general.IntegratedBrowsers.Contains(browser, StringComparer.OrdinalIgnoreCase);
        var result = new ExtensionSettings
        {
            Enabled = enabled,
            Capture = enabled && general.UseAdvancedBrowserIntegration,
            FileTypes = CategoryMatcher.SplitPatterns(settings.FileTypes.AutoCaptureExtensions),
            ExcludedSites = [.. settings.FileTypes.ExcludedSites],
            ExcludedAddresses = [.. settings.FileTypes.ExcludedAddresses],
            PreventKey = KeyName(general.PreventCaptureKey),
            ForceKey = KeyName(general.ForceCaptureKey),
            MenuDownloadLink = enabled && general.ContextMenu.DownloadWithNovaGet,
            MenuDownloadAll = enabled && general.ContextMenu.DownloadAllLinks,
            MenuDownloadVideo = enabled && general.ContextMenu.DownloadVideo,
            PanelShow = enabled && general.WebPlayerPanel.Show,
            PanelInPopups = general.WebPlayerPanel.ShowInPopupWindows,
            PanelPosition = JsonNamingPolicy.CamelCase.ConvertName(general.WebPlayerPanel.Position.ToString()),
            PanelOnHover = general.WebPlayerPanel.ShowOnlyOnHover,
            PanelExcludedSites = [.. general.WebPlayerPanel.ExcludedSites],
        };

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result with { Revision = string.Empty }, IpcJson.Options)));
        return result with { Revision = Convert.ToHexString(hash, 0, 8).ToLowerInvariant() };
    }

    private static string KeyName(CaptureModifier key) => JsonNamingPolicy.CamelCase.ConvertName(key.ToString());
}
