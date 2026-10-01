using System.Collections.Concurrent;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Engine;

public sealed class FileFinisherTests
{
    [Fact]
    public async Task A_matching_checksum_completes_quietly_and_a_wrong_one_warns()
    {
        await using var harness = await EngineHarness.CreateAsync();
        var file = harness.Server.AddFile("sum.bin", 50_000, 3);
        var good = harness.Add(file, "good.bin", d => d.ChecksumExpected = "sha256:" + file.Sha256());
        var bad = harness.Add(file, "bad.bin", d => (d.ChecksumAlgo, d.ChecksumExpected) = ("MD5", new string('0', 32)));

        var ok = await harness.RunAsync(good);
        var wrong = await harness.RunAsync(bad);

        Assert.Equal((DownloadStatus.Completed, DownloadErrorKind.None), (ok.Status, ok.ErrorKind));
        Assert.Null(harness.Get(good).LastError);
        Assert.Equal((DownloadStatus.Completed, DownloadErrorKind.ChecksumMismatch), (wrong.Status, wrong.ErrorKind));
        Assert.Contains("MD5 checksum doesn't match", harness.Get(bad).LastError, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(harness.SaveDirectory, "bad.bin")));
    }

    [Fact]
    public void Zone_identifier_names_the_page_and_the_file_without_logins()
    {
        var text = FileFinisher.ZoneIdentifier("https://example.com/page", "https://user:secret@files.example.com/a.zip");

        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/page\r\nHostUrl=https://files.example.com/a.zip\r\n", text);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", FileFinisher.ZoneIdentifier("about:blank", "javascript:alert(1)"));
    }

    [WindowsFact]
    public async Task Finished_files_carry_the_mark_of_the_web()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { MarkOfTheWeb = true });
        var file = harness.Server.AddFile("motw.exe", 10_000, 4);
        var id = harness.Add(file, "motw.exe", d => d.Referrer = "https://example.com/downloads");

        await harness.RunAsync(id);

        var stream = File.ReadAllText(Path.Combine(harness.SaveDirectory, "motw.exe") + FileFinisher.ZoneIdentifierStream);
        Assert.Contains("ZoneId=3", stream, StringComparison.Ordinal);
        Assert.Contains("ReferrerUrl=https://example.com/downloads", stream, StringComparison.Ordinal);
        Assert.Contains("HostUrl=" + harness.Server.UrlFor(file).AbsoluteUri, stream, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-Scan -ScanType 3 -File \"[file]\"", "-Scan -ScanType 3 -File \"C:\\D\\a b.zip\"")]
    [InlineData("/scan [file] /quiet", "/scan \"C:\\D\\a b.zip\" /quiet")]
    [InlineData("--file=[FILE]", "--file=\"C:\\D\\a b.zip\"")]
    public void Scanner_arguments_quote_the_path_once(string template, string expected) =>
        Assert.Equal(expected, FileFinisher.ScanArguments(template, @"C:\D\a b.zip"));

    [Fact]
    public async Task The_virus_scanner_runs_on_the_finished_file()
    {
        var (program, arguments) = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c copy /y \"[file]\" \"[file].scanned\"")
            : ("/bin/sh", "-c \"cp \\\"$0\\\" \\\"$0.scanned\\\"\" \"[file]\"");
        await using var harness = await EngineHarness.CreateAsync(o => o with { VirusScanProgram = program, VirusScanArguments = arguments });
        var file = harness.Server.AddFile("scan me.zip", 20_000, 5);
        var id = harness.Add(file, "scan me.zip");
        var states = new ConcurrentQueue<DownloadStatus>();
        harness.Engine.StateChanged += (_, e) => states.Enqueue(e.Status);

        var result = await harness.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Contains(DownloadStatus.Scanning, states);
        Assert.True(File.Exists(Path.Combine(harness.SaveDirectory, "scan me.zip.scanned")), "the scanner saw the file");
    }

    [Fact]
    public async Task A_missing_scanner_does_not_fail_the_download()
    {
        await using var harness = await EngineHarness.CreateAsync(o => o with { VirusScanProgram = Path.Combine(Path.GetTempPath(), "no-such-scanner.exe") });
        var file = harness.Server.AddFile("noscan.bin", 5_000, 6);

        var result = await harness.RunAsync(harness.Add(file));

        Assert.Equal(DownloadStatus.Completed, result.Status);
    }
}
