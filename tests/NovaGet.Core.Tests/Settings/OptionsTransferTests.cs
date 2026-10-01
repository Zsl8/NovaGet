using System.Text.Json.Nodes;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Security;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;
using NovaGet.Core.Tests.Data;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data.Repositories;

namespace NovaGet.Core.Tests.Settings;

public sealed class OptionsTransferTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TestDatabase _db = new();
    private readonly SettingsService _settings;
    private readonly CategoryRepository _categories;
    private readonly ServerExceptionRepository _exceptions;
    private readonly SettingsPackageService _package;

    public OptionsTransferTests()
    {
        var paths = AppPaths.ForRoot(_temp.Path);
        paths.EnsureCreated();
        _settings = new SettingsService(paths);
        _categories = new CategoryRepository(_db.Database);
        _exceptions = new ServerExceptionRepository(_db.Database);
        _package = new SettingsPackageService(_settings, _categories, _exceptions);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Apply_copies_options_and_keeps_state_that_is_not_an_option()
    {
        var live = new AppSettings();
        live.Ui.MainWindow.Width = 1234;
        live.Downloads.AddressHistory.Add("https://example.com/a.zip");
        live.SaveTo.RecentFolders.Add(@"C:\Stuff");
        live.Connection.HostSpeedLimits["example.com"] = 300;
        live.SpeedLimiter.Enabled = true;
        live.General.MonitorClipboard = true;
        live.Advanced.LastUpdateCheck = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var edited = SettingsJson.Clone(new AppSettings());
        edited.Connection.DefaultMaxConnections = 16;
        edited.Downloads.ShowStartDialog = false;
        edited.Proxy.Mode = ProxyMode.None;
        edited.Sounds.DownloadAdded.Enabled = true;
        edited.Advanced.MinSegmentSizeKB = 1; // out of range: normalized
        edited.Ui.MainWindow.Width = 10;     // not an option: ignored
        edited.Downloads.AddressHistory.Clear();
        edited.SpeedLimiter.Enabled = false;
        edited.General.MonitorClipboard = false;

        OptionsApplier.Apply(live, edited);

        Assert.Equal(16, live.Connection.DefaultMaxConnections);
        Assert.False(live.Downloads.ShowStartDialog);
        Assert.Equal(ProxyMode.None, live.Proxy.Mode);
        Assert.True(live.Sounds.DownloadAdded.Enabled);
        Assert.Equal(16, live.Advanced.MinSegmentSizeKB);
        Assert.Equal(1234, live.Ui.MainWindow.Width);
        Assert.Single(live.Downloads.AddressHistory);
        Assert.Single(live.SaveTo.RecentFolders);
        Assert.Equal(300, live.Connection.HostSpeedLimits["EXAMPLE.com"]);
        Assert.True(live.SpeedLimiter.Enabled);
        Assert.True(live.General.MonitorClipboard);
        Assert.NotNull(live.Advanced.LastUpdateCheck);
    }

    [Fact]
    public void Apply_does_not_share_objects_with_the_edited_copy()
    {
        var live = new AppSettings();
        var edited = new AppSettings();
        OptionsApplier.Apply(live, edited);

        edited.FileTypes.ExcludedSites.Add("later.example");
        Assert.DoesNotContain("later.example", live.FileTypes.ExcludedSites);
    }

    [Fact]
    public void Defaults_reset_options_but_keep_layout_and_limiter()
    {
        var current = new AppSettings();
        current.Connection.DefaultMaxConnections = 32;
        current.Proxy.Mode = ProxyMode.Manual;
        current.General.MonitorClipboard = true;
        current.Ui.CategoriesPaneWidth = 333;
        current.SpeedLimiter.MaxKBps = 77;

        var reset = OptionsApplier.Defaults(current);

        Assert.Equal(8, reset.Connection.DefaultMaxConnections);
        Assert.Equal(ProxyMode.System, reset.Proxy.Mode);
        Assert.False(reset.General.MonitorClipboard);
        Assert.Equal(333, reset.Ui.CategoriesPaneWidth);
        Assert.Equal(77, reset.SpeedLimiter.MaxKBps);
        Assert.Equal(32, current.Connection.DefaultMaxConnections); // input untouched
    }

    [Fact]
    public void Export_leaves_out_passwords()
    {
        _settings.Update(s =>
        {
            s.Proxy.Http.Host = "proxy.example";
            s.Proxy.Http.User = "alice";
            s.Proxy.Http.ProtectedPassword = "SECRET-BLOB";
            s.Proxy.Socks.ProtectedPassword = "SECRET-BLOB";
            s.DialUp.ProtectedPassword = "SECRET-BLOB";
        });

        var json = _package.Export();

        Assert.DoesNotContain("SECRET-BLOB", json, StringComparison.Ordinal);
        Assert.Contains("proxy.example", json, StringComparison.Ordinal);
        var root = JsonNode.Parse(json)!;
        Assert.Equal(SettingsTransfer.Format, root["format"]!.GetValue<string>());
        Assert.Equal(6, root["categories"]!.AsArray().Count);
    }

    [Fact]
    public void Export_then_import_round_trips_options_categories_and_exceptions()
    {
        _settings.Update(s =>
        {
            s.Connection.DefaultMaxConnections = 4;
            s.FileTypes.ExcludedSites = ["*.cdn.example"];
            s.Downloads.DuplicateAction = DuplicateDownloadAction.AddNumbered;
        });
        var music = _categories.GetAll().Single(c => c.Name == "Music");
        music.DefaultSaveDir = @"D:\Music";
        music.Extensions = "mp3 flac";
        _categories.Update(music);
        _categories.Insert(new Category { Name = "Books", Extensions = "epub" });
        _exceptions.Insert(new ServerException { Host = "slow.example", MaxConnections = 2 });
        var json = _package.Export();

        // A different installation with default settings.
        using var otherDb = new TestDatabase();
        using var otherTemp = new TempDirectory();
        var otherPaths = AppPaths.ForRoot(otherTemp.Path);
        otherPaths.EnsureCreated();
        var otherSettings = new SettingsService(otherPaths);
        var otherCategories = new CategoryRepository(otherDb.Database);
        var otherExceptions = new ServerExceptionRepository(otherDb.Database);
        otherExceptions.Insert(new ServerException { Host = "SLOW.example", MaxConnections = 8 });
        otherSettings.Update(s => s.Ui.MainWindow.Width = 1000);

        new SettingsPackageService(otherSettings, otherCategories, otherExceptions).Import(json);

        Assert.Equal(4, otherSettings.Current.Connection.DefaultMaxConnections);
        Assert.Equal(new[] { "*.cdn.example" }, otherSettings.Current.FileTypes.ExcludedSites);
        Assert.Equal(DuplicateDownloadAction.AddNumbered, otherSettings.Current.Downloads.DuplicateAction);
        Assert.Equal(1000, otherSettings.Current.Ui.MainWindow.Width);
        var importedMusic = otherCategories.GetAll().Single(c => c.Name == "Music");
        Assert.Equal(@"D:\Music", importedMusic.DefaultSaveDir);
        Assert.Equal("mp3 flac", importedMusic.Extensions);
        Assert.Equal("epub", otherCategories.GetAll().Single(c => c.Name == "Books").Extensions);
        Assert.Equal(7, otherCategories.GetAll().Count);
        var exception = Assert.Single(otherExceptions.GetAll());
        Assert.Equal(2, exception.MaxConnections);
    }

    [Fact]
    public void Import_keeps_saved_passwords_for_the_same_user()
    {
        var protector = new DevOnlySecretProtector();
        _settings.Update(s =>
        {
            s.Proxy.Http.Host = "proxy.example";
            s.Proxy.Http.User = "alice";
            s.Proxy.Http.ProtectedPassword = protector.Protect("pw");
        });
        var json = _package.Export();

        _package.Import(json);

        Assert.Equal("pw", protector.Unprotect(_settings.Current.Proxy.Http.ProtectedPassword));
    }

    [Fact]
    public void Import_accepts_a_plain_settings_file()
    {
        var settings = new AppSettings();
        settings.Connection.TimeoutSeconds = 99;

        _package.Import(SettingsJson.Serialize(settings));

        Assert.Equal(99, _settings.Current.Connection.TimeoutSeconds);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"hello\": 1}")]
    [InlineData("{\"format\": \"novaget-settings\", \"version\": 99, \"settings\": {}}")]
    [InlineData("{\"format\": \"novaget-settings\", \"version\": 1}")]
    [InlineData("{\"format\": \"novaget-settings\", \"version\": 1, \"settings\": {\"connection\": {\"timeoutSeconds\": \"soon\"}}}")]
    public void Import_rejects_files_that_are_not_settings(string json)
    {
        var before = SettingsJson.Serialize(_settings.Current);

        Assert.Throws<FormatException>(() => _package.Import(json));
        Assert.Equal(before, SettingsJson.Serialize(_settings.Current));
    }

    [Fact]
    public void Import_skips_invalid_categories_and_exceptions()
    {
        var json = """
            {
              "format": "novaget-settings", "version": 1, "settings": {},
              "categories": [ { "name": "" }, { "name": "Fine", "extensions": "abc" } ],
              "serverExceptions": [ { "host": "a.example", "maxConnections": 0 }, { "host": "b.example", "maxConnections": 99 },
                                    { "host": "c.example", "maxConnections": 4 } ]
            }
            """;

        var package = SettingsTransfer.Import(json);

        Assert.Equal("Fine", Assert.Single(package.Categories).Name);
        Assert.Equal("c.example", Assert.Single(package.ServerExceptions).Host);
    }

    [Fact]
    public void Reset_to_defaults_persists()
    {
        _settings.Update(s => s.Connection.MaxRetries = 3);

        _package.ResetToDefaults();

        Assert.Equal(20, _settings.Current.Connection.MaxRetries);
        Assert.Equal(20, new SettingsService(AppPaths.ForRoot(_temp.Path)).Current.Connection.MaxRetries);
    }
}
