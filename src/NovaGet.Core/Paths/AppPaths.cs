using System.Runtime.InteropServices;

namespace NovaGet.Core.Paths;

/// <summary>
/// Resolves every location NovaGet reads or writes.
/// Normal mode: settings + DB in %APPDATA%\NovaGet, temp + logs in %LOCALAPPDATA%\NovaGet.
/// Portable mode (portable.flag next to the exe): everything under .\Data.
/// </summary>
public sealed class AppPaths
{
    private AppPaths(string roamingDir, string localDir, string executableDir, bool isPortable)
    {
        RoamingDir = roamingDir;
        LocalDir = localDir;
        ExecutableDir = executableDir;
        IsPortable = isPortable;
    }

    /// <summary>Directory for settings and the database.</summary>
    public string RoamingDir { get; }

    /// <summary>Directory for temp files, logs and caches.</summary>
    public string LocalDir { get; }

    /// <summary>Directory that contains NovaGet.exe.</summary>
    public string ExecutableDir { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(RoamingDir, "settings.json");

    public string SettingsBackupFile => SettingsFile + ".bak";

    public string DatabaseFile => Path.Combine(RoamingDir, "novaget.db");

    /// <summary>Default temporary directory; Options → Save To can override it.</summary>
    public string DefaultTempDir => Path.Combine(LocalDir, "Temp");

    public string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>The user's Downloads folder (the "General" category directory).</summary>
    public string UserDownloadsDir => s_downloads.Value;

    private static readonly Lazy<string> s_downloads = new(ResolveDownloadsFolder);

    /// <summary>Resolves paths for the running executable, honoring portable mode.</summary>
    public static AppPaths Resolve(string? executableDir = null)
    {
        executableDir ??= AppContext.BaseDirectory;
        executableDir = Path.GetFullPath(executableDir);

        if (File.Exists(Path.Combine(executableDir, AppInfo.PortableFlagFileName)))
        {
            var data = Path.Combine(executableDir, "Data");
            return new AppPaths(data, data, executableDir, isPortable: true);
        }

        var roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            AppInfo.ProductName);
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            AppInfo.ProductName);
        return new AppPaths(roaming, local, executableDir, isPortable: false);
    }

    /// <summary>Creates paths rooted in an arbitrary folder (tests, tools).</summary>
    public static AppPaths ForRoot(string root)
    {
        root = Path.GetFullPath(root);
        return new AppPaths(Path.Combine(root, "Roaming"), Path.Combine(root, "Local"), root, isPortable: false);
    }

    /// <summary>Creates the directories NovaGet needs before anything else runs.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RoamingDir);
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(DefaultTempDir);
    }

    private static string ResolveDownloadsFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
                if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var pathPtr) == 0)
                {
                    try
                    {
                        var path = Marshal.PtrToStringUni(pathPtr);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            return path;
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pathPtr);
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
            {
                // Fall through to the profile-relative default.
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
