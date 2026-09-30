using System.Diagnostics;
using NovaGet.Core;
using NovaGet.Core.Ipc;

namespace NovaGet.NativeHost;

/// <summary>
/// Launched by the browser (Chrome/Edge/Firefox native messaging). stdin/stdout carry the framed JSON
/// protocol, so nothing else may ever be written to stdout. Diagnostics go to stderr, which browsers log.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan s_quickConnect = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_startupConnect = TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        // args[0] is the calling extension's origin (Chrome) or the manifest path (Firefox); not needed here.
        _ = args;
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            var relay = new NativeMessagingRelay(ConnectAsync);
            await relay.RunAsync(stdin, stdout).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"NovaGet native host error: {ex}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Connects to the running app; starts <c>NovaGet.exe /tray</c> and retries for up to 5 s if needed.</summary>
    private static async Task<PipeClient?> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = await PipeClient.TryConnectWithRetryAsync(AppInfo.PipeName, s_quickConnect, cancellationToken).ConfigureAwait(false);
        if (client is not null)
        {
            return client;
        }

        if (!TryStartApp())
        {
            return null;
        }

        return await PipeClient.TryConnectWithRetryAsync(AppInfo.PipeName, s_startupConnect, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryStartApp()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "NovaGet.exe" : "NovaGet");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"NovaGet native host: app not found at {exe}");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, "/tray")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"NovaGet native host: could not start the app: {ex.Message}");
            return false;
        }
    }
}
