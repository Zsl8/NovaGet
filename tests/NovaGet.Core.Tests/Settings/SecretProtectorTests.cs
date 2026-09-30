using NovaGet.Core.Security;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Settings;

public sealed class SecretProtectorTests
{
    [Fact]
    public void Default_protector_round_trips()
    {
        var protector = SecretProtector.CreateDefault();

        var protectedText = protector.Protect("päss wörd ✓");

        Assert.NotEqual("päss wörd ✓", protectedText);
        Assert.Equal("päss wörd ✓", protector.Unprotect(protectedText));
        Assert.Equal(string.Empty, protector.Protect(""));
        Assert.Equal(string.Empty, protector.Unprotect(""));
        Assert.Null(protector.Unprotect("garbage"));
    }

    [WindowsFact]
    public void Dpapi_output_is_prefixed_and_not_reversible_as_base64_text()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiSecretProtector();
        var protectedText = protector.Protect("secret");

        Assert.StartsWith("dpapi:", protectedText, StringComparison.Ordinal);
        var raw = Convert.FromBase64String(protectedText["dpapi:".Length..]);
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.Equal("secret", protector.Unprotect(protectedText));
    }
}
