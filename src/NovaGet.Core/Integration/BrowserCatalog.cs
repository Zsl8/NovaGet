namespace NovaGet.Core.Integration;

public enum ExtensionFamily
{
    /// <summary>Chromium-based: installs the MV3 extension, talks to <c>com.novaget.nativehost</c> via chrome.json.</summary>
    Chromium,

    /// <summary>Firefox: the same extension packaged for AMO, native host manifest firefox.json.</summary>
    Firefox,
}

/// <summary>A browser NovaGet can integrate with.</summary>
public sealed record BrowserInfo(string Id, string DisplayName, ExtensionFamily Family, IReadOnlyList<string> ExecutableNames)
{
    /// <summary>Anchor in docs/install-extension.html with the steps for this browser.</summary>
    public string HelpAnchor => Family == ExtensionFamily.Firefox ? "firefox" : "chromium";
}

/// <summary>The browsers listed in Options → General (section 9.1), in display order.</summary>
public static class BrowserCatalog
{
    public static IReadOnlyList<BrowserInfo> All { get; } =
    [
        new("chrome", "Google Chrome", ExtensionFamily.Chromium, ["chrome.exe"]),
        new("edge", "Microsoft Edge", ExtensionFamily.Chromium, ["msedge.exe"]),
        new("firefox", "Mozilla Firefox", ExtensionFamily.Firefox, ["firefox.exe"]),
        new("opera", "Opera", ExtensionFamily.Chromium, ["opera.exe", "launcher.exe"]),
        new("brave", "Brave", ExtensionFamily.Chromium, ["brave.exe"]),
        new("vivaldi", "Vivaldi", ExtensionFamily.Chromium, ["vivaldi.exe"]),
    ];

    public static BrowserInfo? Find(string? id) =>
        All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Identifies a browser from its executable path, e.g. a <c>StartMenuInternet\…\shell\open\command</c> value or
    /// the process that started the native host. Opera's <c>launcher.exe</c> only counts inside an Opera folder.
    /// </summary>
    public static BrowserInfo? FromExecutable(string? commandOrPath)
    {
        var path = ExecutablePath(commandOrPath);
        if (path is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(path.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
        foreach (var browser in All)
        {
            if (!browser.ExecutableNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fileName.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("opera", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return browser;
        }

        return null;
    }

    /// <summary>The executable part of a command line: a quoted first token, or everything up to <c>.exe</c>.</summary>
    public static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
    }
}
