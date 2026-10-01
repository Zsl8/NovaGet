using System.ComponentModel;
using System.Runtime.InteropServices;
using NovaGet.Core.Models;
using Serilog;

namespace NovaGet.App.Services;

/// <summary>Shut down / sleep / hibernate / log off (after the 30-second countdown the caller shows).</summary>
public static partial class PowerService
{
    private const uint EwxLogOff = 0x0;
    private const uint EwxPowerOff = 0x8;
    private const uint EwxShutdown = 0x1;
    private const uint EwxForce = 0x4;
    private const uint EwxForceIfHung = 0x10;
    private const uint ShtdnReasonFlagPlanned = 0x80000000;
    private const uint ShtdnReasonMajorApplication = 0x00040000;

    public static bool Execute(PowerAction action, bool forceProcessesToTerminate)
    {
        try
        {
            var force = forceProcessesToTerminate ? EwxForce : EwxForceIfHung;
            switch (action)
            {
                case PowerAction.ShutDown:
                    EnableShutdownPrivilege();
                    return ExitWindowsEx(EwxShutdown | EwxPowerOff | force, ShtdnReasonFlagPlanned | ShtdnReasonMajorApplication);
                case PowerAction.LogOff:
                    return ExitWindowsEx(EwxLogOff | force, ShtdnReasonFlagPlanned | ShtdnReasonMajorApplication);
                case PowerAction.Sleep:
                    EnableShutdownPrivilege();
                    return SetSuspendState(hibernate: false, force: forceProcessesToTerminate, disableWakeEvent: false);
                case PowerAction.Hibernate:
                    EnableShutdownPrivilege();
                    return SetSuspendState(hibernate: true, force: forceProcessesToTerminate, disableWakeEvent: false);
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error(ex, "Power action {Action} failed", action);
            return false;
        }
    }

    private static void EnableShutdownPrivilege()
    {
        const uint TokenAdjustPrivileges = 0x20;
        const uint TokenQuery = 0x8;
        const uint SePrivilegeEnabled = 0x2;

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExitWindowsEx(uint flags, uint reason);

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool force,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint length, IntPtr previous, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

/// <summary>Disconnects dial-up/VPN connections ("Hang up modem when done").</summary>
public interface IDialUpService
{
    /// <summary>Hangs up the configured connection (or every active one). Returns how many were closed.</summary>
    int HangUp();
}

/// <summary>Placeholder until the Dial Up / VPN options are implemented; logs the request.</summary>
internal sealed class NoDialUpService : IDialUpService
{
    public int HangUp()
    {
        Log.Information("Hang up requested; dial-up support is not available in this build");
        return 0;
    }
}
