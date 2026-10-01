using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NovaGet.Core.Services;

/// <summary>A newer release than the one running.</summary>
public sealed record UpdateInfo(Version Version, string Tag, Uri PageUrl);

/// <summary>
/// Ground rule 3: the update check is the app's only own network call, and it can be switched off (Options → Advanced).
/// It reads the latest release of the project's GitHub repository and never downloads or installs anything itself.
/// </summary>
public static class UpdateChecker
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) => lastCheck is null || now - lastCheck.Value >= Interval || lastCheck > now;

    /// <summary>The release in a GitHub "latest release" reply, or null when it isn't usable (draft, pre-release, odd tag).</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                || (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                || !root.TryGetProperty("tag_name", out var tagElement) || tagElement.GetString() is not { } tag
                || !root.TryGetProperty("html_url", out var urlElement) || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var page)
                || page.Scheme != Uri.UriSchemeHttps
                || !Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var version))
            {
                return null;
            }

            return new UpdateInfo(Normalize(version), tag, page);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsNewer(UpdateInfo update, Version current)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(current);
        return update.Version > Normalize(current);
    }

    /// <summary>Asks the feed for the latest release; null when there is none. Throws <see cref="HttpRequestException"/> when unreachable.</summary>
    public static async Task<UpdateInfo?> FetchAsync(HttpClient client, Uri feed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.ProductName, AppInfo.InformationalVersion.Split('+')[0]));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // no release published yet
        }

        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return text.Length > 1024 * 1024 ? null : Parse(text);
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>"1.2.0" for messages.</summary>
    public static string Display(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Revision > 0
            ? version.ToString(4)
            : string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}");
    }
}
