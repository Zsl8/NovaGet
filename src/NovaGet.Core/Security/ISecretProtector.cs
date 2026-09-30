namespace NovaGet.Core.Security;

/// <summary>Encrypts secrets (passwords, cookies) at rest.</summary>
public interface ISecretProtector
{
    /// <summary>Returns an opaque string safe to persist. Empty input yields empty output.</summary>
    string Protect(string? plainText);

    /// <summary>Reverses <see cref="Protect"/>. Returns empty for empty input and null if the data can't be decrypted.</summary>
    string? Unprotect(string? protectedText);
}
