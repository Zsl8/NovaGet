using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;
using NovaGet.Core.Tests.Data;
using NovaGet.Core.Tests.Infrastructure;
using NovaGet.Data.Repositories;

namespace NovaGet.Core.Tests.Services;

public sealed class CategoryMatcherTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private CategoryMatcher Matcher() => new(new CategoryRepository(_db.Database).GetAll());

    [Theory]
    [InlineData("archive.zip", "Compressed")]
    [InlineData("ARCHIVE.RAR", "Compressed")]
    [InlineData("backup.part.r01", "Compressed")]
    [InlineData("backup.r15", "Compressed")]
    [InlineData("linux.tar.gz", "Compressed")]
    [InlineData("paper.pdf", "Documents")]
    [InlineData("song.flac", "Music")]
    [InlineData("setup.msi", "Programs")]
    [InlineData("ubuntu.iso", "Programs")]
    [InlineData("movie.mkv", "Video")]
    [InlineData("clip.ts", "Video")]
    [InlineData("readme", "General")]
    [InlineData("page.html", "General")]
    [InlineData("", "General")]
    public void Built_in_categories(string fileName, string expected)
    {
        Assert.Equal(expected, Matcher().Match(fileName).Name);
    }

    [Fact]
    public void User_category_patterns_apply()
    {
        new CategoryRepository(_db.Database).Insert(new Category { Name = "Books", ParentId = Category.GeneralId, Extensions = "epub, mobi azw?" });
        var matcher = Matcher();

        Assert.Equal("Books", matcher.Match("novel.mobi").Name);
        Assert.Equal("Books", matcher.Match("novel.azw3").Name);
        Assert.Equal("Documents", matcher.Match("novel.epub").Name); // Documents comes first in display order
    }

    [Theory]
    [InlineData("r0*", "r00", true)]
    [InlineData("r0*", "r0", true)]
    [InlineData("r0*", "r10", false)]
    [InlineData("m?v", "m4v", true)]
    [InlineData("m?v", "mkv", true)]
    [InlineData("m?v", "mp4", false)]
    [InlineData("*", "anything", true)]
    [InlineData("ZIP", "zip", true)]
    public void Wildcards(string pattern, string extension, bool expected)
    {
        Assert.Equal(expected, CategoryMatcher.ExtensionMatches(pattern, extension));
    }

    [Fact]
    public void Save_folders_follow_category_then_defaults()
    {
        using var temp = new TempDirectory();
        var paths = AppPaths.ForRoot(temp.Path);
        var settings = new AppSettings();
        var repo = new CategoryRepository(_db.Database);

        Assert.Equal(paths.UserDownloadsDir, SaveLocationResolver.FolderFor(repo.Get(Category.GeneralId)!, settings, paths));
        Assert.Equal(Path.Combine(paths.UserDownloadsDir, "Video"), SaveLocationResolver.FolderFor(repo.Get(6)!, settings, paths));

        var custom = repo.Get(6)!;
        custom.DefaultSaveDir = temp.Combine("films");
        Assert.Equal(temp.Combine("films"), SaveLocationResolver.FolderFor(custom, settings, paths));

        settings.SaveTo.UseSameDirectoryForAllCategories = true;
        settings.SaveTo.SameDirectory = temp.Combine("all");
        Assert.Equal(temp.Combine("all"), SaveLocationResolver.FolderFor(custom, settings, paths));
    }
}
