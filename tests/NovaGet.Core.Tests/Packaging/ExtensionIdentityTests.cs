using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>The installer only lets the pinned extension IDs talk to the native host, so they must all agree.</summary>
public sealed partial class ExtensionIdentityTests
{
    private static JsonElement Ids() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("browser-extension", "extension-ids.json"))).RootElement;

    [Fact]
    public void Chromium_id_is_derived_from_the_public_key()
    {
        var ids = Ids();
        var key = Convert.FromBase64String(ids.GetProperty("chromiumPublicKey").GetString()!);

        // Chromium: first 128 bits of SHA-256(DER public key), hex digits mapped 0-f -> a-p.
        var hex = Convert.ToHexString(SHA256.HashData(key))[..32].ToLowerInvariant();
        var expected = new string(hex.Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))).ToArray());

        Assert.Equal(expected, ids.GetProperty("chromiumId").GetString());
    }

    [Fact]
    public void Installer_defines_match_extension_ids()
    {
        var ids = Ids();
        var iss = File.ReadAllText(RepoPaths.Combine("installer", "extension-ids.iss"));

        Assert.Equal(ids.GetProperty("chromiumId").GetString(), Define(iss, "ChromeExtensionId"));
        Assert.Equal(ids.GetProperty("firefoxId").GetString(), Define(iss, "FirefoxExtensionId"));
    }

    private static string Define(string iss, string name)
    {
        var match = DefineRegex().Matches(iss).FirstOrDefault(m => m.Groups[1].Value == name);
        Assert.NotNull(match);
        return match.Groups[2].Value;
    }

    [GeneratedRegex("""#define\s+(\w+)\s+"([^"]*)"\s*""")]
    private static partial Regex DefineRegex();
}
