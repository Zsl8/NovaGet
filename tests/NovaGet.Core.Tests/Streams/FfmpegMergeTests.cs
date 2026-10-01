using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NovaGet.Core.Engine.Streams;
using NovaGet.Core.Models;
using NovaGet.Core.Tests.Engine;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Streams;

/// <summary>Runs only where ffmpeg is available: <c>NOVAGET_FFMPEG</c> (set by build.ps1 from the fetched build) or the PATH.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegTools.Ffmpeg is null || FfmpegTools.Ffprobe is null)
        {
            Skip = "ffmpeg/ffprobe not found (set NOVAGET_FFMPEG or put them on the PATH).";
        }
    }
}

internal static class FfmpegTools
{
    public static string? Ffmpeg { get; } = Environment.GetEnvironmentVariable("NOVAGET_FFMPEG") is { Length: > 0 } configured && File.Exists(configured)
        ? configured
        : FfmpegMuxer.Locate(AppContext.BaseDirectory);

    public static string? Ffprobe => Ffmpeg is null
        ? null
        : Path.Combine(Path.GetDirectoryName(Ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe") is var probe && File.Exists(probe) ? probe : null;

    public static async Task<string> RunAsync(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(exe)} failed: {await errors}");
        return await output;
    }
}

public sealed class FfmpegMergeTests
{
    /// <summary>Section 23: a local playlist with 50 segments → merged MP4 plays (ffprobe duration check).</summary>
    [FfmpegFact]
    public async Task Fifty_segment_hls_playlist_merges_into_a_playable_mp4()
    {
        using var source = new TempDirectory();

        // 50 one-second segments: MPEG-4 Part 2 video and AAC audio (encoders in every LGPL build). dump_extra repeats
        // the video headers in each keyframe, as H.264 streams carry SPS/PPS in band, so a stream copy can read them.
        await FfmpegTools.RunAsync(FfmpegTools.Ffmpeg!,
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050",
            "-t", "50", "-c:v", "mpeg4", "-bsf:v", "dump_extra", "-g", "10", "-force_key_frames", "expr:gte(t,n_forced*1)", "-c:a", "aac", "-b:a", "32k",
            "-f", "hls", "-hls_time", "1", "-hls_list_size", "0", "-hls_segment_filename", source.Combine("seg%03d.ts"),
            source.Combine("index.m3u8"));
        var segments = Directory.GetFiles(source.Path, "seg*.ts").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(50, segments.Length);

        await using var harness = await EngineHarness.CreateAsync(o => o with { MaxConnections = 8 });
        harness.Muxer = new FfmpegMuxer(FfmpegTools.Ffmpeg);
        harness.RestartEngine();
        foreach (var segment in segments)
        {
            harness.Server.AddContent("hls/" + Path.GetFileName(segment), await File.ReadAllBytesAsync(segment), "video/mp2t");
        }

        var playlist = harness.Server.AddText("hls/index.m3u8", await File.ReadAllTextAsync(source.Combine("index.m3u8")), "application/vnd.apple.mpegurl");
        var manifest = harness.Server.UrlFor(playlist);
        var id = harness.Add(manifest, "Test clip.mp4", d =>
        {
            d.IsStream = true;
            d.StreamManifestJson = new StreamSelection { Kind = StreamKind.Hls, ManifestUrl = manifest.AbsoluteUri }.ToJson();
        });

        var result = await harness.RunAsync(id, TimeSpan.FromMinutes(2));

        Assert.Equal(DownloadStatus.Completed, result.Status);
        var download = harness.Get(id);
        Assert.Equal("Test clip.mp4", download.FileName);
        var output = Path.Combine(harness.SaveDirectory, download.FileName);
        var json = await FfmpegTools.RunAsync(FfmpegTools.Ffprobe!,
            "-v", "error", "-show_entries", "format=duration,format_name:stream=codec_type", "-of", "json", output);
        using var probe = JsonDocument.Parse(json);
        var format = probe.RootElement.GetProperty("format");
        Assert.Contains("mp4", format.GetProperty("format_name").GetString(), StringComparison.Ordinal);
        var duration = double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(duration, 49.5, 50.5);
        var kinds = probe.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToList();
        Assert.Contains("video", kinds);
        Assert.Contains("audio", kinds);

        // The whole file decodes without errors: it plays.
        await FfmpegTools.RunAsync(FfmpegTools.Ffmpeg!, "-v", "error", "-xerror", "-i", output, "-f", "null", "-");
    }

    [FfmpegFact]
    public async Task Separate_video_and_audio_tracks_are_muxed_into_one_file()
    {
        using var work = new TempDirectory();
        var video = work.Combine("video.mp4");
        var audio = work.Combine("audio.mp4");
        await FfmpegTools.RunAsync(FfmpegTools.Ffmpeg!, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10",
            "-t", "4", "-c:v", "mpeg4", "-movflags", "+frag_keyframe+empty_moov", video);
        await FfmpegTools.RunAsync(FfmpegTools.Ffmpeg!, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050",
            "-t", "4", "-c:a", "aac", "-movflags", "+frag_keyframe+empty_moov", audio);
        var output = work.Combine("out.mp4");

        await new FfmpegMuxer(FfmpegTools.Ffmpeg).MuxAsync([new MuxInput(video, TrackKind.Video), new MuxInput(audio, TrackKind.Audio)], output, CancellationToken.None);

        var json = await FfmpegTools.RunAsync(FfmpegTools.Ffprobe!, "-v", "error", "-show_entries", "stream=codec_type", "-of", "json", output);
        Assert.Equal(["video", "audio"], JsonDocument.Parse(json).RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()));
    }

    [FfmpegFact]
    public async Task Ffmpeg_errors_are_reported()
    {
        using var work = new TempDirectory();
        var bogus = work.Combine("bogus.ts");
        await File.WriteAllTextAsync(bogus, "not a video");

        var error = await Assert.ThrowsAsync<StreamMuxException>(() =>
            new FfmpegMuxer(FfmpegTools.Ffmpeg).MuxAsync([new MuxInput(bogus, TrackKind.Muxed)], work.Combine("o.mp4"), CancellationToken.None));

        Assert.StartsWith("ffmpeg failed", error.Message, StringComparison.Ordinal);
    }
}
