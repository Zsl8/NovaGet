namespace NovaGet.Core.Engine.Naming;

/// <summary>
/// Picks a download's file name in this order: Content-Disposition <c>filename*</c> → <c>filename</c> →
/// last segment of the final URL path (after redirects) → <c>index.html</c>. The result is sanitized, and an
/// extension is added from the Content-Type when the name has none.
/// </summary>
public static class FileNameResolver
{
    public const string DefaultPageName = "index.html";

    public static string Resolve(string? contentDisposition, Uri finalUri, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(finalUri);
        var name = ContentDispositionParser.GetFileName(contentDisposition);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = NameFromUrl(finalUri);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return DefaultPageName;
        }

        name = FileNameSanitizer.Sanitize(name);
        if (!Path.HasExtension(name) && MimeTypes.ExtensionFor(contentType) is { } extension)
        {
            name = FileNameSanitizer.Truncate(name + extension, FileNameSanitizer.MaxLength);
        }

        return name;
    }

    /// <summary>The %-decoded last path segment of the URL, or null when the path ends with '/'.</summary>
    public static string? NameFromUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        var slash = path.LastIndexOf('/');
        var segment = slash >= 0 ? path[(slash + 1)..] : path;
        if (segment.Length == 0)
        {
            return null;
        }

        var decoded = ContentDispositionParser.PercentDecode(segment, System.Text.Encoding.UTF8) ?? segment;
        return decoded.Length == 0 ? null : decoded;
    }
}
