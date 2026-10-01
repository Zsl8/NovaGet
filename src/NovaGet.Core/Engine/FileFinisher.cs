using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Models;
using NovaGet.Core.Services;

namespace NovaGet.Core.Engine;

/// <summary>
/// What happens to a finished file before the download is marked complete (sections 4.9, 9.4 and 22): the checksum is
/// verified when an expected value was given, the Mark of the Web is written, and the configured virus scanner runs.
/// Downloaded files are never opened or run.
/// </summary>
public static class FileFinisher
{
    public const string ZoneIdentifierStream = ":Zone.Identifier";

    /// <summary>Returns a warning for the user (a checksum that doesn't match), or null.</summary>
    public static async Task<string?> FinishAsync(
        Download download,
        string path,
        EngineOptions options,
        Action<DownloadStatus, string> setStatus,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(logger);
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(download.ChecksumExpected))
        {
            var algorithm = download.ChecksumAlgo ?? Checksum.GuessAlgorithm(download.ChecksumExpected) ?? Checksum.Sha256;
            setStatus(DownloadStatus.Assembling, "Verifying checksum...");
            var actual = await Checksum.ComputeAsync(path, algorithm, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!Checksum.Matches(download.ChecksumExpected, actual))
            {
                warning = string.Create(CultureInfo.InvariantCulture, $"The {algorithm} checksum doesn't match: expected {download.ChecksumExpected.Trim()}, got {actual}.");
                logger.LogWarning("Download {Id}: {Warning}", download.Id, warning);
            }
        }

        if (options.MarkOfTheWeb)
        {
            WriteZoneIdentifier(path, download.Referrer, string.IsNullOrWhiteSpace(download.OriginalUrl) ? download.Url : download.OriginalUrl, logger);
        }

        if (!string.IsNullOrWhiteSpace(options.VirusScanProgram))
        {
            setStatus(DownloadStatus.Scanning, "Scanning for viruses...");
            await ScanAsync(options.VirusScanProgram, options.VirusScanArguments ?? "\"[file]\"", path, options.VirusScanTimeout, logger, cancellationToken).ConfigureAwait(false);
        }

        return warning;
    }

    /// <summary>The Zone.Identifier contents browsers write: Internet zone, the page and the file's address (no logins).</summary>
    public static string ZoneIdentifier(string? referrer, string? hostUrl)
    {
        var text = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (Clean(referrer) is { } page)
        {
            text.Append("ReferrerUrl=").Append(page).Append("\r\n");
        }

        if (Clean(hostUrl) is { } host)
        {
            text.Append("HostUrl=").Append(host).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Writes the Mark of the Web (an NTFS alternate data stream); file systems without streams are skipped.</summary>
    public static void WriteZoneIdentifier(string path, string? referrer, string? hostUrl, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.WriteAllText(path + ZoneIdentifierStream, ZoneIdentifier(referrer, hostUrl), Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogInformation("Could not mark {Path} as downloaded from the Internet: {Error}", path, ex.Message);
        }
    }

    /// <summary>
    /// The command line for the scanner: <c>[file]</c> becomes the quoted path (or the bare path when the template already
    /// puts quotes around it).
    /// </summary>
    public static string ScanArguments(string template, string path)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Contains("\"[file]\"", StringComparison.OrdinalIgnoreCase)
            ? template.Replace("[file]", path, StringComparison.OrdinalIgnoreCase)
            : template.Replace("[file]", "\"" + path + "\"", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ScanAsync(string program, string arguments, string path, TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
    {
        var executable = Environment.ExpandEnvironmentVariables(program.Trim().Trim('"'));
        var start = new ProcessStartInfo(executable, ScanArguments(arguments, path))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return;
            }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("The virus scanner did not finish within {Timeout}; it was stopped", timeout);
                process.Kill(entireProcessTree: true);
                return;
            }

            logger.LogInformation("Virus scan of {Path} finished with exit code {Code}", path, process.ExitCode);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            logger.LogWarning("The virus scanner {Program} could not be started: {Error}", executable, ex.Message);
        }
    }

    private static string? Clean(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
        {
            return null;
        }

        // Never write a login into the stream.
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        var text = builder.Uri.AbsoluteUri;
        return text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal) ? null : text;
    }
}
