using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace NovaGet.Core.Grabber;

/// <summary>What a link points to, judged by where it appears.</summary>
public enum LinkKind
{
    /// <summary>A page link (a, area, iframe, frame, meta refresh): explored when in scope.</summary>
    Page,

    /// <summary>An embedded file (img, video, audio, source, script, icons, CSS url()).</summary>
    Resource,

    /// <summary>A style sheet: a resource whose url()s are files too.</summary>
    Stylesheet,
}

public sealed record FoundLink(Uri Url, LinkKind Kind);

/// <summary>
/// Finds the links in HTML (respecting &lt;base&gt;, srcset and lazy-loading attributes) and CSS (url(), @import), and
/// rewrites them for offline browsing.
/// </summary>
public static partial class LinkScanner
{
    /// <summary>Element/attribute pairs that hold one address, and what kind of link it is.</summary>
    private static readonly (string Selector, string Attribute, LinkKind Kind)[] s_single =
    [
        ("a[href]", "href", LinkKind.Page),
        ("area[href]", "href", LinkKind.Page),
        ("iframe[src]", "src", LinkKind.Page),
        ("frame[src]", "src", LinkKind.Page),
        ("img[src]", "src", LinkKind.Resource),
        ("img[data-src]", "data-src", LinkKind.Resource),
        ("img[data-lazy-src]", "data-lazy-src", LinkKind.Resource),
        ("img[data-original]", "data-original", LinkKind.Resource),
        ("source[src]", "src", LinkKind.Resource),
        ("video[src]", "src", LinkKind.Resource),
        ("video[poster]", "poster", LinkKind.Resource),
        ("audio[src]", "src", LinkKind.Resource),
        ("track[src]", "src", LinkKind.Resource),
        ("embed[src]", "src", LinkKind.Resource),
        ("object[data]", "data", LinkKind.Resource),
        ("input[type=image][src]", "src", LinkKind.Resource),
        ("script[src]", "src", LinkKind.Resource),
        ("link[rel~=icon][href]", "href", LinkKind.Resource),
        ("link[rel~=apple-touch-icon][href]", "href", LinkKind.Resource),
        ("link[rel~=stylesheet][href]", "href", LinkKind.Stylesheet),
    ];

    private static readonly (string Selector, string Attribute)[] s_srcsets =
    [
        ("img[srcset]", "srcset"),
        ("img[data-srcset]", "data-srcset"),
        ("source[srcset]", "srcset"),
    ];

