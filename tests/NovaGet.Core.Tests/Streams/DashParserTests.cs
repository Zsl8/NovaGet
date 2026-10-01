using NovaGet.Core.Engine.Streams;

namespace NovaGet.Core.Tests.Streams;

public sealed class DashParserTests
{
    private static readonly Uri ManifestUrl = new("https://cdn.example.com/movie/manifest.mpd");

    private const string NumberTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT0H0M20.0S" minBufferTime="PT2S">
          <BaseURL>media/</BaseURL>
          <Period id="p0">
            <AdaptationSet contentType="video" mimeType="video/mp4" segmentAlignment="true">
              <SegmentTemplate timescale="1000" duration="4000" startNumber="1" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/seg-$Number%05d$.m4s"/>
              <Representation id="v720" bandwidth="2500000" width="1280" height="720" codecs="avc1.64001f"/>
              <Representation id="v1080" bandwidth="5000000" width="1920" height="1080" codecs="avc1.640028"/>
            </AdaptationSet>
            <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
              <SegmentTemplate timescale="48000" initialization="a/$Bandwidth$/init.mp4" media="a/$Bandwidth$/$Time$.m4s">
                <SegmentTimeline>
                  <S t="0" d="192000" r="3"/>
                  <S d="96000"/>
                </SegmentTimeline>
              </SegmentTemplate>
              <Representation id="a128" bandwidth="128000" codecs="mp4a.40.2"/>
              <Representation id="a64" bandwidth="64000" codecs="mp4a.40.5"/>
            </AdaptationSet>
            <AdaptationSet contentType="text" mimeType="text/vtt">
              <Representation id="sub" bandwidth="1000"><BaseURL>subs.vtt</BaseURL></Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void Lists_video_qualities_with_the_best_audio()
    {
        var dash = DashManifest.Parse(NumberTemplate, ManifestUrl);
        var info = dash.Info;

        Assert.Equal(StreamKind.Dash, info.Kind);
        Assert.False(info.IsProtected);
        Assert.False(info.IsLive);
        Assert.Equal(20, info.Duration);
        Assert.Equal(["v720", "v1080"], info.Variants.Select(v => v.Id));
        Assert.Equal(5_128_000, info.Variants[1].Bandwidth);
        Assert.Equal("a128", info.Variants[1].AudioId);
        Assert.Equal("v1080", info.Best!.Id);
        Assert.Equal(["a128", "a64"], info.Audio.Select(a => a.Id));
    }

