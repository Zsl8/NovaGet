using NovaGet.App.Services;

namespace NovaGet.App.Tests;

public sealed class NetworkServicesTests
{
    [Theory]
    [InlineData("proxy.corp:8080", "https", "http://proxy.corp:8080/")]
    [InlineData("a.corp:80;b.corp:81", "http", "http://a.corp/")]
    [InlineData("http=a.corp:80;https=b.corp:443", "https", "http://b.corp:443/")]
    [InlineData("http=a.corp:80", "https", null)]
    [InlineData("PROXY c.corp:3128; DIRECT", "http", "http://c.corp:3128/")]
    [InlineData("DIRECT", "http", null)]
    [InlineData("", "http", null)]
    public void Pac_answers_are_parsed(string list, string scheme, string? expected)
    {
        Assert.Equal(expected, WinHttpPacResolver.ParseProxyList(list, scheme)?.AbsoluteUri);
    }
}
