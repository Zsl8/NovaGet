using System.Text.Json;

namespace NovaGet.Core.Integration;

/// <summary>Message types the extension sends through the native host (section 12).</summary>
public static class BrowserMessageTypes
{
    /// <summary>Startup handshake; replied with the settings.</summary>
    public const string Hello = "hello";

    public const string GetSettings = "getSettings";

    public const string Ping = "ping";

    /// <summary>A captured browser download or a "Download with NovaGet" link.</summary>
    public const string Download = "download";

    /// <summary>"Download all links with NovaGet".</summary>
    public const string Links = "links";

    /// <summary>"Download this video" / "Download video with NovaGet".</summary>
    public const string Media = "media";

    /// <summary>The popup's "Open NovaGet" button.</summary>
    public const string OpenApp = "openApp";

    /// <summary>The video panel's "Settings" item: Options → General (web player panel).</summary>
    public const string OpenOptions = "openOptions";
}

/// <summary>A validated message from the extension.</summary>
public abstract record BrowserMessage(string Type)
{
    /// <summary>Which browser sent it (from the native host), when known: chrome, edge, firefox, …</summary>
    public string? Browser { get; init; }
}

public sealed record BrowserSimpleMessage(string Type) : BrowserMessage(Type);

/// <summary>Request details shared by downloads, links and media.</summary>
public sealed record BrowserRequestInfo(string? Referrer, string? Cookies, string? UserAgent);

public sealed record BrowserDownload(
    Uri Url,
    Uri? FinalUrl,
    string? FileName,
    long FileSize,
    string? Mime,
    string? PageTitle,
    BrowserRequestInfo Request,
    string? PostData,
    bool Forced) : BrowserMessage(BrowserMessageTypes.Download);

public sealed record BrowserLink(Uri Url, string? Text, string? Kind);

public sealed record BrowserLinks(Uri? PageUrl, string? PageTitle, BrowserRequestInfo Request, IReadOnlyList<BrowserLink> Links)
    : BrowserMessage(BrowserMessageTypes.Links);

public sealed record BrowserMediaItem(Uri Url, string? Mime, string? Label, long Size, int Width, int Height, bool IsManifest);

public sealed record BrowserMedia(Uri? PageUrl, string? PageTitle, BrowserRequestInfo Request, IReadOnlyList<BrowserMediaItem> Items, bool Protected)
    : BrowserMessage(BrowserMessageTypes.Media);

/// <summary>
/// Section 22: everything from the native host is untrusted. Messages are parsed field by field (unknown fields are
/// ignored), addresses must be absolute http/https/ftp/ftps URLs, and every string and list has a maximum size.
/// </summary>
public static class BrowserMessageValidator
{
    public const int MaxUrlLength = 8192;
    public const int MaxFileNameLength = 1024;
    public const int MaxTitleLength = 2048;
    public const int MaxMimeLength = 255;
    public const int MaxUserAgentLength = 1024;
    public const int MaxCookieLength = 64 * 1024;
    public const int MaxPostDataLength = 64 * 1024;
    public const int MaxLinkTextLength = 1024;
    public const int MaxLinks = 20_000;
    public const int MaxMediaItems = 200;

    private static readonly string[] s_schemes = ["http", "https", "ftp", "ftps"];

    public static bool IsAllowedUrl(Uri uri) =>
        uri.IsAbsoluteUri && s_schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrEmpty(uri.Host);

    /// <summary>Parses and validates a message. Returns null with an error message when it isn't acceptable.</summary>
    public static BrowserMessage? Validate(JsonElement message, out string? error)
    {
        error = null;
        try
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("A message must be a JSON object.");
            }

