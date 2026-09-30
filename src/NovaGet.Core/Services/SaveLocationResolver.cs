using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Settings;

namespace NovaGet.Core.Services;

/// <summary>Where a category's files go by default.</summary>
public static class SaveLocationResolver
{
    /// <summary>
    /// "Use the same directory for all categories" wins; then the category's own folder; then
    /// <c>Downloads</c> for General and <c>Downloads\&lt;Category&gt;</c> for everything else.
    /// </summary>
    public static string FolderFor(Category category, AppSettings settings, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);

        if (settings.SaveTo.UseSameDirectoryForAllCategories && !string.IsNullOrWhiteSpace(settings.SaveTo.SameDirectory))
        {
            return Expand(settings.SaveTo.SameDirectory);
        }

        if (!string.IsNullOrWhiteSpace(category.DefaultSaveDir))
        {
            return Expand(category.DefaultSaveDir);
        }

        return category.Id == Category.GeneralId
            ? paths.UserDownloadsDir
            : Path.Combine(paths.UserDownloadsDir, Engine.Naming.FileNameSanitizer.Sanitize(category.Name, "Other"));
    }

    private static string Expand(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
}
