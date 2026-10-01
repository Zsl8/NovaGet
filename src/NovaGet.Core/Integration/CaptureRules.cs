using System.Text.RegularExpressions;
using NovaGet.Core.Engine;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Integration;

/// <summary>
/// Options → File Types: should a link be taken over automatically? (Clipboard monitoring uses this; the extension
/// applies the same rules to the settings it receives.)
/// </summary>
public sealed class CaptureRules
{
    private readonly IReadOnlyList<string> _extensions;
    private readonly IReadOnlyList<string> _excludedSites;
    private readonly IReadOnlyList<Regex> _excludedAddresses;

    public CaptureRules(FileTypeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _extensions = CategoryMatcher.SplitPatterns(settings.AutoCaptureExtensions);
        _excludedSites = settings.ExcludedSites;
        _excludedAddresses = [.. settings.ExcludedAddresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(Wildcard)];
    }

    /// <summary>Extension patterns, e.g. <c>zip</c>, <c>r0*</c> (lower case, no dots).</summary>
    public IReadOnlyList<string> Extensions => _extensions;

    public bool IsExcluded(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return _excludedSites.Any(site => HostPattern.Matches(site, url.Host) || WildcardMatch(site, url.Host))
            || _excludedAddresses.Any(pattern => pattern.IsMatch(url.AbsoluteUri));
    }

    /// <summary>True when the file name (or the address's last segment) has an auto-start extension.</summary>
    public bool MatchesFileType(Uri url, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        var name = string.IsNullOrWhiteSpace(fileName) ? Uri.UnescapeDataString(url.AbsolutePath.Split('/')[^1]) : fileName;
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return false;
        }

        var extension = name[(dot + 1)..].ToLowerInvariant();
        return _extensions.Any(pattern => WildcardMatch(pattern, extension));
    }

    public bool ShouldCapture(Uri url, string? fileName = null) =>
        BrowserMessageValidator.IsAllowedUrl(url) && !IsExcluded(url) && MatchesFileType(url, fileName);

    private static bool WildcardMatch(string pattern, string text) =>
        (pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal))
            ? Wildcard(pattern).IsMatch(text)
            : string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase);

    private static Regex Wildcard(string pattern) => new(
        "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
