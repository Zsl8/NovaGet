using System.Globalization;
using System.Text.RegularExpressions;

namespace NovaGet.Core.Engine.Streams;

/// <summary>HLS playlists (RFC 8216): master playlists list variants; media playlists list segments.</summary>
public static partial class HlsParser
{
    public static bool LooksLikeHls(string text) => text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    /// <summary>Variants and audio renditions of a master playlist (or a single variant for a media playlist).</summary>
    public static StreamInfo ParseMaster(string text, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsMaster(text))
        {
            var media = ParseMedia(text, baseUrl);
            return new StreamInfo
            {
                Kind = StreamKind.Hls,
                ManifestUrl = baseUrl,
                Variants = [new StreamVariant { Id = "0", PlaylistUrl = baseUrl }],
                Protection = media.Protection,
                IsLive = media.IsLive,
                Duration = media.Track.Duration,
            };
        }

        var variants = new List<StreamVariant>();
        var audio = new List<StreamAudio>();
        string? protection = null;
        Dictionary<string, string>? pendingInfo = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pendingInfo = Attributes(line[18..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = Attributes(line[13..]);
                if (a.GetValueOrDefault("TYPE") == "AUDIO")
                {
                    var group = a.GetValueOrDefault("GROUP-ID") ?? string.Empty;
                    var uri = a.TryGetValue("URI", out var u) ? Resolve(baseUrl, u) : null;
                    audio.Add(new StreamAudio($"{group}:{audio.Count}", group, a.GetValueOrDefault("NAME"), a.GetValueOrDefault("LANGUAGE"),
                        uri, 0, a.GetValueOrDefault("DEFAULT") == "YES"));
                }
            }
            else if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
            {
                protection ??= Protection(Attributes(line[19..]));
            }
            else if (pendingInfo is not null && line.Length > 0 && !line.StartsWith('#'))
            {
                var resolution = pendingInfo.GetValueOrDefault("RESOLUTION")?.Split('x');
                var codecs = pendingInfo.GetValueOrDefault("CODECS");
                variants.Add(new StreamVariant
                {
                    Id = variants.Count.ToString(CultureInfo.InvariantCulture),
                    PlaylistUrl = Resolve(baseUrl, line),
                    Bandwidth = long.TryParse(pendingInfo.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var bw) ? bw : 0,
                    Width = resolution is [var w, _] && int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ? width : 0,
                    Height = resolution is [_, var h] && int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var height) ? height : 0,
                    Codecs = codecs,
                    AudioId = pendingInfo.GetValueOrDefault("AUDIO"),
                    AudioOnly = resolution is null && codecs is not null && !codecs.Contains("avc", StringComparison.OrdinalIgnoreCase)
                        && !codecs.Contains("hvc", StringComparison.OrdinalIgnoreCase) && !codecs.Contains("hev", StringComparison.OrdinalIgnoreCase)
                        && !codecs.Contains("vp0", StringComparison.OrdinalIgnoreCase) && !codecs.Contains("av01", StringComparison.OrdinalIgnoreCase),
                });
                pendingInfo = null;
            }
        }

        return new StreamInfo { Kind = StreamKind.Hls, ManifestUrl = baseUrl, Variants = variants, Audio = audio, Protection = protection };
    }

    public sealed record MediaPlaylist(MediaTrack Track, string? Protection, bool IsLive);

    /// <summary>The segments of a media playlist, with their keys, byte ranges and the init segment (EXT-X-MAP).</summary>
    public static MediaPlaylist ParseMedia(string text, Uri baseUrl, TrackKind kind = TrackKind.Muxed)
    {
        ArgumentNullException.ThrowIfNull(text);
        var segments = new List<MediaSegment>();
        MediaSegment? init = null;
        SegmentKey? key = null;
        string? protection = null;
        long sequence = 0;
        double duration = 0;
        ByteRange? range = null;
        long nextOffset = 0;
        var ended = false;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                long.TryParse(line[22..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line[8..].Split(',')[0];
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                range = ParseRange(line[17..], nextOffset);
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var a = Attributes(line[11..]);
                var method = a.GetValueOrDefault("METHOD") ?? "NONE";
                if (Protection(a) is { } drm)
                {
                    protection ??= drm;
                    key = null;
                }
                else if (method == "AES-128" && a.TryGetValue("URI", out var keyUri) && Resolve(baseUrl, keyUri) is { } keyUrl)
                {
                    key = new SegmentKey(keyUrl, a.TryGetValue("IV", out var iv) ? ParseIv(iv) : null);
                }
                else
                {
                    key = null;
                }
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var a = Attributes(line[11..]);
                if (a.TryGetValue("URI", out var mapUri))
                {
                    init = new MediaSegment(Resolve(baseUrl, mapUri), a.TryGetValue("BYTERANGE", out var r) ? ParseRange(r, 0) : null, null, -1, 0);
                }
            }
            else if (line == "#EXT-X-ENDLIST")
            {
                ended = true;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                var url = Resolve(baseUrl, line);
                segments.Add(new MediaSegment(url, range, key, sequence, duration));
                nextOffset = range is null ? 0 : range.Offset + range.Length;
                range = null;
                duration = 0;
                sequence++;
            }
        }

        var container = init is not null || segments.FirstOrDefault()?.Url.AbsolutePath.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) == true ? "mp4" : "ts";
        return new MediaPlaylist(new MediaTrack(kind, init, segments, container), protection, !ended);
    }

    /// <summary>
    /// Ground rule 2: SAMPLE-AES (FairPlay-style sample encryption) and any key system other than plain AES-128 with
    /// an identity key are DRM. Returns a description, or null when the key is acceptable.
    /// </summary>
    internal static string? Protection(IReadOnlyDictionary<string, string> key)
    {
        var method = key.GetValueOrDefault("METHOD") ?? "NONE";
        var format = key.GetValueOrDefault("KEYFORMAT");
        if (method.StartsWith("SAMPLE-AES", StringComparison.Ordinal))
        {
            return method;
        }

        if (method == "AES-128" && format is not null && format != "identity")
        {
            return format;
        }

        if (method == "AES-128" && key.TryGetValue("URI", out var uri) && !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && absolute.Scheme is not ("http" or "https"))
        {
            return absolute.Scheme; // e.g. skd:// (FairPlay)
        }

        return null;
    }

    /// <summary><c>KEY=value,KEY="quoted, value"</c> attribute lists.</summary>
    internal static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in AttributeRegex().Matches(text))
        {
            var value = m.Groups[2].Value;
            result[m.Groups[1].Value] = value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }

        return result;
    }

    private static ByteRange ParseRange(string text, long defaultOffset)
    {
        var parts = text.Trim().Trim('"').Split('@');
        var length = long.Parse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture);
        var offset = parts.Length > 1 ? long.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture) : defaultOffset;
        return new ByteRange(offset, length);
    }

    private static byte[]? ParseIv(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        try
        {
            var bytes = Convert.FromHexString(hex.PadLeft(32, '0'));
            return bytes.Length == 16 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static Uri Resolve(Uri baseUrl, string reference) =>
        Uri.TryCreate(baseUrl, reference.Trim(), out var resolved) ? resolved : throw new FormatException($"Bad address in playlist: {reference}");

    private static string[] Lines(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

    [GeneratedRegex("""([A-Z0-9-]+)=("[^"]*"|[^,]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();
}
