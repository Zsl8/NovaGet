using System.Globalization;

namespace NovaGet.Core.ImportExport;

public enum BatchMode
{
    Numbers,
    Letters,
}

/// <summary>Why a batch can't be generated (the app shows a localized message for each).</summary>
public enum BatchError
{
    NoAsterisk,
    BadAddress,
    BadNumbers,
    BadLetters,
    MixedCase,
    BadWildcardSize,
    TooMany,
}

/// <summary>
/// "Add batch download" (section 13.1): every <c>*</c> in the address is replaced by a number (zero-padded to the
/// wildcard size) or a letter, from <see cref="From"/> to <see cref="To"/> (either direction).
/// </summary>
public sealed record BatchGenerator
{
    /// <summary>More than this is almost certainly a typo in From/To.</summary>
    public const int MaxCount = 100_000;

    public const int MaxWildcardSize = 10;

    public required string Template { get; init; }

    public BatchMode Mode { get; init; } = BatchMode.Numbers;

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>Numbers are padded with zeros to this many digits (1 = no padding).</summary>
    public int WildcardSize { get; init; } = 1;

    /// <summary>Why the input can't be used, or null when it is fine.</summary>
    public BatchError? Error => Validate();

    public long Count => Error is null ? Range().Count : 0;

    public string? First => Error is null ? Format(Range().Start) : null;

    public string? Last => Error is null ? Format(Range().End) : null;

    public IEnumerable<string> Generate()
    {
        if (Error is { } error)
        {
            throw new InvalidOperationException($"The batch can't be generated: {error}.");
        }

        var (start, end, count) = Range();
        var step = end >= start ? 1 : -1;
        for (long i = 0; i < count; i++)
        {
            yield return Format(start + (i * step));
        }
    }

    private string Format(long value)
    {
        var text = Mode == BatchMode.Letters
            ? ((char)value).ToString()
            : value.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Clamp(WildcardSize, 1, MaxWildcardSize), '0');
        return Template.Trim().Replace("*", text, StringComparison.Ordinal);
    }

    private (long Start, long End, long Count) Range()
    {
        long start;
        long end;
        if (Mode == BatchMode.Letters)
        {
            start = From.Trim()[0];
            end = To.Trim()[0];
        }
        else
        {
            start = long.Parse(From.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
            end = long.Parse(To.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return (start, end, Math.Abs(end - start) + 1);
    }

    private BatchError? Validate()
    {
        var template = Template?.Trim() ?? string.Empty;
        if (!template.Contains('*', StringComparison.Ordinal))
        {
            return BatchError.NoAsterisk;
        }

        if (!Uri.TryCreate(template.Replace("*", "1", StringComparison.Ordinal), UriKind.Absolute, out var sample)
            || sample.Scheme is not ("http" or "https" or "ftp" or "ftps"))
        {
            return BatchError.BadAddress;
        }

        if (Mode == BatchMode.Letters)
        {
            var from = From?.Trim() ?? string.Empty;
            var to = To?.Trim() ?? string.Empty;
            if (from.Length != 1 || to.Length != 1 || !IsAsciiLetter(from[0]) || !IsAsciiLetter(to[0]))
            {
                return BatchError.BadLetters;
            }

            if (char.IsUpper(from[0]) != char.IsUpper(to[0]))
            {
                return BatchError.MixedCase;
            }

            return null;
        }

        if (!long.TryParse(From?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(To?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            || start > 9_999_999_999 || end > 9_999_999_999)
        {
            return BatchError.BadNumbers;
        }

        if (WildcardSize is < 1 or > MaxWildcardSize)
        {
            return BatchError.BadWildcardSize;
        }

        return Math.Abs(end - start) + 1 > MaxCount ? BatchError.TooMany : null;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
