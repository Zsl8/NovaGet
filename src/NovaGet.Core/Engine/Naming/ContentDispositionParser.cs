using System.Text;

namespace NovaGet.Core.Engine.Naming;

/// <summary>
/// Extracts the file name from a Content-Disposition header. Prefers <c>filename*</c> (RFC 5987/6266),
/// then <c>filename</c>, decoding %-escapes, RFC 2047 encoded words and UTF-8 that was mis-decoded as Latin-1.
/// </summary>
public static class ContentDispositionParser
{
    public static string? GetFileName(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        string? plain = null;
        string? extended = null;
        foreach (var (name, value) in Parameters(header))
        {
            if (name.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                extended ??= DecodeExtendedValue(value);
            }
            else if (name.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                plain ??= DecodePlainValue(value);
            }
        }

        var result = !string.IsNullOrWhiteSpace(extended) ? extended : plain;
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>Splits <c>type; a=b; c="d; e"</c> into parameters, honoring quotes and backslash escapes.</summary>
    internal static IEnumerable<(string Name, string Value)> Parameters(string header)
    {
        var i = header.IndexOf(';', StringComparison.Ordinal);
        if (i < 0)
        {
            // Some servers omit the disposition type: "filename=x.zip".
            if (header.Contains('=', StringComparison.Ordinal))
            {
                i = -1;
            }
            else
            {
                yield break;
            }
        }

        while (i < header.Length)
        {
            i++;
            while (i < header.Length && (header[i] == ' ' || header[i] == '\t' || header[i] == ';'))
            {
                i++;
            }

            var nameStart = i;
            while (i < header.Length && header[i] != '=' && header[i] != ';')
            {
                i++;
            }

            var name = header[nameStart..i].Trim();
            if (i >= header.Length || header[i] == ';')
            {
                continue;
            }

            i++; // '='
            while (i < header.Length && header[i] == ' ')
            {
                i++;
            }

            var value = new StringBuilder();
            if (i < header.Length && header[i] == '"')
            {
                i++;
                while (i < header.Length && header[i] != '"')
                {
                    if (header[i] == '\\' && i + 1 < header.Length)
                    {
                        i++;
                    }

                    value.Append(header[i]);
                    i++;
                }

                i++; // closing quote
                while (i < header.Length && header[i] != ';')
                {
                    i++;
                }
            }
            else
            {
                while (i < header.Length && header[i] != ';')
                {
                    value.Append(header[i]);
                    i++;
                }
            }

            if (name.Length > 0)
            {
                yield return (name, value.ToString().Trim());
            }
        }
    }

    /// <summary>RFC 5987: <c>charset'language'percent-encoded</c>.</summary>
    internal static string? DecodeExtendedValue(string value)
    {
        var first = value.IndexOf('\'', StringComparison.Ordinal);
        var second = first < 0 ? -1 : value.IndexOf('\'', first + 1);
        if (first < 0 || second < 0)
        {
            return PercentDecode(value, Encoding.UTF8);
        }

        var charset = value[..first].Trim();
        var encoded = value[(second + 1)..];
        Encoding encoding;
        try
        {
            encoding = charset.Length == 0 ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return PercentDecode(encoded, encoding);
    }

    internal static string DecodePlainValue(string value)
    {
        if (value.Contains("=?", StringComparison.Ordinal) && value.Contains("?=", StringComparison.Ordinal))
        {
            var decoded = DecodeEncodedWords(value);
            if (decoded is not null)
            {
                return decoded;
            }
        }

        var text = FixMisdecodedUtf8(value);
        if (text.Contains('%', StringComparison.Ordinal) && LooksPercentEncoded(text))
        {
            text = PercentDecode(text, Encoding.UTF8) ?? text;
        }

        return text;
    }

    /// <summary>HTTP header bytes are read as Latin-1; if they were really UTF-8, re-decode them.</summary>
    public static string FixMisdecodedUtf8(string value)
    {
        if (value.All(c => c < 0x80) || value.Any(c => c > 0xFF))
        {
            return value;
        }

        var bytes = Encoding.Latin1.GetBytes(value);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return value;
        }
    }

    private static bool LooksPercentEncoded(string text)
    {
        for (var i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] == '%' && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? PercentDecode(string text, Encoding encoding)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(encoding.GetBytes(text[i].ToString()));
            }
        }

        try
        {
            return encoding.GetString([.. bytes]);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>RFC 2047 <c>=?charset?B|Q?text?=</c> words (sent by some servers despite RFC 6266).</summary>
    private static string? DecodeEncodedWords(string value)
    {
        var result = new StringBuilder();
        var i = 0;
        var any = false;
        while (i < value.Length)
        {
            var start = value.IndexOf("=?", i, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(value, i, value.Length - i);
                break;
            }

            var q1 = value.IndexOf('?', start + 2);
            var q2 = q1 < 0 ? -1 : value.IndexOf('?', q1 + 1);
            var end = q2 < 0 ? -1 : value.IndexOf("?=", q2 + 1, StringComparison.Ordinal);
            if (q1 < 0 || q2 < 0 || end < 0)
            {
                result.Append(value, i, value.Length - i);
                break;
            }

            var between = value[i..start];
            if (!(any && string.IsNullOrWhiteSpace(between)))
            {
                result.Append(between);
            }

            try
            {
                var encoding = Encoding.GetEncoding(value[(start + 2)..q1]);
                var mode = value[(q1 + 1)..q2];
                var payload = value[(q2 + 1)..end];
                byte[] bytes = mode.Equals("B", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : QDecode(payload);
                result.Append(encoding.GetString(bytes));
                any = true;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                return null;
            }

            i = end + 2;
        }

        return any ? result.ToString() : null;
    }

    private static byte[] QDecode(string payload)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];
            if (c == '_')
            {
                bytes.Add(0x20);
            }
            else if (c == '=' && i + 2 < payload.Length && Uri.IsHexDigit(payload[i + 1]) && Uri.IsHexDigit(payload[i + 2]))
            {
                bytes.Add(Convert.ToByte(payload.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        return [.. bytes];
    }
}
