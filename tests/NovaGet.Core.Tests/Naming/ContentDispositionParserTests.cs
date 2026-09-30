using System.Text;
using NovaGet.Core.Engine.Naming;

namespace NovaGet.Core.Tests.Naming;

public sealed class ContentDispositionParserTests
{
    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=report.pdf", "report.pdf")]
    [InlineData("inline;filename=\"a; b.txt\"", "a; b.txt")]
    [InlineData("attachment; filename=\"quote\\\"d.txt\"", "quote\"d.txt")]
    [InlineData("filename=bare.zip", "bare.zip")]
    [InlineData("attachment; filename=\"my%20file.zip\"", "my file.zip")]
    [InlineData("attachment; filename*=UTF-8''na%C3%AFve%20file.txt", "naïve file.txt")]
    [InlineData("attachment; filename*=iso-8859-1'en'%A3%20rates.txt", "£ rates.txt")]
    [InlineData("attachment; filename=\"=?UTF-8?B?w6nDqMOgLnR4dA==?=\"", "éèà.txt")]
    [InlineData("attachment; filename=\"=?UTF-8?Q?caf=C3=A9_menu.pdf?=\"", "café menu.pdf")]
    public void Extracts_file_names(string header, string expected)
    {
        Assert.Equal(expected, ContentDispositionParser.GetFileName(header));
    }

    [Fact]
    public void Extended_value_wins_over_plain_filename()
    {
        var header = "attachment; filename=\"fallback.zip\"; filename*=UTF-8''%D9%85%D9%84%D9%81.zip";

        Assert.Equal("ملف.zip", ContentDispositionParser.GetFileName(header));
    }

    [Fact]
    public void Arabic_rfc5987_name_is_decoded()
    {
        var name = "تقرير سنوي.pdf";
        var encoded = Uri.EscapeDataString(name);

        Assert.Equal(name, ContentDispositionParser.GetFileName($"attachment; filename*=UTF-8''{encoded}"));
    }

    [Fact]
    public void Raw_utf8_bytes_read_as_latin1_are_repaired()
    {
        var mojibake = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("ملف.zip"));

        Assert.Equal("ملف.zip", ContentDispositionParser.GetFileName($"attachment; filename=\"{mojibake}\""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("attachment")]
    [InlineData("inline; size=10")]
    public void Returns_null_without_a_name(string? header)
    {
        Assert.Null(ContentDispositionParser.GetFileName(header));
    }
}
