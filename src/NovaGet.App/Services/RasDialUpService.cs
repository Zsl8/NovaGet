using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Security;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>
/// Dial-up and VPN connections through the Remote Access API (Options → Dial Up / VPN): hang up when done, connect
/// before scheduled queues, redial while a queue runs. RASCONN's size is taken from Windows itself (asked for with a
/// null buffer), so the layout matches whatever Windows version runs.
/// </summary>
internal sealed partial class RasDialUpService(ISettingsService settings, ISecretProtector protector, ILogger<RasDialUpService> logger) : IDialUpService
{
    private const int ErrorBufferTooSmall = 603;
    private const int RasMaxEntryName = 256;

    /// <summary>Hangs up the configured connection, or every active one when none is configured.</summary>
    public int HangUp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var configured = settings.Current.DialUp.ConnectionName;
        var closed = 0;
        foreach (var (handle, name) in ActiveConnections())
        {
            if (!string.IsNullOrEmpty(configured) && !string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = RasHangUpW(handle);
            logger.LogInformation("Hung up {Connection} (result {Result})", name, result);
            if (result == 0)
            {
                closed++;
                WaitUntilClosed(handle);
            }
        }

        return closed;
    }

    public bool IsConnected(string entry) =>
        OperatingSystem.IsWindows() && ActiveConnections().Any(c => string.Equals(c.Name, entry, StringComparison.OrdinalIgnoreCase));

    /// <summary>Connects the configured entry with the saved user and password. Returns an error message, or null.</summary>
    public string? Connect()
    {
        var dialUp = settings.Current.DialUp;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dialUp.ConnectionName))
        {
            return null;
        }

        if (IsConnected(dialUp.ConnectionName))
        {
            return null;
        }

        var attempts = dialUp.RedialIfDisconnected ? Math.Max(1, dialUp.RedialAttempts) : 1;
        string? error = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            error = Dial(dialUp.ConnectionName, dialUp.User, protector.Unprotect(dialUp.ProtectedPassword) ?? string.Empty);
            if (error is null)
            {
                logger.LogInformation("Connected {Connection}", dialUp.ConnectionName);
                return null;
            }

            logger.LogWarning("Connecting {Connection} failed (attempt {Attempt}): {Error}", dialUp.ConnectionName, attempt, error);
            if (attempt < attempts)
            {
                Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, dialUp.RedialDelaySeconds)));
            }
        }

        return error;
    }

    /// <summary>
    /// Saves the login with the connection (RasSetCredentials), then dials with <c>rasdial.exe "entry"</c>, which uses
    /// the saved login. The password never appears on a command line where other programs could read it.
    /// </summary>
    private string? Dial(string entry, string user, string password)
    {
        if (!string.IsNullOrEmpty(user))
        {
            var status = SaveCredentials(entry, user, password);
            if (status != 0)
            {
                logger.LogWarning("Saving the login for {Connection} failed with {Status}", entry, status);
            }
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rasdial.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(entry);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("rasdial did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill();
                return "The connection did not complete in time.";
            }

            return process.ExitCode == 0 ? null : output.Result.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// RASCREDENTIALSW: dwSize, dwMask, then UNLEN+1 user, PWLEN+1 password and DNLEN+1 domain characters (the same
    /// layout on every Windows version). Built by hand so the password can be wiped from memory afterwards.
    /// </summary>
    private static uint SaveCredentials(string entry, string user, string password)
    {
        const int nameChars = 257;
        const int passwordChars = 257;
        const int domainChars = 16;
        const int size = 8 + ((nameChars + passwordChars + domainChars) * 2);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            unsafe
            {
                new Span<byte>((void*)buffer, size).Clear();
            }

            Marshal.WriteInt32(buffer, 0, size);
            Marshal.WriteInt32(buffer, 4, RascmUserName | RascmPassword);
            Write(buffer + 8, user, nameChars);
            Write(buffer + 8 + (nameChars * 2), password, passwordChars);
            return RasSetCredentialsW(null, entry, buffer, false);
        }
        finally
        {
            unsafe
            {
                new Span<byte>((void*)buffer, size).Clear();
            }

            Marshal.FreeHGlobal(buffer);
        }

        static void Write(IntPtr target, string text, int maxChars)
        {
            var chars = text.AsSpan(0, Math.Min(text.Length, maxChars - 1));
            unsafe
            {
                chars.CopyTo(new Span<char>((void*)target, maxChars));
            }
        }
    }

    private List<(IntPtr Handle, string Name)> ActiveConnections()
    {
        var result = new List<(IntPtr, string)>();
        try
        {
            // Ask Windows how big RASCONN is (and how many there are).
            uint bytes = 0;
            uint count = 0;
            var status = RasEnumConnectionsW(IntPtr.Zero, ref bytes, ref count);
            if (count == 0 || (status != 0 && status != ErrorBufferTooSmall))
            {
                return result;
            }

            var entrySize = (int)(bytes / count);
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    Marshal.WriteInt32(buffer, i * entrySize, entrySize);
                }

                status = RasEnumConnectionsW(buffer, ref bytes, ref count);
                if (status != 0)
                {
                    logger.LogWarning("RasEnumConnections failed with {Status}", status);
                    return result;
                }

                // RASCONN is 4-byte packed: dwSize, then the handle, then the entry name.
                var nameOffset = 4 + IntPtr.Size;
                for (var i = 0; i < count; i++)
                {
                    var entry = buffer + (i * entrySize);
                    var handle = Marshal.ReadIntPtr(entry, 4);
                    var name = Marshal.PtrToStringUni(entry + nameOffset, RasMaxEntryName + 1).TrimEnd('\0');
                    var end = name.IndexOf('\0', StringComparison.Ordinal);
                    result.Add((handle, end >= 0 ? name[..end] : name));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(ex, "Remote Access is not available");
        }

        return result;
    }

    /// <summary>Windows asks callers to wait until the connection is really gone before exiting.</summary>
    private static void WaitUntilClosed(IntPtr handle)
    {
        var status = new byte[1024];
        for (var i = 0; i < 30; i++)
        {
            BitConverter.GetBytes(status.Length).CopyTo(status, 0);
            if (RasGetConnectStatusW(handle, status) != 0)
            {
                return; // ERROR_INVALID_HANDLE: closed
            }

            Thread.Sleep(100);
        }
    }

    private const int RascmUserName = 0x1;
    private const int RascmPassword = 0x2;

    [LibraryImport("rasapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RasSetCredentialsW(string? phonebook, string entry, IntPtr credentials, [MarshalAs(UnmanagedType.Bool)] bool clear);

    [LibraryImport("rasapi32.dll")]
    private static partial uint RasEnumConnectionsW(IntPtr connections, ref uint bytes, ref uint count);

    [LibraryImport("rasapi32.dll")]
    private static partial uint RasHangUpW(IntPtr connection);

    [LibraryImport("rasapi32.dll")]
    private static partial uint RasGetConnectStatusW(IntPtr connection, [In, Out] byte[] status);
}