            var type = String(message, "type", 64, required: true)!;
            return type switch
            {
                BrowserMessageTypes.Hello or BrowserMessageTypes.GetSettings or BrowserMessageTypes.Ping or BrowserMessageTypes.OpenApp
                    or BrowserMessageTypes.OpenOptions => new BrowserSimpleMessage(type),
                BrowserMessageTypes.Download => Download(message),
                BrowserMessageTypes.Links => Links(message),
                BrowserMessageTypes.Media => Media(message),
                _ => throw new FormatException($"Unknown message type '{type}'."),
            };
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static BrowserDownload Download(JsonElement m)
    {
        var url = Url(m, "url", required: true)!;
        return new BrowserDownload(
            url,
            Url(m, "finalUrl", required: false),
            String(m, "fileName", MaxFileNameLength),
            Long(m, "fileSize"),
            String(m, "mime", MaxMimeLength),
            String(m, "pageTitle", MaxTitleLength),
            Request(m),
            String(m, "postData", MaxPostDataLength),
            Bool(m, "forced"));
    }

    private static BrowserLinks Links(JsonElement m)
    {
        if (!m.TryGetProperty("links", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("'links' must be an array.");
        }

        if (array.GetArrayLength() > MaxLinks)
        {
            throw new FormatException($"Too many links (more than {MaxLinks}).");
        }

        var links = new List<BrowserLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // A page can contain odd links (javascript:, mailto:, data:): skip them rather than reject the page.
            Uri? url;
            try
            {
                url = Url(item, "url", required: true);
            }
            catch (FormatException)
            {
                continue;
            }

            if (url is not null && seen.Add(url.AbsoluteUri))
            {
                links.Add(new BrowserLink(url, String(item, "text", MaxLinkTextLength, truncate: true), String(item, "kind", 32)));
            }
        }

        return new BrowserLinks(Url(m, "pageUrl", required: false), String(m, "pageTitle", MaxTitleLength, truncate: true), Request(m), links);
    }

    private static BrowserMedia Media(JsonElement m)
    {
        if (!m.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("'items' must be an array.");
        }

        if (array.GetArrayLength() > MaxMediaItems)
        {
            throw new FormatException($"Too many media items (more than {MaxMediaItems}).");
        }

        var items = new List<BrowserMediaItem>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Media items must be objects.");
            }

            items.Add(new BrowserMediaItem(
                Url(item, "url", required: true)!,
                String(item, "mime", MaxMimeLength),
                String(item, "label", 256, truncate: true),
                Long(item, "size"),
                (int)Math.Clamp(Long(item, "width"), 0, 100_000),
                (int)Math.Clamp(Long(item, "height"), 0, 100_000),
                Bool(item, "manifest")));
        }

        return new BrowserMedia(Url(m, "pageUrl", required: false), String(m, "pageTitle", MaxTitleLength, truncate: true), Request(m), items, Bool(m, "protected"));
    }

    private static BrowserRequestInfo Request(JsonElement m)
    {
        var referrer = Url(m, "referrer", required: false);
        return new BrowserRequestInfo(referrer?.AbsoluteUri, String(m, "cookies", MaxCookieLength), String(m, "userAgent", MaxUserAgentLength));
    }

    private static Uri? Url(JsonElement m, string name, bool required)
    {
        var text = String(m, name, MaxUrlLength, required);
        if (text is null)
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsAllowedUrl(uri))
        {
            if (!required)
            {
                return null; // an optional referrer like "about:blank" is simply dropped
            }

            throw new FormatException($"'{name}' must be an http, https, ftp or ftps address.");
        }

        return uri;
    }

    private static string? String(JsonElement m, string name, int maxLength, bool required = false, bool truncate = false)
    {
        if (!m.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return required ? throw new FormatException($"'{name}' is required.") : null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"'{name}' must be a string.");
        }

        var text = value.GetString()!;
        if (text.Length > maxLength)
        {
            if (!truncate)
            {
                throw new FormatException($"'{name}' is longer than {maxLength} characters.");
            }

            text = text[..maxLength];
        }

        if (text.Any(c => c is '\0'))
        {
            throw new FormatException($"'{name}' contains a null character.");
        }

        text = text.Trim();
        return text.Length == 0 ? (required ? throw new FormatException($"'{name}' is required.") : null) : text;
    }

    private static long Long(JsonElement m, string name)
    {
        if (!m.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return -1;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number))
        {
            throw new FormatException($"'{name}' must be a number.");
        }

        return number < 0 ? -1 : (long)Math.Min(number, 9_007_199_254_740_991d);
    }

    private static bool Bool(JsonElement m, string name) =>
        m.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
