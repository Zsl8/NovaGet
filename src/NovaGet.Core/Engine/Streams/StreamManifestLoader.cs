using System.Buffers;

namespace NovaGet.Core.Engine.Streams;

/// <summary>Looks at an address that may be an HLS or DASH manifest (Add URL, browser media).</summary>
public interface IStreamProber
{
    /// <summary>What the manifest offers, or null when the address is not a stream manifest.</summary>
    Task<StreamInfo?> ProbeAsync(RequestContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches and reads stream manifests: <see cref="ProbeAsync"/> for the quality list, <see cref="ResolveAsync"/> for the
/// segments of the chosen quality. DRM is reported as <see cref="DownloadErrorKind.ProtectedContent"/>, never decrypted.
/// </summary>
public sealed class StreamManifestLoader(IEnumerable<ITransferProtocol> protocols) : IStreamProber
{
    /// <summary>Manifests larger than this are refused (a 2-hour HLS playlist of 2 s segments is about 300 KB).</summary>
    public const int MaxManifestBytes = 16 * 1024 * 1024;

    private static readonly string[] s_manifestTypes =
    [
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl", "application/dash+xml",
    ];

    private readonly IReadOnlyList<ITransferProtocol> _protocols = [.. protocols];

    public static bool IsManifestType(string? contentType) =>
        contentType is not null && s_manifestTypes.Any(t => contentType.StartsWith(t, StringComparison.OrdinalIgnoreCase));

    public static bool IsManifestAddress(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var path = url.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<StreamInfo?> ProbeAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (text, url) = await FetchTextAsync(context, context.Url, cancellationToken).ConfigureAwait(false);
        try
        {
            if (HlsParser.LooksLikeHls(text))
            {
                var info = HlsParser.ParseMaster(text, url);
                if (!HlsParser.IsMaster(text) || info.IsProtected || info.Best?.PlaylistUrl is not { } playlist)
                {
                    return info;
                }

                // Keys are only listed in the media playlists: look at the best one for DRM and the duration.
                var (mediaText, mediaUrl) = await FetchTextAsync(context, playlist, cancellationToken).ConfigureAwait(false);
                var media = HlsParser.ParseMedia(mediaText, mediaUrl);
                return info with { Protection = media.Protection, IsLive = media.IsLive, Duration = media.Track.Duration };
            }

            return DashManifest.LooksLikeDash(text) ? DashManifest.Parse(text, url).Info : null;
        }
        catch (FormatException ex)
        {
            throw Unreadable(ex);
        }
    }

    /// <summary>The tracks of the chosen quality (video, plus separate audio when the stream has it).</summary>
    public async Task<IReadOnlyList<MediaTrack>> ResolveAsync(StreamSelection selection, RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(context);
        var manifestUrl = new Uri(selection.ManifestUrl);
        var (text, url) = await FetchTextAsync(context, manifestUrl, cancellationToken).ConfigureAwait(false);
        try
        {
            if (DashManifest.LooksLikeDash(text))
            {
                var dash = DashManifest.Parse(text, url);
                ThrowIfProtected(dash.Info.Protection);
                return dash.Tracks(selection.VariantId, selection.AudioId);
            }

            if (!HlsParser.LooksLikeHls(text))
            {
                throw new DownloadException(DownloadErrorKind.Unknown, "The address no longer points to a stream playlist.");
            }

            if (!HlsParser.IsMaster(text))
            {
                var single = HlsParser.ParseMedia(text, url);
                ThrowIfProtected(single.Protection);
                return [single.Track];
            }

            var info = HlsParser.ParseMaster(text, url);
            ThrowIfProtected(info.Protection);
            var variant = info.Variants.FirstOrDefault(v => v.Id == selection.VariantId) ?? info.Best
                ?? throw new DownloadException(DownloadErrorKind.Unknown, "The stream playlist lists no qualities.");
            var audio = AudioFor(info, variant, selection.AudioId);
            var tracks = new List<MediaTrack>();
            var (videoText, videoUrl) = await FetchTextAsync(context, variant.PlaylistUrl!, cancellationToken).ConfigureAwait(false);
            var video = HlsParser.ParseMedia(videoText, videoUrl, audio is null ? TrackKind.Muxed : TrackKind.Video);
            ThrowIfProtected(video.Protection);
            tracks.Add(video.Track);
            if (audio?.PlaylistUrl is { } audioPlaylist)
            {
                var (audioText, audioUrl) = await FetchTextAsync(context, audioPlaylist, cancellationToken).ConfigureAwait(false);
                var audioMedia = HlsParser.ParseMedia(audioText, audioUrl, TrackKind.Audio);
                ThrowIfProtected(audioMedia.Protection);
                tracks.Add(audioMedia.Track);
            }

            return tracks;
        }
        catch (FormatException ex)
        {
            throw Unreadable(ex);
        }
    }

    /// <summary>The separate audio rendition for an HLS variant (null when its audio is in the video segments).</summary>
    private static StreamAudio? AudioFor(StreamInfo info, StreamVariant variant, string? audioId)
    {
        if (variant.AudioId is not { } group)
        {
            return null;
        }

        var renditions = info.Audio.Where(a => a.GroupId == group).ToList();
        var chosen = renditions.FirstOrDefault(a => a.Id == audioId) ?? renditions.FirstOrDefault(a => a.IsDefault) ?? renditions.FirstOrDefault();
        return chosen?.PlaylistUrl is null ? null : chosen;
    }

    private static void ThrowIfProtected(string? protection)
    {
        if (protection is not null)
        {
            throw new DownloadException(DownloadErrorKind.ProtectedContent, StreamSelection.ProtectedMessage);
        }
    }

    private static DownloadException Unreadable(FormatException ex) =>
        new(DownloadErrorKind.Unknown, "The stream playlist could not be read: " + ex.Message, inner: ex);

    /// <summary>Fetches a manifest (address after redirects, for resolving relative entries).</summary>
    private async Task<(string Text, Uri Url)> FetchTextAsync(RequestContext context, Uri url, CancellationToken cancellationToken)
    {
        var protocol = _protocols.FirstOrDefault(p => p.CanHandle(url))
            ?? throw new DownloadException(DownloadErrorKind.InvalidAddress, $"Unsupported address in the stream: {url.Scheme}:");
        var request = new TransferRequest(StreamRequests.For(context, url), 0, -1, null, UseRange: false);
        var response = await protocol.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            if (response.TotalSize > MaxManifestBytes)
            {
                throw new DownloadException(DownloadErrorKind.Unknown, "The stream playlist is too large.");
            }

            using var buffer = new MemoryStream();
            var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = await response.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxManifestBytes)
                    {
                        throw new DownloadException(DownloadErrorKind.Unknown, "The stream playlist is too large.");
                    }

                    buffer.Write(chunk, 0, read);
                }
            }
            catch (IOException ex)
            {
                throw new DownloadException(DownloadErrorKind.Network, "The connection was interrupted.", inner: ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            return (ManifestText.Decode(buffer.ToArray()), response.FinalUri ?? url);
        }
    }
}

/// <summary>Requests for the parts of a stream (playlists, keys, segments).</summary>
internal static class StreamRequests
{
    /// <summary>
    /// The download's request for another address of the same stream. Cookies and the login go only to the host they
    /// came with; segments on a CDN get the referrer and user agent, like the browser's player would send.
    /// </summary>
    public static RequestContext For(RequestContext context, Uri url)
    {
        var sameHost = string.Equals(url.Host, context.Url.Host, StringComparison.OrdinalIgnoreCase);
        return context with
        {
            Url = url,
            Cookies = sameHost ? context.Cookies : null,
            UserName = sameHost ? context.UserName : null,
            Password = sameHost ? context.Password : null,
        };
    }
}
