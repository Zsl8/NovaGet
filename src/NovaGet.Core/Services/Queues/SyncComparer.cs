using NovaGet.Core.Engine;
using NovaGet.Core.Models;

namespace NovaGet.Core.Services.Queues;

/// <summary>Synchronization queue: has the file on the server changed since it was downloaded?</summary>
public static class SyncComparer
{
    /// <summary>
    /// Changed when the size, a strong or weak ETag, or Last-Modified differ, or the downloaded file is gone.
    /// Values the server doesn't send are not compared.
    /// </summary>
    public static bool HasChanged(Download download, ProbeResult probe, bool fileExists)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(probe);
        if (!fileExists)
        {
            return true;
        }

        if (probe.Size >= 0 && download.Size >= 0 && probe.Size != download.Size)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(probe.ETag) && !string.IsNullOrEmpty(download.ETag)
            && !string.Equals(NormalizeETag(probe.ETag), NormalizeETag(download.ETag), StringComparison.Ordinal))
        {
            return true;
        }

        if (probe.LastModified is { } serverTime && download.LastModified is { } savedTime)
        {
            var saved = DateTime.SpecifyKind(savedTime, DateTimeKind.Utc);
            return Math.Abs((serverTime.UtcDateTime - saved).TotalSeconds) >= 1;
        }

        return false;
    }

    private static string NormalizeETag(string tag)
    {
        tag = tag.Trim();
        return tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? tag[2..] : tag;
    }
}
