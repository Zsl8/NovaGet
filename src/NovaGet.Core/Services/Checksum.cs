using System.Security.Cryptography;

namespace NovaGet.Core.Services;

/// <summary>File checksums for the Properties dialog's "Verify".</summary>
public static class Checksum
{
    public const string Md5 = "MD5";
    public const string Sha1 = "SHA-1";
    public const string Sha256 = "SHA-256";

    public static IReadOnlyList<string> Algorithms { get; } = [Md5, Sha1, Sha256];

    /// <summary>Lower-case hex digest of the file. Reports progress from 0 to 1.</summary>
    public static async Task<string> ComputeAsync(string path, string algorithm, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(NameOf(algorithm));
        var buffer = new byte[1024 * 1024];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(length > 0 ? (double)done / length : 1);
        }

        progress?.Report(1);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Compares an expected value as users paste it (any case, spaces, "sha256:" prefixes) with a computed hex digest.</summary>
    public static bool Matches(string? expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var cleaned = new string([.. expected.Where(Uri.IsHexDigit)]);
        var colon = expected.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon < 12)
        {
            cleaned = new string([.. expected[(colon + 1)..].Where(Uri.IsHexDigit)]);
        }

        return string.Equals(cleaned, actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Guesses the algorithm from an expected value's length (32/40/64 hex digits).</summary>
    public static string? GuessAlgorithm(string? expected) =>
        new string([.. (expected ?? string.Empty).Where(Uri.IsHexDigit)]).Length switch
        {
            32 => Md5,
            40 => Sha1,
            64 => Sha256,
            _ => null,
        };

    private static HashAlgorithmName NameOf(string algorithm) => algorithm.ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal) switch
    {
        "MD5" => HashAlgorithmName.MD5,
        "SHA1" => HashAlgorithmName.SHA1,
        "SHA256" => HashAlgorithmName.SHA256,
        _ => throw new ArgumentException($"Unsupported checksum algorithm {algorithm}.", nameof(algorithm)),
    };
}
