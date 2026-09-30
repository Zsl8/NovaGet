using NovaGet.Core.Engine.Naming;

namespace NovaGet.Core.Tests.Naming;

public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData("normal.zip", "normal.zip")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\Windows\\System32\\x.dll", "x.dll")]
    [InlineData("C:\\temp\\a.txt", "a.txt")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("name.txt.  ", "name.txt")]
    [InlineData("trailing...", "trailing")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT9.log", "_LPT9.log")]
    [InlineData("com1 .txt", "_com1 .txt")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("..", "download")]
    [InlineData("", "download")]
    [InlineData("   ", "download")]
    [InlineData("???", "download")]
    [InlineData("ملف عربي.pdf", "ملف عربي.pdf")]
    public void Sanitizes(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void Long_names_are_cut_to_255_keeping_the_extension()
    {
        var result = FileNameSanitizer.Sanitize(new string('a', 400) + ".tar.gz");

        Assert.Equal(255, result.Length);
        Assert.EndsWith(".gz", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var emoji = "\U0001F600"; // two UTF-16 units
        var name = new string('a', 250) + string.Concat(Enumerable.Repeat(emoji, 10)) + ".txt";

        var result = FileNameSanitizer.Sanitize(name);

        Assert.True(result.Length <= 255);
        Assert.False(char.IsHighSurrogate(result[^5]));
        Assert.EndsWith(".txt", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MakeUnique_numbers_duplicates()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("d", "file.zip"), Path.Combine("d", "file (2).zip") };

        Assert.Equal("file (3).zip", FileNameSanitizer.MakeUnique("d", "file.zip", existing.Contains));
        Assert.Equal("other.zip", FileNameSanitizer.MakeUnique("d", "other.zip", existing.Contains));
    }

    [Theory]
    [InlineData(null, "https://h/dl/archive.tar.gz?x=1", null, "archive.tar.gz")]
    [InlineData(null, "https://h/files/my%20report.pdf", null, "my report.pdf")]
    [InlineData(null, "https://h/", null, "index.html")]
    [InlineData(null, "https://h/folder/", "text/html", "index.html")]
    [InlineData(null, "https://h/download", "application/zip", "download.zip")]
    [InlineData(null, "https://h/get?id=5", "application/pdf; charset=binary", "get.pdf")]
    [InlineData("attachment; filename=\"from-header.iso\"", "https://h/x.bin", null, "from-header.iso")]
    [InlineData("attachment; filename=\"../../x.exe\"", "https://h/y", null, "x.exe")]
    public void Resolver_follows_the_documented_order(string? disposition, string url, string? contentType, string expected)
    {
        Assert.Equal(expected, FileNameResolver.Resolve(disposition, new Uri(url), contentType));
    }
}
