using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaGet.Core.Engine.Streams;

public enum StreamKind
{
    Hls,
    Dash,
}

public enum TrackKind
{
    /// <summary>Video with its audio in the same segments (most HLS).</summary>
    Muxed,
    Video,
    Audio,
}

/// <summary>A byte range within a resource (HLS EXT-X-BYTERANGE, DASH SegmentBase/indexRange).</summary>
public sealed record ByteRange(long Offset, long Length)
{
    public long End => Offset + Length - 1;
}

/// <summary>AES-128 (CBC, PKCS#7) as allowed by section 4.8; the IV is explicit or the segment's sequence number.</summary>
public sealed record SegmentKey(Uri KeyUrl, byte[]? Iv);

public sealed record MediaSegment(Uri Url, ByteRange? Range, SegmentKey? Key, long Sequence, double Duration);

/// <summary>One downloadable track: an optional initialization segment followed by media segments.</summary>
public sealed record MediaTrack(TrackKind Kind, MediaSegment? Init, IReadOnlyList<MediaSegment> Segments, string Container)
{
    public double Duration => Segments.Sum(s => s.Duration);
}

/// <summary>A quality the user can choose (an HLS variant or a DASH video representation).</summary>
public sealed record StreamVariant
{
    public required string Id { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public long Bandwidth { get; init; }

    public string? Codecs { get; init; }

    /// <summary>HLS: the variant's media playlist. DASH: unused (segments come from the MPD).</summary>
    public Uri? PlaylistUrl { get; init; }

    /// <summary>HLS AUDIO group / DASH audio representation id paired with this variant, if any.</summary>
    public string? AudioId { get; init; }

    public bool AudioOnly { get; init; }

    /// <summary>"MP4 1920x1080 · 4.5 Mbps", "Audio · 128 kbps".</summary>
    public string Label
    {
        get
        {
            var rate = Bandwidth >= 1_000_000 ? $"{Bandwidth / 1_000_000.0:0.#} Mbps" : Bandwidth > 0 ? $"{Bandwidth / 1000} kbps" : null;
            if (AudioOnly)
            {
                return rate is null ? "Audio" : $"Audio · {rate}";
            }

            var size = Width > 0 && Height > 0 ? $"MP4 {Width}x{Height}" : "MP4";
            return rate is null ? size : $"{size} · {rate}";
        }
    }
}

/// <summary>An audio rendition (HLS EXT-X-MEDIA TYPE=AUDIO / DASH audio representation).</summary>
public sealed record StreamAudio(string Id, string GroupId, string? Name, string? Language, Uri? PlaylistUrl, long Bandwidth, bool IsDefault);

/// <summary>What a manifest offers.</summary>
public sealed record StreamInfo
{
    public required StreamKind Kind { get; init; }

    public required Uri ManifestUrl { get; init; }

    public IReadOnlyList<StreamVariant> Variants { get; init; } = [];

    public IReadOnlyList<StreamAudio> Audio { get; init; } = [];

    /// <summary>DRM or SAMPLE-AES: never downloaded (ground rule 2).</summary>
    public string? Protection { get; init; }

    public bool IsProtected => Protection is not null;

    /// <summary>A live stream (no end): only what's listed now can be downloaded.</summary>
    public bool IsLive { get; init; }

    public double Duration { get; init; }

    /// <summary>The best quality first.</summary>
    public StreamVariant? Best => Variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).FirstOrDefault();
}

/// <summary>What a stream download remembers (Download.StreamManifestJson): the manifest and the chosen quality.</summary>
public sealed record StreamSelection
{
    public const string ProtectedMessage = "This stream is protected and cannot be downloaded.";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public required StreamKind Kind { get; init; }

    public required string ManifestUrl { get; init; }

    public string? VariantId { get; init; }

    public string? AudioId { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static StreamSelection? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StreamSelection>(json, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
