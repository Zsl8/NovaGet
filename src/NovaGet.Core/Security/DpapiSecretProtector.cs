using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NovaGet.Core.Security;

/// <summary>DPAPI (CurrentUser scope). Values are prefixed with <c>dpapi:</c> and base64-encoded.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("NovaGet.Secrets.v1");

    public string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), s_entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return string.Empty;
        }

        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText[Prefix.Length..]), s_entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
