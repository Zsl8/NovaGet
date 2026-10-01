using System.Text.Json;
using NovaGet.Core.Paths;
using NovaGet.Core.Settings;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Settings;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;

    public SettingsServiceTests()
    {
        _paths = AppPaths.ForRoot(_temp.Path);
        _paths.EnsureCreated();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var service = new SettingsService(_paths);

        Assert.Equal(8, service.Current.Connection.DefaultMaxConnections);
        Assert.Equal(30, service.Current.Connection.TimeoutSeconds);
        Assert.Equal(20, service.Current.Connection.MaxRetries);
        Assert.True(service.Current.Downloads.ShowStartDialog);
        Assert.Equal(ProxyMode.System, service.Current.Proxy.Mode);
        Assert.Contains("zip", service.Current.FileTypes.AutoCaptureExtensions, StringComparison.Ordinal);
        Assert.False(File.Exists(_paths.SettingsFile));
    }

    [Fact]
    public void Update_persists_and_round_trips()
    {
        var service = new SettingsService(_paths);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Update(s =>
        {
            s.Connection.DefaultMaxConnections = 16;
            s.Proxy.Mode = ProxyMode.Manual;
            s.Connection.HostSpeedLimits["example.com"] = 500;
        });

        Assert.Equal(1, raised);
        var reloaded = new SettingsService(_paths);
        Assert.Equal(16, reloaded.Current.Connection.DefaultMaxConnections);
        Assert.Equal(ProxyMode.Manual, reloaded.Current.Proxy.Mode);
        Assert.Equal(500, reloaded.Current.Connection.HostSpeedLimits["EXAMPLE.com"]);
    }

    [Fact]
    public void Enums_are_written_as_strings()
    {
        var service = new SettingsService(_paths);
        service.Update(s => s.Proxy.Mode = ProxyMode.AutoConfigScript);

        var json = File.ReadAllText(_paths.SettingsFile);
        Assert.Contains("\"autoConfigScript\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Second_save_keeps_previous_version_as_backup()
    {
        var service = new SettingsService(_paths);
        service.Update(s => s.Connection.MaxRetries = 7);
        service.Update(s => s.Connection.MaxRetries = 9);

        Assert.True(File.Exists(_paths.SettingsBackupFile));
        var backup = SettingsJson.Deserialize(File.ReadAllText(_paths.SettingsBackupFile));
        Assert.Equal(7, backup.Connection.MaxRetries);
        Assert.False(File.Exists(_paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_backup_and_does_not_clobber_it()
    {
        var service = new SettingsService(_paths);
        service.Update(s => s.Connection.MaxRetries = 7);
        service.Update(s => s.Connection.MaxRetries = 9);
        File.WriteAllText(_paths.SettingsFile, "{ this is not json");

        var recovered = new SettingsService(_paths);
        Assert.Equal(7, recovered.Current.Connection.MaxRetries);
        Assert.True(File.Exists(_paths.SettingsFile + ".corrupt"));

        recovered.Update(s => s.Connection.TimeoutSeconds = 45);
        var again = new SettingsService(_paths);
        Assert.Equal(7, again.Current.Connection.MaxRetries);
        Assert.Equal(45, again.Current.Connection.TimeoutSeconds);
    }

    [Fact]
    public void Partial_file_gets_defaults_for_missing_values()
    {
        File.WriteAllText(_paths.SettingsFile, """{ "connection": { "maxRetries": 3 } }""");

        var service = new SettingsService(_paths);

        Assert.Equal(3, service.Current.Connection.MaxRetries);
        Assert.Equal(30, service.Current.Connection.TimeoutSeconds);
        Assert.NotNull(service.Current.Sounds.DownloadComplete);
        Assert.True(service.Current.General.LaunchOnStartup);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 4)]
    [InlineData(12, 8)]
    [InlineData(20, 16)]
    [InlineData(100, 32)]
    public void Connection_count_snaps_to_allowed_values(int value, int expected)
    {
        File.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(new { connection = new { defaultMaxConnections = value } }));

        var service = new SettingsService(_paths);

        Assert.Equal(expected, service.Current.Connection.DefaultMaxConnections);
    }

    [Fact]
    public void Replace_swaps_all_settings()
    {
        var service = new SettingsService(_paths);
        var imported = new AppSettings();
        imported.General.Language = "ar";

        service.Replace(imported);
        imported.General.Language = "fr"; // must not leak into the service (it keeps a copy)

        Assert.Equal("ar", new SettingsService(_paths).Current.General.Language);
        Assert.Equal("ar", service.Current.General.Language);
    }

    [Fact]
    public void A_first_run_applies_the_installer_choices_once()
    {
        File.WriteAllText(Path.Combine(_paths.ExecutableDir, InstallDefaults.FileName),
            """{ "launchOnStartup": true, "browserIntegration": false, "monitorClipboard": true }""");

        var first = new SettingsService(_paths);
        Assert.True(first.IsFirstRun);
        Assert.True(first.Current.General.MonitorClipboard);
        Assert.Empty(first.Current.General.IntegratedBrowsers);

        // Once settings exist, the installer's file no longer matters (an upgrade keeps the user's options).
        first.Update(s => s.General.MonitorClipboard = false);
        var later = new SettingsService(_paths);
        Assert.False(later.IsFirstRun);
        Assert.False(later.Current.General.MonitorClipboard);
    }

    [Theory]
    [InlineData("""{ "browserIntegration": true, "monitorClipboard": false }""", false, 6)]
    [InlineData("""{ "monitorClipboard": "yes" }""", false, 6)]
    [InlineData("not json", false, 6)]
    [InlineData("[]", false, 6)]
    public void Install_defaults_are_read_leniently(string json, bool clipboard, int browsers)
    {
        File.WriteAllText(Path.Combine(_paths.ExecutableDir, InstallDefaults.FileName), json);

        var service = new SettingsService(_paths);

        Assert.Equal(clipboard, service.Current.General.MonitorClipboard);
        Assert.Equal(browsers, service.Current.General.IntegratedBrowsers.Count);
    }

    [Fact]
    public void A_corrupt_settings_file_is_not_a_first_run()
    {
        File.WriteAllText(_paths.SettingsFile, "{ not json");
        File.WriteAllText(Path.Combine(_paths.ExecutableDir, InstallDefaults.FileName), """{ "monitorClipboard": true }""");

        var service = new SettingsService(_paths);

        Assert.False(service.IsFirstRun);
        Assert.False(service.Current.General.MonitorClipboard);
    }
}
