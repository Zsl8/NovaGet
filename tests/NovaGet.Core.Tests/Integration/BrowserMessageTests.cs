using System.Text.Json;
using NovaGet.Core.Integration;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Tests.Integration;

public sealed class BrowserMessageTests
{
    private static BrowserMessage? Validate(string json, out string? error) =>
        BrowserMessageValidator.Validate(JsonDocument.Parse(json).RootElement, out error);

    [Fact]
    public void A_download_is_parsed()
    {
        var message = Validate("""
            {"type":"download","url":"https://example.com/a.zip","finalUrl":"https://cdn.example.com/a.zip",
             "referrer":"https://example.com/page","fileName":"C:\\fake\\a.zip","fileSize":1234,"mime":"application/zip",
             "cookies":"sid=1","userAgent":"UA","forced":true,"extra":{"ignored":true}}
            """, out var error);

        var download = Assert.IsType<BrowserDownload>(message);
        Assert.Null(error);
        Assert.Equal("https://example.com/a.zip", download.Url.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/a.zip", download.FinalUrl!.AbsoluteUri);
        Assert.Equal(1234, download.FileSize);
        Assert.Equal("sid=1", download.Request.Cookies);
        Assert.Equal("https://example.com/page", download.Request.Referrer);
        Assert.True(download.Forced);
    }

    [Theory]
    [InlineData("""{"type":"download","url":"javascript:alert(1)"}""")]
    [InlineData("""{"type":"download","url":"file:///C:/Windows/win.ini"}""")]
    [InlineData("""{"type":"download","url":"data:text/plain,hi"}""")]
    [InlineData("""{"type":"download","url":"relative/path.zip"}""")]
    [InlineData("""{"type":"download"}""")]
    [InlineData("""{"type":"download","url":42}""")]
    [InlineData("""{"type":"download","url":"https://e.com/a","fileSize":"big"}""")]
    [InlineData("""{"type":"download","url":"https://e.com/a","fileName":"a\u0000b"}""")]
    [InlineData("""{"type":"launchMissiles"}""")]
    [InlineData("""{"url":"https://e.com/a"}""")]
    [InlineData("""["download"]""")]
    [InlineData("""{"type":"links","links":"nope"}""")]
    [InlineData("""{"type":"media"}""")]
    [InlineData("""{"type":"cookies","url":"https://e.com/"}""")]
    [InlineData("""{"type":"cookies","url":"file:///C:/x","cookies":"a=b"}""")]
    public void Bad_messages_are_rejected(string json)
    {
        Assert.Null(Validate(json, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Cookies_for_a_requested_login_are_parsed()
    {
        var cookies = Assert.IsType<BrowserCookies>(Validate("""{"type":"cookies","url":"https://shop.example.com/account","cookies":"sid=1; theme=dark"}""", out _));

        Assert.Equal("https://shop.example.com/account", cookies.Url.AbsoluteUri);
        Assert.Equal("sid=1; theme=dark", cookies.Cookies);
        Assert.IsType<BrowserSimpleMessage>(Validate("""{"type":"openOptions"}""", out _));
    }

    [Fact]
    public void Oversized_fields_are_rejected()
    {
        var url = "https://example.com/" + new string('a', BrowserMessageValidator.MaxUrlLength);
        Assert.Null(Validate($$"""{"type":"download","url":"{{url}}"}""", out _));

        var cookies = new string('c', BrowserMessageValidator.MaxCookieLength + 1);
        Assert.Null(Validate($$"""{"type":"download","url":"https://e.com/a","cookies":"{{cookies}}"}""", out _));
    }

    [Fact]
    public void A_bad_referrer_is_dropped_not_fatal()
    {
        var download = Assert.IsType<BrowserDownload>(Validate("""{"type":"download","url":"ftp://e.com/a.iso","referrer":"about:blank","fileSize":-5}""", out _));

        Assert.Null(download.Request.Referrer);
        Assert.Equal(-1, download.FileSize);
    }

    [Fact]
    public void Links_skip_unusable_entries_and_duplicates()
    {
        var links = Assert.IsType<BrowserLinks>(Validate("""
            {"type":"links","pageUrl":"https://example.com/","pageTitle":"Page",
             "links":[{"url":"https://example.com/a.zip","text":"A"},{"url":"mailto:x@y.z"},{"url":"javascript:void(0)"},
                      {"url":"https://example.com/a.zip","text":"again"},{"url":"https://example.com/b.pdf"},42]}
            """, out _));

        Assert.Equal(["https://example.com/a.zip", "https://example.com/b.pdf"], links.Links.Select(l => l.Url.AbsoluteUri));
        Assert.Equal("A", links.Links[0].Text);
    }

    [Fact]
    public void Too_many_links_are_rejected()
    {
        var items = string.Join(',', Enumerable.Range(0, BrowserMessageValidator.MaxLinks + 1).Select(i => $$"""{"url":"https://e.com/{{i}}"}"""));
        Assert.Null(Validate($$"""{"type":"links","links":[{{items}}]}""", out var error));
        Assert.Contains("Too many", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_follow_options_and_the_browser_checklist()
    {
        var settings = new AppSettings();
        settings.General.IntegratedBrowsers = ["chrome"];
        settings.General.PreventCaptureKey = CaptureModifier.Alt;
        settings.General.ForceCaptureKey = CaptureModifier.CtrlShift;
        settings.FileTypes.AutoCaptureExtensions = "zip *.ISO r0*";

        var chrome = ExtensionSettings.From(settings, "chrome");
        var edge = ExtensionSettings.From(settings, "edge");

        Assert.True(chrome.Enabled);
        Assert.True(chrome.Capture);
        Assert.False(edge.Enabled);
        Assert.False(edge.Capture);
        Assert.False(edge.MenuDownloadLink);
        Assert.Equal(["zip", "iso", "r0*"], chrome.FileTypes);
        Assert.Equal("alt", chrome.PreventKey);
        Assert.Equal("ctrlShift", chrome.ForceKey);
        Assert.Equal("topRight", chrome.PanelPosition);

        settings.General.UseAdvancedBrowserIntegration = false;
        var menusOnly = ExtensionSettings.From(settings, "chrome");
        Assert.False(menusOnly.Capture);
        Assert.True(menusOnly.MenuDownloadLink);
        Assert.NotEqual(chrome.Revision, menusOnly.Revision);
        Assert.Equal(menusOnly.Revision, ExtensionSettings.From(settings, "chrome").Revision);
    }

    [Theory]
    [InlineData("https://example.com/files/setup.EXE", true)]
    [InlineData("https://example.com/files/movie.part.r01", true)]
    [InlineData("https://example.com/index.html", false)]
    [InlineData("https://download.windowsupdate.com/x.cab", false)]
    [InlineData("https://a.update.microsoft.com/kb.msu", false)]
    [InlineData("https://example.com/private/report.pdf", false)]
    [InlineData("https://example.com/noextension", false)]
    public void Capture_rules(string url, bool capture)
    {
        var rules = new CaptureRules(new FileTypeSettings
        {
            ExcludedAddresses = ["https://example.com/private/*"],
        });

        Assert.Equal(capture, rules.ShouldCapture(new Uri(url)));
    }

    [Fact]
    public void Links_are_found_in_text()
    {
        var text = "Get it from https://example.com/a.zip, or (ftp://mirror.example/a.zip). Not javascript:x or www.example.com.\n" +
                   "<a href=\"https://example.com/b.iso\">b</a> https://example.com/a.zip again";

        Assert.Equal(
            new[] { "https://example.com/a.zip", "ftp://mirror.example/a.zip", "https://example.com/b.iso" },
            LinkExtractor.ExtractUrls(text).Select(u => u.AbsoluteUri));
        Assert.Equal("https://example.com/x.zip", LinkExtractor.SingleUrl("  https://example.com/x.zip \r\n")!.AbsoluteUri);
        Assert.Null(LinkExtractor.SingleUrl("see https://example.com/x.zip"));
        Assert.Equal("https://example.com/s", LinkExtractor.ParseInternetShortcut("[InternetShortcut]\r\nURL=https://example.com/s\r\n")!.AbsoluteUri);
        Assert.Equal(2, LinkExtractor.ParseMozUrl("https://a.example/1\nOne\r\nhttps://a.example/2\nTwo").Count);
    }
}
