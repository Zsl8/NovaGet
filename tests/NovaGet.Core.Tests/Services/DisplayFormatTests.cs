using System.Globalization;
using NovaGet.Core.Formatting;

namespace NovaGet.Core.Tests.Services;

public sealed class DisplayFormatTests
{
    private static readonly CultureInfo s_en = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "0 Bytes")]
    [InlineData(1023, "1023 Bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(89_128_960, "85 MB")]
    [InlineData(149_274_460, "142.36 MB")]
    [InlineData(5_057_323_008, "4.71 GB")]
    public void Sizes(long bytes, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Size(bytes, culture: s_en));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(500, "500 Bytes/sec")]
    [InlineData(13_002_342, "12.4 MB/sec")]
    [InlineData(1024 * 300, "300 KB/sec")]
    public void Rates(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Rate(bytesPerSecond, culture: s_en));
    }

    [Fact]
    public void Rate_with_three_decimals_for_the_progress_dialog()
    {
        Assert.Equal("12.418 MB/sec", DisplayFormat.Rate(12.418 * 1024 * 1024, 3, s_en));
    }

    [Theory]
    [InlineData(45, false, "45 sec")]
    [InlineData(243, false, "4 min 3 sec")]
    [InlineData(243, true, "4 min")]
    [InlineData(180, false, "3 min")]
    [InlineData(3900, false, "1 hour 5 min")]
    [InlineData(3900, true, "1 hour")]
    [InlineData(7200, false, "2 hours")]
    [InlineData(183_600, false, "2 days 3 hours")]
    [InlineData(86_400, false, "1 day")]
    [InlineData(0.2, false, "1 sec")]
    public void Durations(double seconds, bool compact, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Duration(TimeSpan.FromSeconds(seconds), compact));
    }

    [Fact]
    public void Unknown_duration_is_empty()
    {
        Assert.Equal(string.Empty, DisplayFormat.Duration(null));
    }

    [Theory]
    [InlineData(375, 1000, "37.5%")]
    [InlineData(1, 3, "33.33%")]
    [InlineData(10, 10, "100%")]
    [InlineData(10, -1, "")]
    public void Percentages(long downloaded, long size, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Percent(downloaded, size, s_en));
    }
}
