using NovaGet.Core.Engine.Streams;

namespace NovaGet.Core.Tests.Streams;

public sealed class HlsParserTests
{
    private static readonly Uri Base = new("https://cdn.example.com/show/master.m3u8?token=abc");

    private const string Master = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,URI="audio/en.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="Arabic, dubbed",LANGUAGE="ar",URI="audio/ar.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2",AUDIO="aud"
        360p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="aud"
        https://other.example.com/1080p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS="mp4a.40.2"
        audio-only/index.m3u8
        """;

    [Fact]
    public void Master_playlist_lists_variants_and_audio()
    {
        var info = HlsParser.ParseMaster(Master, Base);

        Assert.Equal(StreamKind.Hls, info.Kind);
        Assert.Equal(3, info.Variants.Count);
        var hd = info.Variants[1];
        Assert.Equal((1920, 1080, 4_500_000L), (hd.Width, hd.Height, hd.Bandwidth));
        Assert.Equal("avc1.640028,mp4a.40.2", hd.Codecs);
        Assert.Equal("https://other.example.com/1080p/index.m3u8", hd.PlaylistUrl!.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/show/360p/index.m3u8", info.Variants[0].PlaylistUrl!.AbsoluteUri);
        Assert.Equal("aud", hd.AudioId);
        Assert.True(info.Variants[2].AudioOnly);
        Assert.Same(hd, info.Best);

        Assert.Equal(2, info.Audio.Count);
        Assert.Equal("Arabic, dubbed", info.Audio[1].Name);
        Assert.True(info.Audio[0].IsDefault);
        Assert.Equal("https://cdn.example.com/show/audio/en.m3u8", info.Audio[0].PlaylistUrl!.AbsoluteUri);
        Assert.False(info.IsProtected);
    }

    [Theory]
    [InlineData(1920, 1080, 4_500_000L, false, "MP4 1920x1080 · 4.5 Mbps")]
    [InlineData(1280, 720, 0L, false, "MP4 1280x720")]
    [InlineData(0, 0, 128_000L, true, "Audio · 128 kbps")]
    public void Variant_labels_read_like_the_panel(int width, int height, long bandwidth, bool audioOnly, string expected)
    {
        var variant = new StreamVariant { Id = "0", Width = width, Height = height, Bandwidth = bandwidth, AudioOnly = audioOnly };

        Assert.Equal(expected, variant.Label);
    }

    [Fact]
    public void Media_playlist_lists_segments_with_ranges_keys_and_init()
    {
        const string text = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-MAP:URI="init.mp4",BYTERANGE="720@0"
            #EXTINF:6.0,
            #EXT-X-BYTERANGE:1000@720
            video.m4s
            #EXTINF:5.5,
            #EXT-X-BYTERANGE:2000
            video.m4s
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/k1",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:4.0,
            seg3.m4s
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:2.5,
            seg4.m4s
            #EXT-X-ENDLIST
            """;

        var media = HlsParser.ParseMedia(text, new Uri("https://cdn.example.com/v/index.m3u8"));

        Assert.False(media.IsLive);
        Assert.Null(media.Protection);
        var track = media.Track;
        Assert.Equal("mp4", track.Container);
        Assert.Equal(new ByteRange(0, 720), track.Init!.Range);
        Assert.Equal(4, track.Segments.Count);
        Assert.Equal(18.0, track.Duration, 3);
        Assert.Equal([7L, 8, 9, 10], track.Segments.Select(s => s.Sequence));
        Assert.Equal(new ByteRange(720, 1000), track.Segments[0].Range);
        Assert.Equal(new ByteRange(1720, 2000), track.Segments[1].Range); // continues after the previous range
        Assert.Null(track.Segments[0].Key);
        var key = track.Segments[2].Key!;
        Assert.Equal("https://keys.example.com/k1", key.KeyUrl.AbsoluteUri);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), key.Iv!);
        Assert.Null(track.Segments[3].Key);
    }

    [Fact]
    public void Live_playlists_and_ts_segments_are_recognized()
    {
        var media = HlsParser.ParseMedia("#EXTM3U\n#EXTINF:2,\na.ts\n#EXTINF:2,\nb.ts\n", new Uri("http://example.com/live.m3u8"));

        Assert.True(media.IsLive);
        Assert.Equal("ts", media.Track.Container);
        Assert.Equal(TrackKind.Muxed, media.Track.Kind);
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key1\",KEYFORMAT=\"com.apple.streamingkeydelivery\"")]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES-CTR,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.com/k\",KEYFORMAT=\"com.microsoft.playready\"")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"skd://fairplay-key\"")]
    public void Drm_keys_mark_the_stream_protected(string keyLine)
    {
        var media = HlsParser.ParseMedia($"#EXTM3U\n{keyLine}\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n", new Uri("https://example.com/v.m3u8"));

        Assert.NotNull(media.Protection);
        Assert.Null(media.Track.Segments[0].Key); // never handed to the downloader
    }

    [Fact]
    public void Plain_aes128_with_an_identity_key_is_allowed()
    {
        var media = HlsParser.ParseMedia(
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",KEYFORMAT=\"identity\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n",
            new Uri("https://example.com/v.m3u8"));

        Assert.Null(media.Protection);
        Assert.Equal("https://example.com/key.bin", media.Track.Segments[0].Key!.KeyUrl.AbsoluteUri);
        Assert.Null(media.Track.Segments[0].Key!.Iv);
    }

    [Fact]
    public void A_session_key_in_the_master_playlist_marks_it_protected()
    {
        var text = Master + "\n#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n";

        Assert.True(HlsParser.ParseMaster(text, Base).IsProtected);
    }

    [Fact]
    public void A_media_playlist_given_as_master_becomes_one_variant()
    {
        var url = new Uri("https://example.com/v.m3u8");
        var info = HlsParser.ParseMaster("#EXTM3U\n#EXTINF:4,\na.ts\n#EXTINF:3,\nb.ts\n#EXT-X-ENDLIST\n", url);

        var variant = Assert.Single(info.Variants);
        Assert.Equal(url, variant.PlaylistUrl);
        Assert.Equal(7, info.Duration);
        Assert.False(info.IsLive);
    }

    [Fact]
    public void Selection_round_trips_as_json()
    {
        var selection = new StreamSelection { Kind = StreamKind.Dash, ManifestUrl = "https://example.com/a.mpd", VariantId = "v2", AudioId = "a1" };

        var json = selection.ToJson();

        Assert.Contains("\"kind\":\"dash\"", json, StringComparison.Ordinal);
        Assert.Equal(selection, StreamSelection.FromJson(json));
        Assert.Null(StreamSelection.FromJson("{not json"));
        Assert.Null(StreamSelection.FromJson(null));
    }
}
