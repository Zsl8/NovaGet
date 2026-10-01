using System.Security.Cryptography;
using System.Text;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Streams;
using NovaGet.Core.Models;
using NovaGet.Core.Tests.Engine;
using NovaGet.TestServer;

namespace NovaGet.Core.Tests.Streams;

public sealed class StreamDownloadTests
{
    private const string Hls = "application/vnd.apple.mpegurl";

    /// <summary>Serves <paramref name="count"/> generated segments and a media playlist listing them.</summary>
    private static (TestFile Playlist, TestFile[] Segments) ServeHls(TestHttpServer server, string folder, int count, int size = 40_000, int seed = 10, string extra = "")
    {
        var segments = Enumerable.Range(0, count).Select(i => server.AddFile($"{folder}/seg{i}.ts", size + i, seed + i)).ToArray();
        var text = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n").Append(extra);
        foreach (var segment in segments)
        {
            text.Append("#EXTINF:2.0,\n").Append(segment.Path.Split('/')[^1]).Append('\n');
        }

        text.Append("#EXT-X-ENDLIST\n");
        return (server.AddText($"{folder}/index.m3u8", text.ToString(), Hls), segments);
    }

    private static byte[] Concat(IEnumerable<TestFile> files) => [.. files.SelectMany(f => f.Content())];

    private static long AddStream(EngineHarness harness, Uri manifest, string? variantId = null, string? audioId = null, string fileName = "Show.mp4", StreamKind kind = StreamKind.Hls) =>
        harness.Add(manifest, fileName, d =>
        {
            d.IsStream = true;
            d.StreamManifestJson = new StreamSelection { Kind = kind, ManifestUrl = manifest.AbsoluteUri, VariantId = variantId, AudioId = audioId }.ToJson();
        });

