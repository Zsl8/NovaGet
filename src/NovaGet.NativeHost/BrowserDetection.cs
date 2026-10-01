using System.Diagnostics;
using System.Runtime.InteropServices;
using NovaGet.Core.Integration;

namespace NovaGet.NativeHost;

/// <summary>
/// Which browser started this host: Chrome-based browsers on Windows launch native hosts through <c>cmd.exe</c>, so
/// the parent chain is walked a few levels up until a known browser executable appears.
/// </summary>
internal static partial class BrowserDetection
{
    public static string? Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var pid = Environment.ProcessId;
            for (var level = 0; level < 4; level++)
            {
                pid = ParentOf(pid);
                if (pid <= 0)
                {
                    return null;
                }

                using var process = Process.GetProcessById(pid);
                if (BrowserCatalog.FromExecutable(process.MainModule?.FileName) is { } browser)
                {
                    return browser.Id;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        return null;
    }

    private static int ParentOf(int pid)
    {
        using var process = Process.GetProcessById(pid);
        var info = default(ProcessBasicInformation);
        var status = NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        return status == 0 ? checked((int)info.InheritedFromUniqueProcessId) : -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returnLength);
}
