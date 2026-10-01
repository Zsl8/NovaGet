using System.Text.RegularExpressions;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>Ground rule 4: every package NovaGet ships is listed in THIRD_PARTY_NOTICES.txt with its license.</summary>
public sealed partial class ThirdPartyNoticeTests
{
    private static readonly string[] TestOnly = ["Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio"];

    [Fact]
    public void Every_shipped_package_is_in_the_notices()
    {
        var packages = PackageRegex().Matches(File.ReadAllText(RepoPaths.Combine("Directory.Packages.props")))
            .Select(m => m.Groups[1].Value)
            .Where(p => !TestOnly.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var notices = File.ReadAllText(RepoPaths.Combine("THIRD_PARTY_NOTICES.txt"));

        Assert.NotEmpty(packages);
        Assert.All(packages, package => Assert.True(IsListed(package, notices), $"{package} is missing from THIRD_PARTY_NOTICES.txt"));
    }

    [Fact]
    public void The_bundled_ffmpeg_is_listed_with_its_license_and_source()
    {
        var notices = File.ReadAllText(RepoPaths.Combine("THIRD_PARTY_NOTICES.txt"));

        Assert.Contains("FFmpeg", notices, StringComparison.Ordinal);
        Assert.Contains("LGPL", notices, StringComparison.Ordinal);
        Assert.Contains("https://ffmpeg.org/download.html", notices, StringComparison.Ordinal);
        Assert.Contains("lgpl-shared", File.ReadAllText(RepoPaths.Combine("build", "ffmpeg.json")), StringComparison.Ordinal);
    }

    /// <summary>Listed by name, or by a wildcard line such as <c>Microsoft.Extensions.*</c> or <c>Serilog.*</c>.</summary>
    private static bool IsListed(string package, string notices)
    {
        if (notices.Contains(package, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = package.Split('.');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            if (notices.Contains(string.Join('.', parts.Take(i)) + ".*", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("<PackageVersion Include=\"([^\"]+)\"")]
    private static partial Regex PackageRegex();
}