    [Fact]
    public async Task Hls_segments_are_joined_in_order()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { MaxConnections = 4 });
        var (playlist, segments) = ServeHls(harness.Server, "show", 12);
        var id = AddStream(harness, harness.Server.UrlFor(playlist));

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        var download = harness.Get(id);
        Assert.Equal("Show.ts", download.FileName); // no muxer: the joined transport stream as it is
        var path = Path.Combine(harness.SaveDirectory, download.FileName);
        Assert.Equal(Concat(segments), await File.ReadAllBytesAsync(path));
        Assert.Equal(new FileInfo(path).Length, download.Size);
        Assert.False(Directory.Exists(Path.Combine(harness.Options.TempDirectory, id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task Segments_download_in_parallel()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { MaxConnections = 4 });
        var (playlist, segments) = ServeHls(harness.Server, "par", 16, size: 60_000);
        foreach (var segment in segments)
        {
            segment.BytesPerSecond = 200_000;
        }

        var (result, peak) = await harness.RunTrackingConnectionsAsync(AddStream(harness, harness.Server.UrlFor(playlist)));

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.InRange(peak, 2, 4);
    }

    [Fact]
    public async Task The_chosen_variant_is_downloaded()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var server = harness.Server;
        var (low, lowSegments) = ServeHls(server, "low", 3, seed: 100);
        var (high, highSegments) = ServeHls(server, "high", 3, seed: 200);
        var master = server.AddText("master.m3u8", $"""
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=640x360
            {low.Path.Split('/')[0]}/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080
            {high.Path.Split('/')[0]}/index.m3u8
            """, Hls);

        var info = await new StreamManifestLoader([harness.Http]).ProbeAsync(new RequestContext { Url = server.UrlFor(master) }, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(2, info.Variants.Count);
        Assert.Equal(6, info.Duration); // read from the best variant's playlist

        var id = AddStream(harness, server.UrlFor(master), variantId: "0");
        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(Concat(lowSegments), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, harness.Get(id).FileName)));
        Assert.All(highSegments, s => Assert.Empty(server.RequestsFor(s)));
    }

    [Fact]
    public async Task Aes128_segments_are_decrypted()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var server = harness.Server;
        var key = RandomNumberGenerator.GetBytes(16);
        var explicitIv = RandomNumberGenerator.GetBytes(16);
        var plain = Enumerable.Range(0, 4).Select(i => RandomNumberGenerator.GetBytes(30_000 + (i * 7))).ToArray();
        server.AddContent("enc/key.bin", key);
        var playlist = new StringBuilder("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:5\n");
        for (var i = 0; i < plain.Length; i++)
        {
            // Segments 0-1 use the playlist's IV, 2-3 the media sequence number (RFC 8216 5.2).
            byte[] iv;
            if (i == 0)
            {
                playlist.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x{Convert.ToHexString(explicitIv)}\n");
            }

            if (i < 2)
            {
                iv = explicitIv;
            }
            else
            {
                if (i == 2)
                {
                    playlist.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n");
                }

                iv = StreamJob.SequenceIv(5 + i);
            }

            using var aes = Aes.Create();
            aes.Key = key;
            server.AddContent($"enc/{i}.ts", aes.EncryptCbc(plain[i], iv));
            playlist.Append($"#EXTINF:2,\n{i}.ts\n");
        }

        playlist.Append("#EXT-X-ENDLIST\n");
        var manifest = server.AddText("enc/index.m3u8", playlist.ToString(), Hls);
        var id = AddStream(harness, server.UrlFor(manifest));

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(plain.SelectMany(p => p).ToArray(), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, harness.Get(id).FileName)));
        Assert.Single(server.Requests, r => r.Path == "/enc/key.bin"); // fetched once
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://license.example.com/k\",KEYFORMAT=\"com.microsoft.playready\"\n")]
    public async Task Drm_streams_are_refused(string key)
    {
        await using var harness = await EngineHarness.CreateAsync();
        var (playlist, segments) = ServeHls(harness.Server, "drm", 3, extra: key);
        var id = AddStream(harness, harness.Server.UrlFor(playlist));

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.ProtectedContent, result.ErrorKind);
        Assert.Equal("This stream is protected and cannot be downloaded.", result.Message);
        Assert.Equal(StreamSelection.ProtectedMessage, harness.Get(id).LastError);
        Assert.All(segments, s => Assert.Empty(harness.Server.RequestsFor(s)));
    }

    [Fact]
    public async Task Protected_dash_is_refused_by_the_probe_and_the_engine()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var mpd = harness.Server.AddText("drm.mpd", """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S">
              <Period><AdaptationSet mimeType="video/mp4">
                <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
                <Representation id="v" bandwidth="1"><BaseURL>v.mp4</BaseURL></Representation>
              </AdaptationSet></Period>
            </MPD>
            """, "application/dash+xml");
        var url = harness.Server.UrlFor(mpd);

        var info = await new StreamManifestLoader([harness.Http]).ProbeAsync(new RequestContext { Url = url }, CancellationToken.None);
        var result = await harness.RunAsync(AddStream(harness, url, kind: StreamKind.Dash));

        Assert.True(info!.IsProtected);
        Assert.Equal("Widevine", info.Protection);
        Assert.Equal(DownloadErrorKind.ProtectedContent, result.ErrorKind);
    }

    [Fact]
    public async Task A_dropped_segment_is_retried_on_its_own()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { MaxConnections = 3 });
        var (playlist, segments) = ServeHls(harness.Server, "drop", 6, size: 80_000);
        segments[2].DropAfterBytes = (1_000, 20_000);
        segments[2].DropLimit = 2;
        segments[4].ForceStatus = 503;
        var id = AddStream(harness, harness.Server.UrlFor(playlist));
        _ = Task.Run(async () =>
        {
            await EngineHarness.WaitUntilAsync(() => harness.Server.RequestsFor(segments[4]).Count >= 2);
            segments[4].ForceStatus = null;
        });

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(Concat(segments), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, harness.Get(id).FileName)));
        Assert.Single(harness.Server.RequestsFor(segments[0]));
        Assert.True(harness.Server.RequestsFor(segments[2]).Count >= 2);
    }

    [Fact]
    public async Task A_missing_segment_stops_with_an_error()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var (playlist, segments) = ServeHls(harness.Server, "gone", 4);
        segments[3].ForceStatus = 404;

        var result = await harness.RunAsync(AddStream(harness, harness.Server.UrlFor(playlist)));

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.LinkExpired, result.ErrorKind);
    }

    [Fact]
    public async Task Paused_streams_resume_without_fetching_finished_segments_again()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { MaxConnections = 2 });
        var (playlist, segments) = ServeHls(harness.Server, "resume", 10, size: 50_000);
        foreach (var segment in segments.Skip(3))
        {
            segment.BytesPerSecond = 100_000;
        }

        var id = AddStream(harness, harness.Server.UrlFor(playlist));
        var stopped = harness.WaitForEndAsync(id);
        Assert.True(harness.Engine.Start(id));
        await EngineHarness.WaitUntilAsync(() => harness.Server.RequestsFor(segments[4]).Count > 0);
        await harness.Engine.PauseAsync(id);
        Assert.Equal(DownloadStatus.Paused, (await stopped).Status);
        Assert.True(harness.Get(id).Downloaded > 0);

        foreach (var segment in segments)
        {
            segment.BytesPerSecond = 0;
        }

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(Concat(segments), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, harness.Get(id).FileName)));
        Assert.All(segments.Take(3), s => Assert.Single(harness.Server.RequestsFor(s)));
    }

    [Fact]
    public async Task Crashed_streams_resume_after_a_restart()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { MaxConnections = 1 });
        var (playlist, segments) = ServeHls(harness.Server, "crash", 6, size: 50_000);
        segments[3].BytesPerSecond = 50_000;
        var id = AddStream(harness, harness.Server.UrlFor(playlist));
        Assert.True(harness.Engine.Start(id));
        await EngineHarness.WaitUntilAsync(() => harness.Server.RequestsFor(segments[3]).Count > 0);
        await harness.Engine.SimulateCrashAsync(id);
        harness.RestartEngine();
        harness.Engine.RecoverInterrupted();
        segments[3].BytesPerSecond = 0;

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(Concat(segments), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, harness.Get(id).FileName)));
        Assert.Single(harness.Server.RequestsFor(segments[0]));
    }

    [Fact]
    public async Task Byte_range_segments_are_cut_from_one_file()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { MaxConnections = 3 });
        var file = harness.Server.AddFile("ranged/all.ts", 90_000, seed: 7);
        var manifest = harness.Server.AddText("ranged/index.m3u8", """
            #EXTM3U
            #EXTINF:2,
            #EXT-X-BYTERANGE:30000@0
            all.ts
            #EXTINF:2,
            #EXT-X-BYTERANGE:30000
            all.ts
            #EXTINF:2,
            #EXT-X-BYTERANGE:30000
            all.ts
            #EXT-X-ENDLIST
            """, Hls);
        var id = AddStream(harness, harness.Server.UrlFor(manifest));

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Content(), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, harness.Get(id).FileName)));
        Assert.Equal(3, harness.Server.RequestsFor(file).Count(r => r.Range is not null));
    }

    [Fact]
    public async Task Byte_ranges_work_when_the_server_ignores_ranges()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var file = harness.Server.AddFile("noranges/all.ts", 60_000, seed: 8);
        file.SupportsRanges = false;
        var manifest = harness.Server.AddText("noranges/index.m3u8",
            "#EXTM3U\n#EXTINF:2,\n#EXT-X-BYTERANGE:20000@0\nall.ts\n#EXTINF:2,\n#EXT-X-BYTERANGE:40000@20000\nall.ts\n#EXT-X-ENDLIST\n", Hls);
        var id = AddStream(harness, harness.Server.UrlFor(manifest));

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Content(), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, harness.Get(id).FileName)));
    }

    [Fact]
    public async Task Dash_video_and_audio_are_muxed()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var muxer = new RecordingMuxer();
        harness.Muxer = muxer;
        harness.RestartEngine();
        var server = harness.Server;
        var videoInit = server.AddFile("dash/v/init.mp4", 900, 1);
        var video = Enumerable.Range(1, 3).Select(n => server.AddFile($"dash/v/{n}.m4s", 20_000, 10 + n)).ToArray();
        var audioInit = server.AddFile("dash/a/init.mp4", 500, 2);
        var audio = Enumerable.Range(1, 3).Select(n => server.AddFile($"dash/a/{n}.m4s", 5_000, 20 + n)).ToArray();
        var mpd = server.AddText("dash/manifest.mpd", """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
              <Period>
                <AdaptationSet contentType="video" mimeType="video/mp4">
                  <Representation id="v" bandwidth="1000000" width="640" height="360">
                    <SegmentTemplate timescale="1" duration="2" initialization="v/init.mp4" media="v/$Number$.m4s"/>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet contentType="audio" mimeType="audio/mp4">
                  <Representation id="a" bandwidth="64000">
                    <SegmentTemplate timescale="1" duration="2" initialization="a/init.mp4" media="a/$Number$.m4s"/>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """, "application/dash+xml");
        var id = AddStream(harness, server.UrlFor(mpd), variantId: "v", kind: StreamKind.Dash, fileName: "Movie.webm");

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal("Movie.mp4", harness.Get(id).FileName);
        Assert.Equal([TrackKind.Video, TrackKind.Audio], muxer.Inputs.Select(i => i.Kind));
        Assert.Equal(Concat([videoInit, .. video]), muxer.InputBytes[0]);
        Assert.Equal(Concat([audioInit, .. audio]), muxer.InputBytes[1]);
        Assert.Equal(muxer.Output, await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, "Movie.mp4")));
    }

    [Fact]
    public async Task Without_a_muxer_dash_tracks_are_kept_side_by_side()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var server = harness.Server;
        server.AddFile("raw/v.mp4", 30_000, 1);
        server.AddFile("raw/a.mp4", 9_000, 2);
        var mpd = server.AddText("raw/manifest.mpd", """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S">
              <Period>
                <AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1" width="640" height="360"><BaseURL>v.mp4</BaseURL></Representation></AdaptationSet>
                <AdaptationSet mimeType="audio/mp4"><Representation id="a" bandwidth="1"><BaseURL>a.mp4</BaseURL></Representation></AdaptationSet>
              </Period>
            </MPD>
            """, "application/dash+xml");
        var id = AddStream(harness, server.UrlFor(mpd), kind: StreamKind.Dash, fileName: "Clip.mp4");

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal("Clip.mp4", harness.Get(id).FileName);
        Assert.Equal(30_000, new FileInfo(Path.Combine(harness.SaveDirectory, "Clip.mp4")).Length);
        Assert.Equal(9_000, new FileInfo(Path.Combine(harness.SaveDirectory, "Clip (audio).m4a")).Length);
    }

    [Fact]
    public async Task A_failing_muxer_keeps_the_download()
    {
        await using var harness = await EngineHarness.CreateAsync();
        harness.Muxer = new RecordingMuxer { Fail = true };
        harness.RestartEngine();
        var (playlist, segments) = ServeHls(harness.Server, "muxfail", 3);
        var id = AddStream(harness, harness.Server.UrlFor(playlist));

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal("Show.ts", harness.Get(id).FileName);
        Assert.Equal(Concat(segments), await File.ReadAllBytesAsync(Path.Combine(harness.SaveDirectory, "Show.ts")));
    }

    [Fact]
    public async Task Cookies_stay_with_the_manifest_host()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var server = harness.Server;
        var segment = server.AddFile("cdn/seg.ts", 10_000, 3);

        // The playlist names its segment by another host name for the same server (localhost vs 127.0.0.1).
        var other = new UriBuilder(server.UrlFor(segment)) { Host = "localhost" }.Uri;
        var playlist = server.AddText("cookies/index.m3u8", $"#EXTM3U\n#EXTINF:2,\n{other.AbsoluteUri}\n#EXT-X-ENDLIST\n", Hls);
        var id = harness.Add(server.UrlFor(playlist), "c.mp4", d =>
        {
            d.IsStream = true;
            d.Cookies = "session=secret";
            d.Referrer = "https://example.com/watch";
        });

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        var manifestRequest = server.Requests.First(r => r.Path == "/cookies/index.m3u8");
        var segmentRequest = server.RequestsFor(segment).Single();
        Assert.Equal("session=secret", manifestRequest.Headers["Cookie"]);
        Assert.False(segmentRequest.Headers.ContainsKey("Cookie"));
        Assert.Equal("https://example.com/watch", segmentRequest.Headers["Referer"]);
    }

    [Fact]
    public void Muxer_arguments_copy_streams_from_local_files_only()
    {
        var args = FfmpegMuxer.Arguments([new MuxInput("v.mp4", TrackKind.Video), new MuxInput("a.mp4", TrackKind.Audio)], "out.mp4");

        Assert.Equal(
            "-hide_banner -nostdin -loglevel error -y -protocol_whitelist file -i v.mp4 -protocol_whitelist file -i a.mp4 -map 0:v? -map 1:a? -c copy -movflags +faststart -f mp4 out.mp4",
            string.Join(' ', args));
        Assert.Equal("-map 0:v? -map 0:a?", string.Join(' ', FfmpegMuxer.Arguments([new MuxInput("x.ts", TrackKind.Muxed)], "o.mp4").SkipWhile(a => a != "-map").Take(4)));
        Assert.False(new FfmpegMuxer(null).IsAvailable);
    }

    [Fact]
    public async Task Probe_recognizes_manifests_and_ignores_other_files()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var (playlist, _) = ServeHls(harness.Server, "probe", 2);
        var zip = harness.Server.AddFile("file.zip", 1000);
        var loader = new StreamManifestLoader([harness.Http]);

        var info = await loader.ProbeAsync(new RequestContext { Url = harness.Server.UrlFor(playlist) }, CancellationToken.None);
        var none = await loader.ProbeAsync(new RequestContext { Url = harness.Server.UrlFor(zip) }, CancellationToken.None);

        Assert.Equal(StreamKind.Hls, info!.Kind);
        Assert.Equal(4, info.Duration);
        Assert.Null(none);
        Assert.True(StreamManifestLoader.IsManifestType("application/x-mpegURL; charset=utf-8"));
        Assert.True(StreamManifestLoader.IsManifestAddress(new Uri("https://example.com/a/master.M3U8?x=1")));
        Assert.False(StreamManifestLoader.IsManifestAddress(new Uri("https://example.com/a/video.mp4")));
    }

    private sealed class RecordingMuxer : IStreamMuxer
    {
        public bool Fail { get; init; }

        public List<MuxInput> Inputs { get; } = [];

        public List<byte[]> InputBytes { get; } = [];

        public byte[] Output { get; } = Encoding.ASCII.GetBytes("merged mp4");

        public bool IsAvailable => true;

        public async Task MuxAsync(IReadOnlyList<MuxInput> inputs, string output, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new StreamMuxException("ffmpeg failed: test");
            }

            foreach (var input in inputs)
            {
                Inputs.Add(input);
                InputBytes.Add(await File.ReadAllBytesAsync(input.Path, cancellationToken));
            }

            await File.WriteAllBytesAsync(output, Output, cancellationToken);
        }
    }
}
