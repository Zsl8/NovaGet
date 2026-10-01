using System.Text.RegularExpressions;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>Section 15: the switches are documented in docs/command-line.md and in Help (command-line.html).</summary>
public sealed partial class CommandLineDocsTests
{
    [Theory]
    [InlineData("command-line.md")]
    [InlineData("command-line.html")]
    public void Every_switch_the_parser_knows_is_documented(string doc)
    {
        var parser = File.ReadAllText(RepoPaths.Combine("src", "NovaGet.Core", "CommandLine", "CommandLineParser.cs"));
        var switches = CaseRegex().Matches(parser).Select(m => m.Groups[1].Value).ToList();
        var text = File.ReadAllText(RepoPaths.Combine("docs", doc));

        Assert.True(switches.Count >= 13, "the parser's switches were not found");
        Assert.All(switches, s => Assert.Matches($@"/{Regex.Escape(s)}\b", text));
    }

    [GeneratedRegex("""case "([a-z]+)":""")]
    private static partial Regex CaseRegex();
}
