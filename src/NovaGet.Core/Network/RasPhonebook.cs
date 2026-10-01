namespace NovaGet.Core.Network;

/// <summary>
/// Dial-up and VPN entry names from Windows phonebook files (<c>rasphone.pbk</c>), which are INI files with one
/// section per entry. Reading the files avoids P/Invoke for a list that only fills a dropdown.
/// </summary>
public static class RasPhonebook
{
    /// <summary>The per-user and all-users phonebooks.</summary>
    public static IEnumerable<string> DefaultFiles()
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        })
        {
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");
            }
        }
    }

    /// <summary>Entry names from the given phonebooks, de-duplicated and sorted.</summary>
    public static IReadOnlyList<string> ReadEntries(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    foreach (var name in ParseEntries(File.ReadAllText(file)))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable phonebook: just list what we can.
            }
        }

        return [.. names];
    }

    public static IEnumerable<string> ParseEntries(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (name.Length > 0)
                {
                    yield return name;
                }
            }
        }
    }
}
