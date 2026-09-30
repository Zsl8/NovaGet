using System.Text;

namespace NovaGet.Core.Security;

public static class SecretProtector
{
    /// <summary>DPAPI on Windows; an obfuscating fallback elsewhere (tests and dev builds only).</summary>
    public static ISecretProtector CreateDefault() =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new DevOnlySecretProtector();
}

/// <summary>
/// Non-Windows fallback so the engine and data layer can run in cross-platform tests.
/// It is NOT encryption; the shipping product always uses <see cref="DpapiSecretProtector"/>.
/// </summary>
public sealed class DevOnlySecretProtector : ISecretProtector
{
    private const string Prefix = "dev:";

    public string Protect(string? plainText) =>
        string.IsNullOrEmpty(plainText) ? string.Empty : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

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
            return Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[Prefix.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
