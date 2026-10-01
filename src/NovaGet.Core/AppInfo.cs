using System.Reflection;

namespace NovaGet.Core;

/// <summary>Product-wide constants and version information.</summary>
public static class AppInfo
{
    public const string ProductName = "NovaGet";

    /// <summary>Named mutex that marks a running instance. The installer checks it via AppMutex.</summary>
    public const string MutexName = "NovaGet.SingleInstance";

    /// <summary>Named pipe (<c>\\.\pipe\NovaGet.Main</c>) used for single-instance forwarding and the native host.</summary>
    public const string PipeName = "NovaGet.Main";

    /// <summary>Native messaging host name registered for the browsers.</summary>
    public const string NativeHostName = "com.novaget.nativehost";

    /// <summary>Name of the file that switches the app into portable mode when it sits next to the executable.</summary>
    public const string PortableFlagFileName = "portable.flag";

    /// <summary>The only address the app itself contacts: the latest release, for the optional update check.</summary>
    public const string UpdateFeed = "https://api.github.com/repos/Zsl8/NovaGet/releases/latest";

    private static readonly Lazy<Version> s_version = new(() =>
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0));

    private static readonly Lazy<string> s_informationalVersion = new(() =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? s_version.Value.ToString(3));

    public static Version Version => s_version.Value;

    /// <summary>Full semantic version, e.g. <c>1.0.0</c>.</summary>
    public static string InformationalVersion => s_informationalVersion.Value;

    /// <summary>Short version used in the main window title, e.g. <c>1.0</c>.</summary>
    public static string ShortVersion => $"{Version.Major}.{Version.Minor}";

    /// <summary>Main window title: <c>NovaGet x.y</c>.</summary>
    public static string MainWindowTitle => $"{ProductName} {ShortVersion}";
}
