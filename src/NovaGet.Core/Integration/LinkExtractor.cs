using System.Text.RegularExpressions;

namespace NovaGet.Core.Integration;

/// <summary>Finds download addresses in dropped or pasted text, Internet shortcuts (.url) and browser drag formats.</summary>
public static partial class LinkExtractor
{
    public const int MaxLinks = 20_000;

    /// <summary>Every http/https/ftp/ftps address in the text, in order, without duplicates.</summary>
    public static IReadOnlyList<Uri> ExtractUrls(string? text)
    {
        var result = new List<Uri>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in UrlRegex().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"', '>');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && BrowserMessageValidator.IsAllowedUrl(uri)
                && uri.AbsoluteUri.Length <= BrowserMessageValidator.MaxUrlLength && seen.Add(uri.AbsoluteUri))
            {
                result.Add(uri);
                if (result.Count >= MaxLinks)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>The single address in <paramref name="text"/> when the text is just that address (clipboard monitoring).</summary>
    public static Uri? SingleUrl(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > BrowserMessageValidator.MaxUrlLength || trimmed.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && BrowserMessageValidator.IsAllowedUrl(uri) ? uri : null;
    }

    /// <summary>The <c>URL=</c> line of an Internet shortcut file.</summary>
    public static Uri? ParseInternetShortcut(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                return SingleUrl(line[4..]);
            }
        }

        return null;
    }

    /// <summary>Firefox's <c>text/x-moz-url</c>: address and title on alternating lines.</summary>
    public static IReadOnlyList<Uri> ParseMozUrl(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return [];
        }

        var lines = data.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var result = new List<Uri>();
        for (var i = 0; i < lines.Length; i += 2)
        {
            if (SingleUrl(lines[i]) is { } uri)
            {
                result.Add(uri);
            }
        }

        return result;
    }

    [GeneratedRegex(@"(?:https?|ftps?)://[^\s""'<>\u0000-\u001f]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UrlRegex();
}
