using NovaGet.Core.Engine.Storage;
using NovaGet.Core.Integration;
using NovaGet.Core.Network;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Network;

public sealed class IntegrationHelpersTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", "chrome")]
    [InlineData("\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --single-argument %1", "edge")]
    [InlineData("C:\\Program Files\\Mozilla Firefox\\firefox.exe -osint -url \"%1\"", "firefox")]
    [InlineData("\"C:\\Users\\a\\AppData\\Local\\Programs\\Opera\\launcher.exe\"", "opera")]
    [InlineData("\"C:\\Tools\\launcher.exe\"", null)]
    [InlineData("\"C:\\Program Files\\BraveSoftware\\Brave-Browser\\Application\\brave.exe\"", "brave")]
    [InlineData("C:\\Users\\a\\AppData\\Local\\Vivaldi\\Application\\vivaldi.exe", "vivaldi")]
    [InlineData("\"C:\\Program Files\\Internet Explorer\\iexplore.exe\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Browsers_are_recognized_by_executable(string? command, string? expected)
    {
        Assert.Equal(expected, BrowserCatalog.FromExecutable(command)?.Id);
    }

    [Fact]
    public void Catalog_lists_the_six_browsers_in_order()
    {
        Assert.Equal(new[] { "chrome", "edge", "firefox", "opera", "brave", "vivaldi" }, BrowserCatalog.All.Select(b => b.Id));
        Assert.Equal("firefox", BrowserCatalog.Find("FIREFOX")!.HelpAnchor);
        Assert.Equal("chromium", BrowserCatalog.Find("edge")!.HelpAnchor);
    }

    [Fact]
    public void Phonebook_entries_are_section_names()
    {
        var content = "[Office VPN]\r\nMEDIA=rastapi\r\nDEVICE=vpn\r\n\r\n[Dial-up]\nMEDIA=serial\n[ ]\n";

        Assert.Equal(new[] { "Office VPN", "Dial-up" }, RasPhonebook.ParseEntries(content));
    }

    [Fact]
    public void Phonebooks_are_merged_and_missing_files_ignored()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("a.pbk"), "[Work]\n[Home]\n");
        File.WriteAllText(temp.Combine("b.pbk"), "[work]\n[Backup]\n");

        var entries = RasPhonebook.ReadEntries([temp.Combine("a.pbk"), temp.Combine("b.pbk"), temp.Combine("missing.pbk")]);

        Assert.Equal(new[] { "Backup", "Home", "Work" }, entries);
    }

    [Fact]
    public void Temp_folders_of_unfinished_downloads_move_to_the_new_directory()
    {
        using var temp = new TempDirectory();
        var oldDir = temp.Combine("old");
        var newDir = temp.Combine("new");
        Directory.CreateDirectory(Path.Combine(oldDir, "12"));
        File.WriteAllText(Path.Combine(oldDir, "12", "movie.mkv.ngpart"), "partial");
        Directory.CreateDirectory(Path.Combine(oldDir, "13", "sub"));
        File.WriteAllText(Path.Combine(oldDir, "13", "sub", "x"), "y");
        Directory.CreateDirectory(Path.Combine(oldDir, "not-a-download"));

        var result = TempDirectoryMover.Move(oldDir, newDir);

        Assert.Equal(2, result.Moved);
        Assert.Empty(result.Failed);
        Assert.Equal("partial", File.ReadAllText(Path.Combine(newDir, "12", "movie.mkv.ngpart")));
        Assert.True(File.Exists(Path.Combine(newDir, "13", "sub", "x")));
        Assert.False(Directory.Exists(Path.Combine(oldDir, "12")));
        Assert.True(Directory.Exists(Path.Combine(oldDir, "not-a-download")));
    }

    [Fact]
    public void Moving_to_the_same_or_a_missing_directory_does_nothing()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("t", "5"));

        Assert.Equal(0, TempDirectoryMover.Move(temp.Combine("t"), temp.Combine("t") + Path.DirectorySeparatorChar).Moved);
        Assert.Equal(0, TempDirectoryMover.Move(temp.Combine("missing"), temp.Combine("t")).Moved);
        Assert.True(Directory.Exists(temp.Combine("t", "5")));
    }
}
