using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace NovaGet.TestServer;

/// <summary>Basic and Digest (MD5, qop=auth) checks for <see cref="TestFile.RequireAuth"/>.</summary>
internal static class TestAuth
{
    private const string Realm = "novaget-test";
    private const string Nonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093";

    public static void Challenge(HttpResponse response, string scheme)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)
            ? $"Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Nonce}\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\", algorithm=MD5"
            : $"Basic realm=\"{Realm}\"";
    }

    public static bool IsAuthorized(HttpRequest request, (string Scheme, string User, string Password) auth)
    {
        var header = request.Headers.Authorization.ToString();
        if (auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            return decoded == $"{auth.User}:{auth.Password}";
        }

        if (!header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fields = Parse(header[7..]);
        if (fields.GetValueOrDefault("username") != auth.User || fields.GetValueOrDefault("nonce") != Nonce)
        {
            return false;
        }

        var ha1 = Md5($"{auth.User}:{Realm}:{auth.Password}");
        var ha2 = Md5($"{request.Method}:{fields.GetValueOrDefault("uri")}");
        var expected = fields.TryGetValue("qop", out var qop)
            ? Md5($"{ha1}:{Nonce}:{fields.GetValueOrDefault("nc")}:{fields.GetValueOrDefault("cnonce")}:{qop}:{ha2}")
            : Md5($"{ha1}:{Nonce}:{ha2}");
        return string.Equals(expected, fields.GetValueOrDefault("response"), StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i])))
            {
                i++;
            }

            var eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            var name = text[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                value = text[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var end = text.IndexOf(',', i);
                end = end < 0 ? text.Length : end;
                value = text[i..end].Trim();
                i = end;
            }

            fields[name] = value;
        }

        return fields;
    }

#pragma warning disable CA5351 // Digest is MD5 by definition; this is a test server.
    private static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
#pragma warning restore CA5351
}
