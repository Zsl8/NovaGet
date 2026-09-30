using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace NovaGet.App.Services;

/// <summary>Explorer integration: opening files and folders and Windows file-type icons.</summary>
public static partial class ShellService
{
    private static readonly ConcurrentDictionary<(string Extension, bool Large), ImageSource?> s_icons = new();

    /// <summary>Opens a file with its associated program. Only ever called from an explicit user action.</summary>
    public static bool OpenFile(string path) => Run(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));

    /// <summary>Shows the Windows "Open with" dialog for a file.</summary>
    public static bool OpenWith(string path) =>
        Run(() => Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false }));

    /// <summary>Opens Explorer with the file selected (or the folder itself if the file is gone).</summary>
    public static bool OpenFolder(string path)
    {
        if (File.Exists(path))
        {
            return Run(() => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }));
        }

        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return folder is not null && Directory.Exists(folder)
            && Run(() => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }));
    }

    /// <summary>Opens a web page or local document in the default handler.</summary>
    public static bool OpenUrl(string url) => Run(() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));

    /// <summary>The Windows icon for a file type (by extension), cached. Works for files that don't exist yet.</summary>
    public static ImageSource? IconFor(string fileName, bool large = false)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension) || extension is ".exe" or ".ico" or ".lnk")
        {
            // Per-file icons would need the actual file; use the generic type icon for these.
            extension = string.IsNullOrEmpty(extension) ? ".__none" : extension;
        }

        return s_icons.GetOrAdd((extension, large), static key => LoadIcon(key.Extension, key.Large));
    }

    private static BitmapSource? LoadIcon(string extension, bool large)
    {
        const uint FileAttributeNormal = 0x80;
        const uint ShgfiIcon = 0x100;
        const uint ShgfiLargeIcon = 0x0;
        const uint ShgfiSmallIcon = 0x1;
        const uint ShgfiUseFileAttributes = 0x10;

        try
        {
            var info = default(ShFileInfo);
            var flags = ShgfiIcon | ShgfiUseFileAttributes | (large ? ShgfiLargeIcon : ShgfiSmallIcon);
            if (SHGetFileInfo("file" + extension, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero
                || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or DllNotFoundException)
        {
            return null;
        }
    }

    private static bool Run(Func<Process?> start)
    {
        try
        {
            using var process = start();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warning(ex, "Shell action failed");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
