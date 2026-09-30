namespace NovaGet.Core.Engine.Naming;

/// <summary>Maps common Content-Types to extensions for names that arrive without one.</summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> s_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/vnd.rar"] = ".rar",
        ["application/x-7z-compressed"] = ".7z",
        ["application/gzip"] = ".gz",
        ["application/x-gzip"] = ".gz",
        ["application/x-tar"] = ".tar",
        ["application/x-bzip2"] = ".bz2",
        ["application/x-xz"] = ".xz",
        ["application/pdf"] = ".pdf",
        ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["application/epub+zip"] = ".epub",
        ["application/x-msdownload"] = ".exe",
        ["application/x-msdos-program"] = ".exe",
        ["application/x-msi"] = ".msi",
        ["application/x-ms-installer"] = ".msi",
        ["application/vnd.android.package-archive"] = ".apk",
        ["application/x-iso9660-image"] = ".iso",
        ["application/x-apple-diskimage"] = ".dmg",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["text/xml"] = ".xml",
        ["text/plain"] = ".txt",
        ["text/html"] = ".html",
        ["text/css"] = ".css",
        ["text/csv"] = ".csv",
        ["application/javascript"] = ".js",
        ["text/javascript"] = ".js",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",
        ["image/bmp"] = ".bmp",
        ["image/tiff"] = ".tif",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/ogg"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/flac"] = ".flac",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/x-matroska"] = ".mkv",
        ["video/quicktime"] = ".mov",
        ["video/x-msvideo"] = ".avi",
        ["video/x-flv"] = ".flv",
        ["video/mp2t"] = ".ts",
        ["video/3gpp"] = ".3gp",
        ["application/vnd.apple.mpegurl"] = ".m3u8",
        ["application/x-mpegurl"] = ".m3u8",
        ["application/dash+xml"] = ".mpd",
    };

    /// <summary>Extension (with dot) for a Content-Type such as <c>video/mp4; codecs=...</c>, or null.</summary>
    public static string? ExtensionFor(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var mime = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return s_extensions.TryGetValue(mime, out var extension) ? extension : null;
    }
}
