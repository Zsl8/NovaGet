using System.Security.Cryptography;
using System.Text;
using NovaGet.Core.Engine.Naming;

namespace NovaGet.Core.Grabber;

/// <summary>
/// Where an address is saved for offline browsing: <c>&lt;root&gt;\&lt;host&gt;\&lt;path&gt;</c>, "index.html" for folders,
/// ".html" added to pages without one, a short hash of the query in the name, and Windows-safe names and lengths.
/// </summary>
public static class OfflinePaths
{
    private const int MaxSegment = 80;
    private const int MaxPath = 240;
    private static readonly string[] s_pageExtensions = [".html", ".htm", ".xhtml", ".shtml"];

    public static string LocalPath(string root, Uri url, bool isPage)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(url);
        var host = FileNameSanitizer.Sanitize(url.IsDefaultPort ? url.Host : $"{url.Host}_{url.Port}");
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => FileNameSanitizer.Truncate(FileNameSanitizer.Sanitize(Uri.UnescapeDataString(s)), MaxSegment))
            .Where(s => s is not ("." or ".."))
            .ToList();
        var folder = url.AbsolutePath.EndsWith('/') || segments.Count == 0;
        var name = folder ? "index.html" : segments[^1];
        if (!folder)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 ? name[..^extension.Length] : name;
        if (url.Query.Length > 1)
        {
            stem += "_" + Hash(url.Query);
        }

        if (isPage && !s_pageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            extension += ".html"; // about → about.html, index.php → index.php.html
        }

        var path = Path.Combine([root, host, .. segments, stem + extension]);
        if (path.Length > MaxPath)
        {
            // Too deep for Windows: keep it findable under the host with a name made from the whole address.
            path = Path.Combine(root, host, "_long", Hash(url.AbsoluteUri) + FileNameSanitizer.Truncate(extension, 16));
        }

        return path;
    }

    /// <summary>A link from one saved file to another ("../img/a.png"), with each segment escaped for HTML.</summary>
    public static string RelativeLink(string fromFile, string toFile)
    {
        ArgumentNullException.ThrowIfNull(fromFile);
        ArgumentNullException.ThrowIfNull(toFile);
        var relative = Path.GetRelativePath(Path.GetDirectoryName(fromFile)!, toFile);
        return string.Join('/', relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(s => s == ".." ? s : Uri.EscapeDataString(s)));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
}
