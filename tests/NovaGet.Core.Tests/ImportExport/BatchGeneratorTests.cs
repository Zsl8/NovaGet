using NovaGet.Core.ImportExport;

namespace NovaGet.Core.Tests.ImportExport;

public sealed class BatchGeneratorTests
{
    [Fact]
    public void Numbers_are_padded_to_the_wildcard_size()
    {
        var batch = new BatchGenerator { Template = "http://www.site.com/images/img*.jpg", From = "1", To = "150", WildcardSize = 3 };

        Assert.Null(batch.Error);
        Assert.Equal(150, batch.Count);
        Assert.Equal("http://www.site.com/images/img001.jpg", batch.First);
        Assert.Equal("http://www.site.com/images/img150.jpg", batch.Last);
        var all = batch.Generate().ToList();
        Assert.Equal(150, all.Count);
        Assert.Equal("http://www.site.com/images/img042.jpg", all[41]);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Numbers_wider_than_the_wildcard_are_not_cut()
    {
        var batch = new BatchGenerator { Template = "https://a.example/p*.zip", From = "98", To = "101", WildcardSize = 2 };

        Assert.Equal(["https://a.example/p98.zip", "https://a.example/p99.zip", "https://a.example/p100.zip", "https://a.example/p101.zip"], batch.Generate());
    }

    [Fact]
    public void Ranges_may_count_down()
    {
        var batch = new BatchGenerator { Template = "https://a.example/*.bin", From = "3", To = "1" };

        Assert.Equal(["https://a.example/3.bin", "https://a.example/2.bin", "https://a.example/1.bin"], batch.Generate());
    }

    [Fact]
    public void Letters_replace_every_asterisk()
    {
        var batch = new BatchGenerator { Template = "ftp://files.example.com/*/part-*.rar", Mode = BatchMode.Letters, From = "a", To = "d" };

        Assert.Equal(4, batch.Count);
        Assert.Equal("ftp://files.example.com/a/part-a.rar", batch.First);
        Assert.Equal("ftp://files.example.com/d/part-d.rar", batch.Last);
        Assert.Equal("ftp://files.example.com/c/part-c.rar", batch.Generate().ElementAt(2));
    }

    [Theory]
    [InlineData("http://example.com/file.jpg", BatchMode.Numbers, "1", "5", 1)] // no asterisk
    [InlineData("file:///C:/*.txt", BatchMode.Numbers, "1", "5", 1)]
    [InlineData("javascript:*", BatchMode.Numbers, "1", "5", 1)]
    [InlineData("http://example.com/*.jpg", BatchMode.Numbers, "x", "5", 1)]
    [InlineData("http://example.com/*.jpg", BatchMode.Numbers, "-1", "5", 1)]
    [InlineData("http://example.com/*.jpg", BatchMode.Numbers, "1", "5", 11)]
    [InlineData("http://example.com/*.jpg", BatchMode.Numbers, "0", "100000", 1)] // 100,001 addresses
    [InlineData("http://example.com/*.jpg", BatchMode.Letters, "a", "zz", 1)]
    [InlineData("http://example.com/*.jpg", BatchMode.Letters, "a", "Z", 1)]
    [InlineData("http://example.com/*.jpg", BatchMode.Letters, "1", "9", 1)]
    public void Unusable_input_is_explained(string template, BatchMode mode, string from, string to, int size)
    {
        var batch = new BatchGenerator { Template = template, Mode = mode, From = from, To = to, WildcardSize = size };

        Assert.NotNull(batch.Error);
        Assert.Equal(0, batch.Count);
        Assert.Null(batch.First);
        Assert.Throws<InvalidOperationException>(() => batch.Generate().ToList());
    }

    [Fact]
    public void The_largest_batch_is_allowed()
    {
        var batch = new BatchGenerator { Template = "http://example.com/*.jpg", From = "1", To = "100000" };

        Assert.Null(batch.Error);
        Assert.Equal(BatchGenerator.MaxCount, batch.Count);
    }
}
