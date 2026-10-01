using System.Text.Json;

namespace NovaGet.Core.Settings;

/// <summary>
/// The choices made on the installer's Tasks page (<c>{app}\install-defaults.json</c>), applied once, to the
/// settings of a first run. "Launch at startup" is not here: the installer writes the Run value itself.
/// </summary>
public static class InstallDefaults
{
    public const string FileName = "install-defaults.json";

    /// <summary>Applies the file to <paramref name="settings"/>; returns false when there is no usable file.</summary>
    public static bool TryApply(AppSettings settings, string file)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!File.Exists(file))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(file);
            if (info.Length > 16 * 1024)
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (Flag(root, "monitorClipboard") is { } clipboard)
            {
                settings.General.MonitorClipboard = clipboard;
            }

            // Without the browser task the native host isn't registered, so no browser is integrated either.
            if (Flag(root, "browserIntegration") == false)
            {
                settings.General.IntegratedBrowsers = [];
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool? Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
