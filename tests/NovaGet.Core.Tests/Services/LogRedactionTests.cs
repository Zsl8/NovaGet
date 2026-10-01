using NovaGet.Core.Diagnostics;

namespace NovaGet.Core.Tests.Services;

public sealed class LogRedactionTests
{
    [Theory]
    [InlineData("Downloading ftp://alice:s3cret@files.example.com/a.zip", "Downloading ftp://alice:***@files.example.com/a.zip")]
    [InlineData("args: [\"/d\", \"https://bob:p%40ss@example.com/x\"]", "args: [\"/d\", \"https://bob:***@example.com/x\"]")]
    [InlineData("HTTPS://U:P@HOST/ and ftps://v:q@other/", "HTTPS://U:***@HOST/ and ftps://v:***@other/")]
    [InlineData("ftp://anonymous@example.com/pub", "ftp://anonymous@example.com/pub")]
    [InlineData("mail me at someone@example.com", "mail me at someone@example.com")]
    [InlineData("https://example.com/path?a=b@c", "https://example.com/path?a=b@c")]
    public void Passwords_in_addresses_are_masked(string text, string expected) =>
        Assert.Equal(expected, LogRedaction.Redact(text));
}
