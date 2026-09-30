namespace NovaGet.Core.Tests.Infrastructure;

internal static class RepoPaths
{
    /// <summary>The repository root (the folder that contains NovaGet.sln).</summary>
    public static string Root { get; } = FindRoot();

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NovaGet.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (NovaGet.sln) not found.");
    }
}
