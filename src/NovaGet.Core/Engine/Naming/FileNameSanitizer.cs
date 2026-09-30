using System.Text;

namespace NovaGet.Core.Engine.Naming;

/// <summary>Makes any server- or user-supplied name safe to use as a Windows file name.</summary>
public static class FileNameSanitizer
{
    public const int MaxLength = 255;
    public const string DefaultName = "download";

    private static readonly HashSet<string> s_reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
        "CONIN$", "CONOUT$",
    };

    /// <summary>
    /// Strips directories (path traversal), replaces characters Windows forbids, trims trailing dots and
    /// spaces, escapes reserved device names and limits the length to 255 characters (keeping the extension).
    /// </summary>
    public static string Sanitize(string? name, string fallback = DefaultName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        // Only the last path component survives: "../../x.exe" and "C:\a\b.txt" become "x.exe" / "b.txt".
        var text = name.Replace('\\', '/');
        text = text[(text.LastIndexOf('/') + 1)..];

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                < ' ' or '\u007F' => '_',
                '<' or '>' or ':' or '"' or '|' or '?' or '*' => '_',
                _ => c,
            });
        }

        text = builder.ToString().Trim().TrimEnd('.', ' ');
        if (text.Length == 0 || text.Trim('.', '_', ' ').Length == 0)
        {
            return fallback;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? text : text[..dot]).TrimEnd(' ');
        if (s_reserved.Contains(stem))
        {
            text = "_" + text;
        }

        return Truncate(text, MaxLength);
    }

    /// <summary>Shortens the stem, keeping a reasonable extension, without splitting surrogate pairs.</summary>
    internal static string Truncate(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length > 16)
        {
            extension = string.Empty;
        }

        var stemLength = maxLength - extension.Length;
        var stem = name[..^extension.Length];
        if (stemLength < stem.Length)
        {
            if (char.IsHighSurrogate(stem[stemLength - 1]))
            {
                stemLength--;
            }

            stem = stem[..stemLength].TrimEnd('.', ' ');
        }

        return stem + extension;
    }

    /// <summary>Returns <c>name</c>, or <c>name (2).ext</c>, <c>name (3).ext</c>… so it doesn't collide with an existing file.</summary>
    public static string MakeUnique(string directory, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!exists(Path.Combine(directory, fileName)))
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = Truncate($"{stem} ({n}){extension}", MaxLength);
            if (!exists(Path.Combine(directory, candidate)))
            {
                return candidate;
            }
        }

        return Truncate($"{stem} ({Guid.NewGuid():N}){extension}", MaxLength);
    }
}