    public static IReadOnlyList<FoundLink> ScanHtml(string html, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);
        var document = new HtmlParser().ParseDocument(html);
        var baseUrl = BaseUrl(document, pageUrl);
        var found = new List<FoundLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? value, LinkKind kind)
        {
            if (Resolve(baseUrl, value) is { } url && seen.Add(kind + url.AbsoluteUri))
            {
                found.Add(new FoundLink(url, kind));
            }
        }

        foreach (var (selector, attribute, kind) in s_single)
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                Add(element.GetAttribute(attribute), kind);
            }
        }

        foreach (var (selector, attribute) in s_srcsets)
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                foreach (var candidate in SrcsetUrls(element.GetAttribute(attribute)))
                {
                    Add(candidate, LinkKind.Resource);
                }
            }
        }

        foreach (var meta in document.QuerySelectorAll("meta[http-equiv]"))
        {
            if (string.Equals(meta.GetAttribute("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase)
                && RefreshRegex().Match(meta.GetAttribute("content") ?? string.Empty) is { Success: true } refresh)
            {
                Add(refresh.Groups[1].Value.Trim().Trim('\'', '"'), LinkKind.Page);
            }
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            foreach (var url in CssUrls(element.GetAttribute("style")))
            {
                Add(url, LinkKind.Resource);
            }
        }

        foreach (var style in document.QuerySelectorAll("style"))
        {
            foreach (var url in CssUrls(style.TextContent))
            {
                Add(url, LinkKind.Resource);
            }
        }

        return found;
    }

    /// <summary>The files a style sheet refers to (url() and @import).</summary>
    public static IReadOnlyList<Uri> ScanCss(string css, Uri cssUrl)
    {
        ArgumentNullException.ThrowIfNull(css);
        ArgumentNullException.ThrowIfNull(cssUrl);
        return [.. CssUrls(css).Select(u => Resolve(cssUrl, u)).OfType<Uri>().DistinctBy(u => u.AbsoluteUri)];
    }

    /// <summary>The addresses in a srcset ("a.jpg 1x, b.jpg 2x" or "a.jpg 480w, …").</summary>
    public static IEnumerable<string> SrcsetUrls(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            yield break;
        }

        // Candidates are separated by commas followed by whitespace; an address may itself contain commas.
        foreach (var candidate in SrcsetSplitRegex().Split(srcset.Trim()))
        {
            var url = candidate.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd(',');
            if (!string.IsNullOrEmpty(url))
            {
                yield return url;
            }
        }
    }

    /// <summary>
    /// Rewrites a page for offline browsing: every link that <paramref name="localLinkFor"/> maps (to a relative path)
    /// is replaced, &lt;base&gt; is removed and the page is declared UTF-8 (it is written that way).
    /// </summary>
    public static string RewriteHtml(string html, Uri pageUrl, Func<Uri, string?> localLinkFor)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);
        ArgumentNullException.ThrowIfNull(localLinkFor);
        var document = new HtmlParser().ParseDocument(html);
        var baseUrl = BaseUrl(document, pageUrl);

        string? Map(string? value)
        {
            if (Resolve(baseUrl, value, keepFragment: true) is not { } url)
            {
                return null;
            }

            var local = localLinkFor(new Uri(url.GetLeftPart(UriPartial.Query)));
            return local is null ? null : local + (url.Fragment.Length > 1 ? url.Fragment : string.Empty);
        }

        foreach (var (selector, attribute, _) in s_single)
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                if (Map(element.GetAttribute(attribute)) is { } local)
                {
                    element.SetAttribute(attribute, local);
                }
            }
        }

        foreach (var (selector, attribute) in s_srcsets)
        {
            foreach (var element in document.QuerySelectorAll(selector))
            {
                var value = element.GetAttribute(attribute);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var parts = SrcsetSplitRegex().Split(value.Trim()).Select(candidate =>
                {
                    var pieces = candidate.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (pieces.Length == 0)
                    {
                        return candidate;
                    }

                    var local = Map(pieces[0].TrimEnd(','));
                    return local is null ? candidate.Trim() : pieces.Length > 1 ? local + " " + pieces[1] : local;
                });
                element.SetAttribute(attribute, string.Join(", ", parts));
            }
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            element.SetAttribute("style", RewriteCss(element.GetAttribute("style") ?? string.Empty, baseUrl, localLinkFor));
        }

        foreach (var style in document.QuerySelectorAll("style"))
        {
            style.TextContent = RewriteCss(style.TextContent, baseUrl, localLinkFor);
        }

        foreach (var element in document.QuerySelectorAll("base"))
        {
            element.Remove();
        }

        DeclareUtf8(document);
        return document.ToHtml();
    }

    private static string RewriteCss(string css, Uri baseUrl, Func<Uri, string?> localLinkFor) =>
        CssUrlRegex().Replace(css, m =>
        {
            var value = m.Groups["url"].Value;
            return Resolve(baseUrl, value) is { } url && localLinkFor(url) is { } local ? $"url(\"{local}\")" : m.Value;
        });

    private static void DeclareUtf8(IDocument document)
    {
        foreach (var meta in document.QuerySelectorAll("meta[charset]"))
        {
            meta.SetAttribute("charset", "utf-8");
        }

        foreach (var meta in document.QuerySelectorAll("meta[http-equiv]"))
        {
            if (string.Equals(meta.GetAttribute("http-equiv"), "content-type", StringComparison.OrdinalIgnoreCase))
            {
                meta.SetAttribute("content", "text/html; charset=utf-8");
            }
        }

        if (document.QuerySelector("meta[charset]") is null && document.Head is { } head)
        {
            var meta = document.CreateElement("meta");
            meta.SetAttribute("charset", "utf-8");
            head.Prepend(meta);
        }
    }

    private static IEnumerable<string> CssUrls(string? css)
    {
        if (string.IsNullOrEmpty(css))
        {
            yield break;
        }

        foreach (Match m in CssUrlRegex().Matches(css))
        {
            yield return m.Groups["url"].Value;
        }

        foreach (Match m in CssImportRegex().Matches(css))
        {
            yield return m.Groups[1].Value;
        }
    }

    private static Uri BaseUrl(IDocument document, Uri pageUrl)
    {
        var href = document.QuerySelector("base[href]")?.GetAttribute("href");
        return href is not null && Uri.TryCreate(pageUrl, href.Trim(), out var resolved) && resolved.Scheme is "http" or "https" ? resolved : pageUrl;
    }

    /// <summary>An absolute http/https/ftp/ftps address without its fragment, or null (javascript:, mailto:, data:, …).</summary>
    internal static Uri? Resolve(Uri baseUrl, string? value, bool keepFragment = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith('#') || text.Length > 8192)
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl, text, out var url) || url.Scheme is not ("http" or "https" or "ftp" or "ftps"))
        {
            return null;
        }

        return url.Fragment.Length > 0 && !keepFragment ? new Uri(url.GetLeftPart(UriPartial.Query)) : url;
    }

    [GeneratedRegex(@"url\(\s*(['""]?)(?<url>[^'""\)]+?)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex(@"@import\s+['""]([^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssImportRegex();

    [GeneratedRegex(@"^\s*\d*\s*;\s*url\s*=\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefreshRegex();

    [GeneratedRegex(@",\s+")]
    private static partial Regex SrcsetSplitRegex();
}

/// <summary>Text from a response body, in the charset its Content-Type names (else UTF-8 / what the bytes declare).</summary>
public static class PageText
{
    public static string Decode(byte[] body, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(body);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var charset = contentType?.Split(';').Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"', '\'', ' ');
        if (charset is not null)
        {
            try
            {
                return Encoding.GetEncoding(charset).GetString(body);
            }
            catch (ArgumentException)
            {
                // Unknown charset: fall through.
            }
        }

        // A BOM or <meta charset> decides; AngleSharp's detection handles both.
        using var stream = new MemoryStream(body);
        var document = new HtmlParser().ParseDocument(stream);
        if (!string.Equals(document.CharacterSet, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Encoding.GetEncoding(document.CharacterSet).GetString(body);
            }
            catch (ArgumentException)
            {
                // Fall back to UTF-8.
            }
        }

        return new UTF8Encoding(false).GetString(body).TrimStart('﻿');
    }
}
