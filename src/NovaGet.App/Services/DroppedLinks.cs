using System.IO;
using System.Text;
using System.Windows;
using NovaGet.Core.Integration;

namespace NovaGet.App.Services;

/// <summary>Addresses from a drag and drop: browser link formats, plain text, and Internet shortcut (.url) files.</summary>
internal static class DroppedLinks
{
    private const long MaxShortcutBytes = 64 * 1024;

    public static bool HasLinks(IDataObject data) =>
        data.GetDataPresent("UniformResourceLocatorW") || data.GetDataPresent("UniformResourceLocator")
        || data.GetDataPresent("text/x-moz-url") || data.GetDataPresent(DataFormats.UnicodeText)
        || data.GetDataPresent(DataFormats.Text) || data.GetDataPresent(DataFormats.FileDrop);

    public static IReadOnlyList<Uri> From(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                return [.. files
                    .Where(f => f.EndsWith(".url", StringComparison.OrdinalIgnoreCase) && File.Exists(f) && new FileInfo(f).Length <= MaxShortcutBytes)
                    .Select(f => LinkExtractor.ParseInternetShortcut(File.ReadAllText(f)))
                    .OfType<Uri>()
                    .Distinct()];
            }

            if (Read(data, "text/x-moz-url", Encoding.Unicode) is { } moz && LinkExtractor.ParseMozUrl(moz) is { Count: > 0 } mozLinks)
            {
                return mozLinks;
            }

            foreach (var (format, encoding) in new[] { ("UniformResourceLocatorW", Encoding.Unicode), ("UniformResourceLocator", Encoding.Default) })
            {
                if (LinkExtractor.SingleUrl(Read(data, format, encoding)) is { } link)
                {
                    return [link];
                }
            }

            var text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
            return LinkExtractor.ExtractUrls(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>Browsers put some formats on the clipboard as raw, null-terminated memory streams.</summary>
    private static string? Read(IDataObject data, string format, Encoding encoding)
    {
        if (!data.GetDataPresent(format))
        {
            return null;
        }

        switch (data.GetData(format))
        {
            case string text:
                return text;
            case MemoryStream stream when stream.Length <= 1024 * 1024:
                return encoding.GetString(stream.ToArray()).TrimEnd('\0');
            default:
                return null;
        }
    }
}
