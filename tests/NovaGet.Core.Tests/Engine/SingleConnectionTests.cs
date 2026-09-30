using NovaGet.Core.Engine;
using NovaGet.Core.Models;

namespace NovaGet.Core.Tests.Engine;

/// <summary>Milestone 2: probe, single connection, pause/resume, temp → final move.</summary>
public sealed class SingleConnectionTests
{
    private static EngineOptions OneConnection(EngineOptions o) => o with { MaxConnections = 1 };

    [Fact]
    public async Task Downloads_a_file_and_moves_it_to_the_save_folder()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("files/setup.exe", 3 * 1024 * 1024 + 17);
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        var download = h.Get(id);
        Assert.Equal("setup.exe", download.FileName);
        Assert.Equal(file.Size, download.Size);
        Assert.Equal(file.Size, download.Downloaded);
        Assert.True(download.ResumeCapable);
        Assert.NotNull(download.CompletedAt);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(download.FullPath));
        Assert.Empty(h.Repository.GetSegments(id));
        Assert.False(Directory.Exists(Path.Combine(h.Options.TempDirectory, id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task Server_without_range_support()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("plain.bin", 2 * 1024 * 1024);
        file.SupportsRanges = false;
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.False(h.Get(id).ResumeCapable);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
    }

    [Fact]
    public async Task Unknown_size_download()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("chunked.bin", 1_234_567);
        file.SupportsRanges = false;
        file.SendContentLength = false;
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(1_234_567, h.Get(id).Size);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
    }

    [Fact]
    public async Task Zero_byte_file()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("empty.txt", 0);
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(0, new FileInfo(h.Get(id).FullPath).Length);
    }

    [Fact]
    public async Task Pause_and_resume_continues_where_it_stopped()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("slow.zip", 2 * 1024 * 1024);
        file.BytesPerSecond = 1024 * 1024;
        var id = h.Add(file);

        var stopped = h.WaitForEndAsync(id);
        Assert.True(h.Engine.Start(id));
        await EngineHarness.WaitUntilAsync(() => h.Engine.GetProgress(id)?.Downloaded > 400 * 1024);
        await h.Engine.PauseAsync(id);

        Assert.Equal(DownloadStatus.Paused, (await stopped).Status);
        var paused = h.Get(id);
        Assert.Equal(DownloadStatus.Paused, paused.Status);
        Assert.InRange(paused.Downloaded, 400 * 1024, file.Size - 1);
        Assert.Equal(paused.Downloaded, h.Repository.GetSegments(id).Sum(s => s.CurrentByte - s.StartByte));
        Assert.False(h.Engine.IsRunning(id));

        h.Server.ClearRequests();
        file.BytesPerSecond = 0;
        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        var resumed = Assert.Single(h.Server.RequestsFor(file), r => r.Method == "GET");
        Assert.Equal(paused.Downloaded, resumed.RangeStart);
        Assert.Equal(file.ETag, resumed.IfRange);
        Assert.Equal(206, resumed.Status);
    }

    [Fact]
    public async Task Changed_file_on_server_is_detected_and_restart_downloads_the_new_version()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("changing.bin", 2 * 1024 * 1024);
        file.BytesPerSecond = 1024 * 1024;
        var id = h.Add(file);

        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(() => h.Engine.GetProgress(id)?.Downloaded > 200 * 1024);
        await h.Engine.PauseAsync(id);

        file.ChangeContent(newSeed: 42);
        file.BytesPerSecond = 0;
        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.ServerFileChanged, result.ErrorKind);
        Assert.Equal(DownloadStatus.Error, h.Get(id).Status);

        var restarted = h.WaitForEndAsync(id);
        await h.Engine.RestartAsync(id);
        Assert.Equal(DownloadStatus.Completed, (await restarted).Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
    }

    [Fact]
    public async Task Size_change_without_validators_is_detected()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("novalidators.bin", 2 * 1024 * 1024);
        file.SendValidators = false;
        file.BytesPerSecond = 1024 * 1024;
        var id = h.Add(file);

        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(() => h.Engine.GetProgress(id)?.Downloaded > 200 * 1024);
        await h.Engine.PauseAsync(id);
        file.ChangeContent(newSeed: 7, newSize: 3 * 1024 * 1024);
        file.BytesPerSecond = 0;

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadErrorKind.ServerFileChanged, result.ErrorKind);
    }

    [Fact]
    public async Task Killed_process_resumes_from_last_checkpoint_without_corruption()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("crash.iso", 3 * 1024 * 1024);
        file.BytesPerSecond = 1024 * 1024;
        var id = h.Add(file);

        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(() => h.Get(id).Downloaded > 300 * 1024, because: "a checkpoint was saved");
        await h.Engine.SimulateCrashAsync(id);

        // The database still says "receiving": the process died without a clean stop.
        Assert.True(h.Get(id).Status.IsActive());

        h.RestartEngine();
        Assert.Equal(1, h.Engine.RecoverInterrupted());
        Assert.Equal(DownloadStatus.Paused, h.Get(id).Status);

        file.BytesPerSecond = 0;
        h.Server.ClearRequests();
        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        Assert.True(h.Server.RequestsFor(file).Single(r => r.Method == "GET").RangeStart > 0);
    }

    [Fact]
    public async Task Missing_temp_file_restarts_from_the_beginning()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("lost.bin", 2 * 1024 * 1024);
        file.BytesPerSecond = 1024 * 1024;
        var id = h.Add(file);

        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(() => h.Engine.GetProgress(id)?.Downloaded > 200 * 1024);
        await h.Engine.PauseAsync(id);
        Directory.Delete(Path.Combine(h.Options.TempDirectory, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), recursive: true);
        file.BytesPerSecond = 0;

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
    }

    [Fact]
    public async Task Dropped_connections_are_retried_and_the_file_is_intact()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("flaky.bin", 4 * 1024 * 1024);
        file.DropAfterBytes = (200 * 1024, 700 * 1024);
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        Assert.True(h.Server.RequestsFor(file).Count(r => r.Method == "GET") > 3);
    }

    [Fact]
    public async Task Server_without_ranges_restarts_after_a_drop()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("noresume.bin", 1024 * 1024);
        file.SupportsRanges = false;
        file.DropAfterBytes = (300 * 1024, 300 * 1024);
        file.DropLimit = 2; // the probe's GET, then the first real transfer
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(h.Get(id).FullPath));
        var gets = h.Server.RequestsFor(file).Where(r => r.Method == "GET" && r.Range != "bytes=0-0").ToList();
        Assert.Equal(2, gets.Count);
        Assert.All(gets, r => Assert.Null(r.Range));
    }

    [Fact]
    public async Task Not_found_fails_without_retrying()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("gone.zip", 1000);
        file.ForceStatus = 404;
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.LinkExpired, result.ErrorKind);
        Assert.Equal("HTTP 404 Not Found", h.Get(id).LastError);
        Assert.Equal(1, h.Server.RequestsFor(file).Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task Server_errors_are_retried_up_to_the_limit()
    {
        await using var h = await EngineHarness.CreateAsync(o => OneConnection(o) with { MaxRetries = 3 });
        var file = h.Server.AddFile("busy.bin", 1000);
        var id = h.Add(file, "busy.bin", d =>
        {
            d.Size = 1000;
            d.ResumeCapable = true;
        });
        file.ForceStatus = 503;

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.ServerBusy, result.ErrorKind);
        Assert.Equal(4, h.Server.RequestsFor(file).Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task Existing_file_gets_a_numbered_name()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("report.pdf", 1000);
        Directory.CreateDirectory(h.SaveDirectory);
        await File.WriteAllTextAsync(Path.Combine(h.SaveDirectory, "report.pdf"), "old");
        var id = h.Add(file);

        await h.RunAsync(id);

        Assert.Equal("report (2).pdf", h.Get(id).FileName);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(h.SaveDirectory, "report.pdf")));
    }

    [Fact]
    public async Task File_name_comes_from_content_disposition_when_not_set()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("dl.php", 500);
        file.ContentDisposition = "attachment; filename=\"real name.7z\"";
        var id = h.Add(file);

        await h.RunAsync(id);

        Assert.Equal("real name.7z", h.Get(id).FileName);
        Assert.True(File.Exists(Path.Combine(h.SaveDirectory, "real name.7z")));
    }

    [Fact]
    public async Task Redirected_address_is_stored_and_original_kept()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("mirror/file.bin", 5000);
        var original = h.Server.UrlFor("redirect/3/mirror/file.bin");
        var id = h.Add(original);

        await h.RunAsync(id);

        var download = h.Get(id);
        Assert.Equal(original.AbsoluteUri, download.OriginalUrl);
        Assert.Equal(h.Server.UrlFor(file).AbsoluteUri, download.Url);
    }

    [Fact]
    public async Task Low_disk_space_pauses_the_download()
    {
        await using var h = await EngineHarness.CreateAsync(o => OneConnection(o) with { FreeSpaceMargin = long.MaxValue / 2 });
        var file = h.Server.AddFile("huge.bin", 1000);
        var id = h.Add(file);

        var result = await h.RunAsync(id);

        Assert.Equal(DownloadStatus.Paused, result.Status);
        Assert.Equal(DownloadErrorKind.DiskFull, result.ErrorKind);
        Assert.StartsWith("Not enough disk space", h.Get(id).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_reports_speed_and_connection_details()
    {
        await using var h = await EngineHarness.CreateAsync(OneConnection);
        var file = h.Server.AddFile("progress.bin", 2 * 1024 * 1024);
        file.BytesPerSecond = 512 * 1024;
        var id = h.Add(file);

        h.Engine.Start(id);
        await EngineHarness.WaitUntilAsync(() => h.Engine.GetProgress(id) is { BytesPerSecond: > 0, Downloaded: > 200 * 1024 });
        var progress = h.Engine.GetProgress(id)!;
        await h.Engine.PauseAsync(id);

        Assert.Equal(DownloadStatus.Receiving, progress.Status);
        Assert.Equal(file.Size, progress.Size);
        Assert.InRange(progress.BytesPerSecond, 100 * 1024, 2 * 1024 * 1024);
        Assert.NotNull(progress.TimeLeft);
        var connection = Assert.Single(progress.Connections);
        Assert.Equal(1, connection.Number);
        Assert.Equal("Receiving data...", connection.Info);
        Assert.Single(progress.Segments);
    }
}
