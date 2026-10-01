using System.Text;

namespace NovaGet.Core.ImportExport;

/// <summary>A download as exported or imported (EF2 block or text-list line).</summary>
public sealed record ExportedDownload(Uri Url)
{
    public string? Referrer { get; init; }

    public string? Cookies { get; init; }

    public string? UserAgent { get; init; }

    public string? FileName { get; init; }
}

/// <summary>What an import found: the downloads and how many entries were skipped as unusable.</summary>
public sealed record ImportResult(IReadOnlyList<ExportedDownload> Downloads, int Skipped);

/// <summary>Reading and writing download lists: IDM-compatible EF2 and plain text (section 13.3).</summary>
public static class DownloadListFormats
{
    /// <summary>Lists larger than this are refused.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public const int MaxEntries = 100_000;

    public const int MaxUrlLength = 8192;

    public const int MaxValueLength = 64 * 1024;

    /// <summary>
    /// EF2: blocks between a line <c>&lt;</c> and a line <c>&gt;</c>; the first line is the address, then
    /// <c>name: value</c> lines (referer, cookie, User-Agent, filename; other names are ignored).
    /// </summary>
    public static ImportResult ParseEf2(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var downloads = new List<ExportedDownload>();
        var skipped = 0;
        List<string>? block = null;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line == "<")
            {
                if (block is not null)
                {
                    skipped++; // the previous block was never closed
                }

                block = [];
            }
            else if (line == ">")
            {
                if (block is null)
                {
                    continue;
                }

                if (FromBlock(block) is { } download && downloads.Count < MaxEntries)
                {
                    downloads.Add(download);
                }
                else
                {
                    skipped++;
                }

                block = null;
            }
            else if (block is not null && line.Length > 0)
            {
                block.Add(line);
            }
        }

        if (block is not null)
        {
            skipped++;
        }

        return new ImportResult(downloads, skipped);
    }

    public static string WriteEf2(IEnumerable<ExportedDownload> downloads)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        var text = new StringBuilder();
        foreach (var download in downloads)
        {
            text.Append("<\r\n").Append(download.Url.AbsoluteUri).Append("\r\n");
            AppendField(text, "referer", download.Referrer);
            AppendField(text, "cookie", download.Cookies);
            AppendField(text, "User-Agent", download.UserAgent);
            AppendField(text, "filename", download.FileName);
            text.Append(">\r\n");
        }

        return text.ToString();
    }

    /// <summary>One address per line; blank lines and lines starting with <c>#</c> are skipped.</summary>
    public static ImportResult ParseText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var downloads = new List<ExportedDownload>();
        var skipped = 0;
        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (ParseUrl(line) is { } url && downloads.Count < MaxEntries)
            {
                downloads.Add(new ExportedDownload(url));
            }
            else
            {
                skipped++;
            }
        }

        return new ImportResult(downloads, skipped);
    }

    public static string WriteText(IEnumerable<ExportedDownload> downloads)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        return string.Concat(downloads.Select(d => d.Url.AbsoluteUri + "\r\n"));
    }

    /// <summary>
    /// Decodes a list file: UTF-8 or UTF-16 by its byte order mark, else UTF-8 when valid, else the ANSI code page
    /// older download managers wrote (Windows-1252).
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The file is too large to be a download list.");
        }

        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>Encoding for written lists: UTF-8 without a byte order mark (plain ASCII for typical addresses).</summary>
    public static Encoding FileEncoding { get; } = new UTF8Encoding(false);

    private static ExportedDownload? FromBlock(List<string> lines)
    {
        if (lines.Count == 0 || ParseUrl(lines[0]) is not { } url)
        {
            return null;
        }

        var download = new ExportedDownload(url);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0 || value.Length > MaxValueLength || value.Contains('\0', StringComparison.Ordinal))
            {
                continue;
            }

            download = name switch
            {
                "referer" or "referrer" => ParseUrl(value) is { } referrer && referrer.Scheme is "http" or "https" ? download with { Referrer = referrer.AbsoluteUri } : download,
                "cookie" or "cookies" => download with { Cookies = value },
                "user-agent" => download with { UserAgent = value },
                "filename" => download with { FileName = value },
                _ => download,
            };
        }

        return download;
    }

    private static Uri? ParseUrl(string text) =>
        text.Length <= MaxUrlLength && Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" or "ftp" or "ftps"
            ? url
            : null;

    private static void AppendField(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            // A value never spans lines in EF2.
            text.Append(name).Append(": ").Append(value.ReplaceLineEndings(" ").Trim()).Append("\r\n");
        }
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
