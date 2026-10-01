using Microsoft.Win32;
using NovaGet.Core;

namespace NovaGet.App.Services;

/// <summary>
/// Options → General → "Launch NovaGet on startup": the <c>Run</c> value (started with <c>/tray</c>). An
/// all-users install writes the value under HKLM, which a standard user can't delete, so turning it off also
/// marks it disabled in Explorer's <c>StartupApproved</c> list, exactly like Task Manager's Startup tab does.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const byte Enabled = 0x02;
    private const byte Disabled = 0x03;

    private static string ValueName => AppInfo.ProductName;

    public static bool IsEnabled()
    {
        try
        {
            var registered = HasValue(Registry.CurrentUser) || HasValue(Registry.LocalMachine);
            return registered && !IsDisabledByUser();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Returns false when the registry could not be changed.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey, writable: true);
            if (enabled)
            {
                run.SetValue(ValueName, $"\"{Environment.ProcessPath}\" /tray", RegistryValueKind.String);
                approved.SetValue(ValueName, ApprovalFlag(Enabled), RegistryValueKind.Binary);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
                if (HasValue(Registry.LocalMachine))
                {
                    approved.SetValue(ValueName, ApprovalFlag(Disabled), RegistryValueKind.Binary);
                }
                else
                {
                    approved.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Uninstall: removes the per-user values.</summary>
    public static void Remove()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
        }
    }

    private static bool HasValue(RegistryKey hive)
    {
        using var key = hive.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string { Length: > 0 };
    }

    private static bool IsDisabledByUser()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } flag && (flag[0] & 0x01) == 0x01;
    }

    /// <summary>12 bytes: the state, then the FILETIME it changed (Explorer shows "disabled since").</summary>
    private static byte[] ApprovalFlag(byte state)
    {
        var data = new byte[12];
        data[0] = state;
        if (state == Disabled)
        {
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }

        return data;
    }
}
