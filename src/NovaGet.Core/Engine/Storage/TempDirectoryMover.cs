using System.Globalization;

namespace NovaGet.Core.Engine.Storage;

/// <summary>
/// Moves unfinished downloads' temp folders (<c>&lt;temp&gt;\&lt;id&gt;</c>) when Options → Save To → Temporary
/// directory changes, so paused downloads keep their data. Only numeric folders are touched.
/// </summary>
public static class TempDirectoryMover
{
    public sealed record Result(int Moved, IReadOnlyList<string> Failed);

    public static Result Move(string oldDirectory, string newDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDirectory);
        var from = Path.GetFullPath(Environment.ExpandEnvironmentVariables(oldDirectory));
        var to = Path.GetFullPath(Environment.ExpandEnvironmentVariables(newDirectory));
        if (string.Equals(from.TrimEnd(Path.DirectorySeparatorChar), to.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(from))
        {
            return new Result(0, []);
        }

        Directory.CreateDirectory(to);
        var moved = 0;
        var failed = new List<string>();
        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(folder);
            if (!long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            var target = Path.Combine(to, name);
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true); // stale leftovers of an earlier download with this id
                }

                try
                {
                    Directory.Move(folder, target);
                }
                catch (IOException) when (!SameVolume(from, to))
                {
                    CopyDirectory(folder, target);
                    Directory.Delete(folder, recursive: true);
                }

                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(name);
            }
        }

        return new Result(moved, failed);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
