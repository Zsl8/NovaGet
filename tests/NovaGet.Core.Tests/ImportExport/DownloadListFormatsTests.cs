using System.Text;
using NovaGet.Core.ImportExport;

namespace NovaGet.Core.Tests.ImportExport;

public sealed class DownloadListFormatsTests
{
    private static readonly ExportedDownload[] Sample =
    [
        new(new Uri("https://example.com/file.zip"))
        {
            Referrer = "https://example.com/page",
            Cookies = "a=b; session=xyz",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            FileName = "file.zip",
        },
        new(new Uri("ftp://files.example.com/pub/a%20b.iso")),
        new(new Uri("https://example.com/تقرير.pdf")) { FileName = "تقرير.pdf" },
    ];

    [Fact]
    public void Ef2_round_trips()
    {
        var text = DownloadListFormats.WriteEf2(Sample);
        var parsed = DownloadListFormats.ParseEf2(DownloadListFormats.Decode(DownloadListFormats.FileEncoding.GetBytes(text)));

        Assert.Equal(0, parsed.Skipped);
        Assert.Equal(Sample, parsed.Downloads);
    }

    [Fact]
    public void Ef2_is_written_like_other_download_managers_do()
    {
        var text = DownloadListFormats.WriteEf2([Sample[0]]);

        Assert.Equal(
            "<\r\nhttps://example.com/file.zip\r\nreferer: https://example.com/page\r\ncookie: a=b; session=xyz\r\n" +
            "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\nfilename: file.zip\r\n>\r\n",
            text);
    }

    [Fact]
    public void Ef2_from_other_programs_is_read_leniently()
    {
        const string text = """
            <
            http://example.com/one.zip
            Referer: http://example.com/
            COOKIE: x=1
            postdata: ignored=1
            username: someone
            >

            <
            https://example.com/two.exe
            >
            junk outside blocks
            <
            javascript:alert(1)
            >
            <
            >
            <
            https://example.com/never-closed.zip
            """;

        var parsed = DownloadListFormats.ParseEf2(text);

        Assert.Equal(["http://example.com/one.zip", "https://example.com/two.exe"], parsed.Downloads.Select(d => d.Url.AbsoluteUri));
        Assert.Equal("http://example.com/", parsed.Downloads[0].Referrer);
        Assert.Equal("x=1", parsed.Downloads[0].Cookies);
        Assert.Null(parsed.Downloads[1].Cookies);
        Assert.Equal(3, parsed.Skipped);
    }

    [Fact]
    public void A_referrer_must_be_a_web_address()
    {
        var parsed = DownloadListFormats.ParseEf2("<\nhttps://example.com/a.zip\nreferer: file:///etc/passwd\n>\n");

        Assert.Null(Assert.Single(parsed.Downloads).Referrer);
    }

    [Fact]
    public void Values_never_break_the_block_structure()
    {
        var text = DownloadListFormats.WriteEf2([new ExportedDownload(new Uri("https://example.com/a.zip")) { FileName = "evil\r\n>\r\n<\r\nhttps://attacker.example/x" }]);

        var parsed = DownloadListFormats.ParseEf2(text);

        Assert.Equal(["https://example.com/a.zip"], parsed.Downloads.Select(d => d.Url.AbsoluteUri));
    }

    [Fact]
    public void Text_lists_skip_blank_lines_and_comments()
    {
        const string text = "# my downloads\r\nhttps://example.com/a.zip\r\n\r\n   \r\n  ftp://example.com/b.iso  \r\n#https://example.com/c.zip\r\nnot an address\r\nfile:///C:/secret.txt\r\n";

        var parsed = DownloadListFormats.ParseText(text);

        Assert.Equal(["https://example.com/a.zip", "ftp://example.com/b.iso"], parsed.Downloads.Select(d => d.Url.AbsoluteUri));
        Assert.Equal(2, parsed.Skipped);
    }

    [Fact]
    public void Text_lists_round_trip()
    {
        var parsed = DownloadListFormats.ParseText(DownloadListFormats.WriteText(Sample));

        Assert.Equal(Sample.Select(s => s.Url), parsed.Downloads.Select(d => d.Url));
    }

    [Fact]
    public void Files_in_other_encodings_are_read()
    {
        const string text = "<\nhttps://example.com/a.zip\nfilename: résumé.zip\n>\n";

        Assert.Equal("résumé.zip", DownloadListFormats.ParseEf2(DownloadListFormats.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray())).Downloads[0].FileName);
        Assert.Equal("résumé.zip", DownloadListFormats.ParseEf2(DownloadListFormats.Decode(Encoding.Latin1.GetBytes(text))).Downloads[0].FileName);
        Assert.Equal("résumé.zip", DownloadListFormats.ParseEf2(DownloadListFormats.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray())).Downloads[0].FileName);
    }
}
