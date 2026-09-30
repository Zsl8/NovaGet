using NovaGet.Core.Paths;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Settings;

public sealed class AppPathsTests
{
    [Fact]
    public void Portable_flag_moves_everything_under_Data()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine(AppInfo.PortableFlagFileName), string.Empty);

        var paths = AppPaths.Resolve(temp.Path);

        Assert.True(paths.IsPortable);
        var data = temp.Combine("Data");
        Assert.Equal(Path.Combine(data, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(data, "novaget.db"), paths.DatabaseFile);
        Assert.Equal(Path.Combine(data, "Temp"), paths.DefaultTempDir);
        Assert.Equal(Path.Combine(data, "logs"), paths.LogsDir);
    }

    [Fact]
    public void Without_flag_uses_per_user_folders()
    {
        using var temp = new TempDirectory();

        var paths = AppPaths.Resolve(temp.Path);

        Assert.False(paths.IsPortable);
        Assert.EndsWith(Path.Combine("NovaGet", "settings.json"), paths.SettingsFile, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("NovaGet", "logs"), paths.LogsDir, StringComparison.Ordinal);
        Assert.False(paths.SettingsFile.StartsWith(temp.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureCreated_creates_directories()
    {
        using var temp = new TempDirectory();
        var paths = AppPaths.ForRoot(temp.Path);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.RoamingDir));
        Assert.True(Directory.Exists(paths.LogsDir));
        Assert.True(Directory.Exists(paths.DefaultTempDir));
    }

    [Fact]
    public void Title_uses_major_minor()
    {
        Assert.Matches(@"^NovaGet \d+\.\d+$", AppInfo.MainWindowTitle);
    }
}
