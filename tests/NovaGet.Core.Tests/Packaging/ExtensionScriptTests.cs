using System.Diagnostics;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>Node.js on the PATH, for the extension's script tests.</summary>
internal static class NodeTools
{
    public static string? Node { get; } = Find();

    /// <summary>Playwright with its Chromium (developer machines; not installed on CI).</summary>
    public static bool HasPlaywright { get; } = Node is not null && Run(Node, ["-e",
        "for (const p of ['playwright', require('path').join(require('path').dirname(process.execPath), '..', 'lib', 'node_modules', 'playwright'), require('path').join(require('path').dirname(process.execPath), 'node_modules', 'playwright')]) { try { require.resolve(p); process.exit(0); } catch {} } process.exit(1);"],
        TimeSpan.FromSeconds(20)).ExitCode == 0;

    public static (int ExitCode, string Output) Run(string exe, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timed out");
        }

        return (process.ExitCode, output.Result + errors.Result);
    }

    private static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), name))
            .FirstOrDefault(File.Exists);
    }
}

public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (NodeTools.Node is null)
        {
            Skip = "Node.js is not on the PATH.";
        }
    }
}

public sealed class PlaywrightFactAttribute : FactAttribute
{
    public PlaywrightFactAttribute()
    {
        if (!NodeTools.HasPlaywright)
        {
            Skip = "Playwright (with Chromium) is not installed.";
        }
    }
}

/// <summary>The extension's own tests: background logic against a fake browser API, and the video panel in Chromium.</summary>
public sealed class ExtensionScriptTests
{
    [NodeFact]
    public void Background_media_detection_and_messaging()
    {
        var (exitCode, output) = NodeTools.Run(NodeTools.Node!, [RepoPaths.Combine("browser-extension", "test", "background.test.js")], TimeSpan.FromMinutes(1));

        Assert.True(exitCode == 0, output);
    }

    [PlaywrightFact]
    public void Video_panel_appears_on_mp4_and_hls_players_and_refuses_drm()
    {
        var (exitCode, output) = NodeTools.Run(NodeTools.Node!, [RepoPaths.Combine("browser-extension", "test", "panel.e2e.js")], TimeSpan.FromMinutes(2));

        Assert.True(exitCode == 0, output);
    }
}