    [Fact]
    public void Number_templates_cover_the_period()
    {
        var tracks = DashManifest.Parse(NumberTemplate, ManifestUrl).Tracks("v720", null);

        Assert.Equal(2, tracks.Count);
        var video = tracks[0];
        Assert.Equal(TrackKind.Video, video.Kind);
        Assert.Equal("https://cdn.example.com/movie/media/v720/init.mp4", video.Init!.Url.AbsoluteUri);
        Assert.Equal(5, video.Segments.Count);
        Assert.Equal("https://cdn.example.com/movie/media/v720/seg-00001.m4s", video.Segments[0].Url.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/movie/media/v720/seg-00005.m4s", video.Segments[^1].Url.AbsoluteUri);
        Assert.Equal(20, video.Duration, 3);
    }

    [Fact]
    public void Timelines_expand_repeats_and_times()
    {
        var audio = DashManifest.Parse(NumberTemplate, ManifestUrl).Tracks(null, "a128")[1];

        Assert.Equal(TrackKind.Audio, audio.Kind);
        Assert.Equal("https://cdn.example.com/movie/media/a/128000/init.mp4", audio.Init!.Url.AbsoluteUri);
        Assert.Equal(
            ["0", "192000", "384000", "576000", "768000"],
            audio.Segments.Select(s => s.Url.Segments[^1].Replace(".m4s", string.Empty, StringComparison.Ordinal)));
        Assert.Equal(18, audio.Duration, 3);
    }

    [Fact]
    public void Without_a_choice_the_best_video_is_taken()
    {
        var tracks = DashManifest.Parse(NumberTemplate, ManifestUrl).Tracks(null, null);

        Assert.Contains("/v1080/", tracks[0].Segments[0].Url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_lists_and_single_files_are_supported()
    {
        const string xml = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">
              <Period>
                <AdaptationSet mimeType="video/mp4">
                  <Representation id="v" bandwidth="1000000" width="640" height="360">
                    <BaseURL>https://files.example.com/v/</BaseURL>
                    <SegmentList timescale="10" duration="50">
                      <Initialization sourceURL="init.mp4"/>
                      <SegmentURL media="s1.m4s"/>
                      <SegmentURL media="all.m4s" mediaRange="100-199"/>
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType="audio/mp4">
                  <Representation id="a" bandwidth="96000">
                    <BaseURL>audio.m4a</BaseURL>
                    <SegmentBase indexRange="0-800"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var tracks = DashManifest.Parse(xml, ManifestUrl).Tracks("v", "a");

        var video = tracks[0];
        Assert.Equal("https://files.example.com/v/init.mp4", video.Init!.Url.AbsoluteUri);
        Assert.Equal(2, video.Segments.Count);
        Assert.Equal(new ByteRange(100, 100), video.Segments[1].Range);
        Assert.Equal(5, video.Segments[0].Duration);
        var audio = Assert.Single(tracks[1].Segments);
        Assert.Equal("https://cdn.example.com/movie/audio.m4a", audio.Url.AbsoluteUri);
        Assert.Null(audio.Range);
    }

    [Theory]
    [InlineData("urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", "Widevine")]
    [InlineData("urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95", "PlayReady")]
    [InlineData("urn:mpeg:dash:mp4protection:2011", "CENC")]
    [InlineData("urn:uuid:00000000-1111-2222-3333-444444444444", "urn:uuid:00000000-1111-2222-3333-444444444444")]
    public void Any_content_protection_is_drm(string scheme, string expected)
    {
        var xml = NumberTemplate.Replace(
            "<AdaptationSet contentType=\"video\" mimeType=\"video/mp4\" segmentAlignment=\"true\">",
            $"<AdaptationSet contentType=\"video\" mimeType=\"video/mp4\"><ContentProtection schemeIdUri=\"{scheme}\"/>",
            StringComparison.Ordinal);

        var info = DashManifest.Parse(xml, ManifestUrl).Info;

        Assert.True(info.IsProtected);
        Assert.Equal(expected, info.Protection);
    }

    [Fact]
    public void Named_drm_wins_over_the_generic_cenc_marker()
    {
        var xml = NumberTemplate.Replace(
            "<Representation id=\"v720\" bandwidth=\"2500000\" width=\"1280\" height=\"720\" codecs=\"avc1.64001f\"/>",
            """
            <Representation id="v720" bandwidth="2500000" width="1280" height="720">
              <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc"/>
              <ContentProtection schemeIdUri="urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95"/>
            </Representation>
            """,
            StringComparison.Ordinal);

        Assert.Equal("PlayReady", DashManifest.Parse(xml, ManifestUrl).Info.Protection);
    }

    [Theory]
    [InlineData("PT1H2M3.5S", 3723.5)]
    [InlineData("PT20S", 20)]
    [InlineData("P1DT1S", 86401)]
    [InlineData("PT0.5S", 0.5)]
    public void Iso_durations(string text, double seconds) => Assert.Equal(seconds, DashManifest.Duration(text));

    [Fact]
    public void Template_identifiers_are_filled()
    {
        Assert.Equal("v1/0042-$-9000-128000.m4s", DashManifest.Fill("$RepresentationID$/$Number%04d$-$$-$Time$-$Bandwidth$.m4s", "v1", 42, 9000, 128000));
    }

    [Fact]
    public void Hostile_xml_is_refused()
    {
        const string xml = """<?xml version="1.0"?><!DOCTYPE MPD [<!ENTITY x SYSTEM "file:///etc/passwd">]><MPD xmlns="urn:mpeg:dash:schema:mpd:2011">&x;</MPD>""";

        Assert.Throws<FormatException>(() => DashManifest.Parse(xml, ManifestUrl));
    }

    [Fact]
    public void Recognizes_dash_text()
    {
        Assert.True(DashManifest.LooksLikeDash(NumberTemplate));
        Assert.False(DashManifest.LooksLikeDash("#EXTM3U"));
    }
}
