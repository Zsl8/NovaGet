using System.Text.RegularExpressions;
using NovaGet.Core.Settings;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>Section 20: the installer script stays in step with the app and keeps the required settings.</summary>
public sealed partial class InstallerScriptTests
{
    private static readonly string s_script = File.ReadAllText(RepoPaths.Combine("installer", "NovaGet.iss"));

    private static string Define(string name)
    {
        var match = Regex.Match(s_script, $@"^#define {name} ""([^""]*)""", RegexOptions.Multiline);
        Assert.True(match.Success, $"#define {name} is missing");
        return match.Groups[1].Value;
    }

    [Fact]
    public void Names_match_the_app()
    {
        Assert.Equal(AppInfo.MutexName, Define("AppMutexName"));
        Assert.Equal(AppInfo.AppUserModelId, Define("AppUserModelId"));
        Assert.Equal(AppInfo.NativeHostName, Define("NativeHostName"));
        Assert.Equal(AppInfo.ProductName, Define("AppName"));
        Assert.Equal("NovaGet.exe", Define("AppExeName"));
        Assert.Contains("AppMutex={#AppMutexName}", s_script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PrivilegesRequired=lowest")]
    [InlineData("PrivilegesRequiredOverridesAllowed=dialog")]
    [InlineData("ArchitecturesAllowed=x64compatible")]
    [InlineData("ArchitecturesInstallIn64BitMode=x64compatible")]
    [InlineData("MinVersion=10.0.17763")]
    [InlineData("Compression=lzma2/ultra64")]
    [InlineData("SolidCompression=yes")]
    [InlineData("OutputBaseFilename=NovaGet-Setup-{#AppVersion}")]
    public void Required_setup_directives_are_present(string directive) =>
        Assert.Matches($"(?m)^{Regex.Escape(directive)}\\s*$", s_script);

    [Fact]
    public void Tasks_are_the_four_from_the_spec_with_clipboard_unchecked()
    {
        var tasks = TaskLine().Matches(s_script).Select(m => (Name: m.Groups[1].Value, Line: m.Value)).ToList();
        Assert.Equal(["desktopicon", "startup", "browsers", "clipboard"], tasks.Select(t => t.Name));
        Assert.All(tasks.Where(t => t.Name != "clipboard"), t => Assert.DoesNotContain("unchecked", t.Line, StringComparison.Ordinal));
        Assert.Contains("unchecked", tasks.Single(t => t.Name == "clipboard").Line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_writes_the_defaults_the_app_reads()
    {
        Assert.Contains($"{{app}}\\{InstallDefaults.FileName}", s_script, StringComparison.Ordinal);
        foreach (var key in new[] { "browserIntegration", "monitorClipboard" })
        {
            Assert.Contains($"\"{key}\": ", s_script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_smoke_test_checks_this_installation()
    {
        var smoke = File.ReadAllText(RepoPaths.Combine("installer", "smoke-test.ps1"));
        Assert.Contains($@"Uninstall\{Define("AppGuid")}_is1", smoke, StringComparison.Ordinal);
        Assert.Contains($"$hostName = '{AppInfo.NativeHostName}'", smoke, StringComparison.Ordinal);
        Assert.Contains(AppInfo.PipeName, smoke, StringComparison.Ordinal);
        Assert.Contains("build.ps1 -SmokeTest", File.ReadAllText(RepoPaths.Combine(".github", "workflows", "build.yml")), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?m)^Name: ""(\w+)""; Description: ""\{cm:Task\w*\}"".*$")]
    private static partial Regex TaskLine();
}
