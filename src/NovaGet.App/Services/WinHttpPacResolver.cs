using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Network;

namespace NovaGet.App.Services;

/// <summary>
/// "Use automatic configuration script (PAC)": asks WinHTTP which proxy a URL uses. WinHTTP downloads and runs the
/// script (and caches it per session); answers are cached here for a few minutes per script and host.
/// </summary>
internal sealed partial class WinHttpPacResolver(ILogger<WinHttpPacResolver> logger) : IPacResolver, IDisposable
{
    private const uint AccessTypeNoProxy = 1;
    private const uint AutoProxyConfigUrl = 0x2;
    private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Script, string Scheme, string Host), (Uri? Proxy, DateTime Until)> _cache = new();
    private readonly object _gate = new();
    private IntPtr _session;

    public Uri? Resolve(Uri script, Uri destination)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(destination);
        var key = (script.AbsoluteUri, destination.Scheme, destination.Host);
        if (_cache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow)
        {
            return cached.Proxy;
        }

        var proxy = Query(script, destination);
        _cache[key] = (proxy, DateTime.UtcNow + CacheTime);
        return proxy;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_session != IntPtr.Zero)
            {
                WinHttpCloseHandle(_session);
                _session = IntPtr.Zero;
            }
        }
    }

    /// <summary>Picks the proxy for the scheme from a WinHTTP answer such as <c>proxy:8080</c>, <c>a:80;b:80</c> or <c>http=a:80;https=b:443</c>.</summary>
    internal static Uri? ParseProxyList(string? list, string scheme)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return null;
        }

        string? fallback = null;
        foreach (var raw in list.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw;
            var equals = entry.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                if (entry[..equals].Equals(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    return ToUri(entry[(equals + 1)..]);
                }

                continue;
            }

            fallback ??= entry;
        }

        return fallback is null ? null : ToUri(fallback);

        static Uri? ToUri(string entry)
        {
            if (entry.Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (entry.StartsWith("PROXY ", StringComparison.OrdinalIgnoreCase))
            {
                entry = entry[6..];
            }

            return Uri.TryCreate(entry.Contains("://", StringComparison.Ordinal) ? entry : "http://" + entry, UriKind.Absolute, out var uri) ? uri : null;
        }
    }

    private Uri? Query(Uri script, Uri destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var session = Session();
        if (session == IntPtr.Zero)
        {
            return null;
        }

        var scriptUrl = Marshal.StringToHGlobalUni(script.AbsoluteUri);
        try
        {
            var options = new AutoProxyOptions
            {
                Flags = AutoProxyConfigUrl,
                AutoConfigUrl = scriptUrl,
                AutoLogonIfChallenged = 1,
            };
            if (!WinHttpGetProxyForUrl(session, destination.AbsoluteUri, ref options, out var info))
            {
                logger.LogInformation("The PAC script {Script} gave no proxy for {Host} (error {Error}); connecting directly",
                    script, destination.Host, Marshal.GetLastPInvokeError());
                return null;
            }

            try
            {
                return info.AccessType == AccessTypeNoProxy ? null : ParseProxyList(Marshal.PtrToStringUni(info.Proxy), destination.Scheme);
            }
            finally
            {
                if (info.Proxy != IntPtr.Zero)
                {
                    GlobalFree(info.Proxy);
                }

                if (info.ProxyBypass != IntPtr.Zero)
                {
                    GlobalFree(info.ProxyBypass);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(scriptUrl);
        }
    }

    private IntPtr Session()
    {
        lock (_gate)
        {
            if (_session == IntPtr.Zero)
            {
                _session = WinHttpOpen("NovaGet", AccessTypeNoProxy, null, null, 0);
            }

            return _session;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AutoProxyOptions
    {
        public uint Flags;
        public uint AutoDetectFlags;
        public IntPtr AutoConfigUrl;
        public IntPtr Reserved;
        public uint ReservedValue;
        public int AutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProxyInfo
    {
        public uint AccessType;
        public IntPtr Proxy;
        public IntPtr ProxyBypass;
    }

    [LibraryImport("winhttp.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr WinHttpOpen(string agent, uint accessType, string? proxyName, string? proxyBypass, uint flags);

    [LibraryImport("winhttp.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpGetProxyForUrl(IntPtr session, string url, ref AutoProxyOptions options, out ProxyInfo info);

    [LibraryImport("winhttp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpCloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr memory);
}
