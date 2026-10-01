using System.Net;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Tests.Data;
using NovaGet.Data.Repositories;

namespace NovaGet.Core.Tests.Engine;

public sealed class HttpAuthTests : IAsyncLifetime
{
    private EngineHarness _h = null!;

    public async Task InitializeAsync() => _h = await EngineHarness.CreateAsync(o => o with { MaxConnections = 4 });

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Theory]
    [InlineData("Basic")]
    [InlineData("Digest")]
    public async Task The_download_login_answers_the_challenge(string scheme)
    {
        var file = _h.Server.AddFile("/private/report.bin", 900_000, seed: 4);
        file.RequireAuth = (scheme, "alice", "pa:ss wörd");
        var id = _h.Add(file, configure: d =>
        {
            d.AuthUser = "alice";
            d.AuthPassword = "pa:ss wörd";
        });

        var result = await _h.RunAsync(id);

        Assert.Equal(DownloadStatus.Completed, result.Status);
        Assert.Equal(file.Sha256(), EngineHarness.Sha256OfFile(_h.Get(id).FullPath));
        Assert.Contains(_h.Server.RequestsFor(file), r => r.Headers.TryGetValue("Authorization", out var value) && value.StartsWith(scheme, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_login_the_download_fails_with_authentication_required()
    {
        var file = _h.Server.AddFile("/private/locked.bin", 10_000);
        file.RequireAuth = ("Basic", "alice", "pw");

        var result = await _h.RunAsync(_h.Add(file));

        Assert.Equal(DownloadStatus.Error, result.Status);
        Assert.Equal(DownloadErrorKind.AuthenticationRequired, result.ErrorKind);
    }

    [Fact]
    public async Task A_wrong_password_fails()
    {
        var file = _h.Server.AddFile("/private/wrong.bin", 10_000);
        file.RequireAuth = ("Digest", "alice", "pw");

        var result = await _h.RunAsync(_h.Add(file, configure: d =>
        {
            d.AuthUser = "alice";
            d.AuthPassword = "nope";
        }));

        Assert.Equal(DownloadErrorKind.AuthenticationRequired, result.ErrorKind);
    }

    [Fact]
    public async Task A_matching_site_login_is_used_automatically()
    {
        using var db = new TestDatabase();
        var logins = new SiteLoginRepository(db.Database, db.Protector);
        logins.Insert(new SiteLogin { UrlPattern = "127.0.0.1", User = "bob", Password = "hunter2" });
        _h.SiteCredentials = new SiteCredentials(logins);
        _h.RestartEngine();
        var file = _h.Server.AddFile("/members/file.bin", 300_000, seed: 8);
        file.RequireAuth = ("Digest", "bob", "hunter2");

        var result = await _h.RunAsync(_h.Add(file));

        Assert.Equal(DownloadStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Address_logins_are_used()
    {
        var file = _h.Server.AddFile("/private/inline.bin", 50_000);
        file.RequireAuth = ("Basic", "carol", "p@ss");
        var url = new UriBuilder(_h.Server.UrlFor(file)) { UserName = "carol", Password = Uri.EscapeDataString("p@ss") }.Uri;

        var result = await _h.RunAsync(_h.Add(url, "inline.bin"));

        Assert.Equal(DownloadStatus.Completed, result.Status);
    }

    [Theory]
    [InlineData("example.com", "https://example.com/a.zip", true)]
    [InlineData("example.com", "https://www.example.com/a.zip", false)]
    [InlineData("*.example.com", "https://files.example.com/a.zip", true)]
    [InlineData("*.example.com", "https://example.com/a.zip", true)]
    [InlineData("*.example.com", "https://example.com.evil.net/a.zip", false)]
    [InlineData("https://example.com/private/*", "https://example.com/private/x/a.zip", true)]
    [InlineData("https://example.com/private/*", "https://example.com/public/a.zip", false)]
    [InlineData("example.com/files/*", "ftp://example.com/files/a.zip", true)]
    [InlineData("EXAMPLE.com", "http://example.COM/", true)]
    public void Site_login_patterns(string pattern, string url, bool matches)
    {
        Assert.Equal(matches, SiteCredentials.Matches(pattern, new Uri(url)));
    }

    [Fact]
    public void The_most_specific_site_login_wins()
    {
        using var db = new TestDatabase();
        var logins = new SiteLoginRepository(db.Database, db.Protector);
        logins.Insert(new SiteLogin { UrlPattern = "*.example.com", User = "general", Password = "a" });
        logins.Insert(new SiteLogin { UrlPattern = "https://files.example.com/vip/*", User = "vip", Password = "b" });
        var credentials = new SiteCredentials(logins);

        Assert.Equal("vip", credentials.Find(new Uri("https://files.example.com/vip/a.zip"))!.UserName);
        Assert.Equal("general", credentials.Find(new Uri("https://files.example.com/other/a.zip"))!.UserName);
        Assert.Null(credentials.Find(new Uri("https://example.org/")));
    }
}
