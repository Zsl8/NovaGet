using System.Text.RegularExpressions;

namespace NovaGet.Core.Diagnostics;

/// <summary>Removes secrets from text before it is written to a log: passwords in addresses (<c>ftp://user:***@host</c>).</summary>
public static partial class LogRedaction
{
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("://", StringComparison.Ordinal) && text.Contains('@', StringComparison.Ordinal)
            ? AddressPassword().Replace(text, "$1:***@")
            : text;
    }

    // scheme://user:password@ → scheme://user:***@ (the user name helps diagnosing; the password never appears).
    [GeneratedRegex(@"(\b[a-z][a-z0-9+.\-]*://[^\s/?#@:""']*):[^\s/?#@""']*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AddressPassword();
}
