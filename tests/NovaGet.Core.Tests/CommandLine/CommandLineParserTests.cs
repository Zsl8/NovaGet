using NovaGet.Core.CommandLine;

namespace NovaGet.Core.Tests.CommandLine;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Parses_all_idm_switches()
    {
        var o = CommandLineParser.Parse(
        [
            "/d", "https://example.com/file.zip", "/p", @"C:\Downloads", "/f", "renamed.zip",
            "/q", "/h", "/n", "/a", "/s", "/tray", "/startqueue", "Night queue", "/stopqueue", "Main download queue",
        ]);

        Assert.Empty(o.Errors);
        Assert.Equal("https://example.com/file.zip", o.Url);
        Assert.Equal(@"C:\Downloads", o.SaveFolder);
        Assert.Equal("renamed.zip", o.FileName);
        Assert.True(o.ExitWhenDone);
        Assert.True(o.HangUpWhenDone);
        Assert.True(o.Silent);
        Assert.True(o.AddToQueueOnly);
        Assert.True(o.StartMainQueue);
        Assert.True(o.StartInTray);
        Assert.Equal(["Night queue"], o.StartQueues);
        Assert.Equal(["Main download queue"], o.StopQueues);
        Assert.True(o.HasActions);
    }

    [Fact]
    public void Switches_are_case_insensitive_and_accept_dash_prefix()
    {
        var o = CommandLineParser.Parse(["-D", "ftp://host/a.iso", "--TRAY", "/N"]);

        Assert.Empty(o.Errors);
        Assert.Equal("ftp://host/a.iso", o.Url);
        Assert.True(o.StartInTray);
        Assert.True(o.Silent);
    }

    [Fact]
    public void Tray_only_has_no_actions()
    {
        var o = CommandLineParser.Parse(["/tray"]);

        Assert.True(o.StartInTray);
        Assert.False(o.HasActions);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("not a url")]
    public void Rejects_unsupported_addresses(string url)
    {
        var o = CommandLineParser.Parse(["/d", url]);

        Assert.Null(o.Url);
        Assert.NotEmpty(o.Errors);
    }

    [Fact]
    public void Reports_unknown_switches_and_missing_values()
    {
        var o = CommandLineParser.Parse(["/bogus", "/p"]);

        Assert.Equal(2, o.Errors.Count);
    }

    [Theory]
    [InlineData("novaget://https://example.com/a.zip", "https://example.com/a.zip")]
    [InlineData("novaget:https://example.com/a.zip", "https://example.com/a.zip")]
    [InlineData("novaget://https:/example.com/a.zip", "https://example.com/a.zip")]
    [InlineData("novaget://download?url=https%3A%2F%2Fexample.com%2Fa%20b.zip", "https://example.com/a b.zip")]
    [InlineData("\"https://example.com/q?x=1\"", "https://example.com/q?x=1")]
    public void Unwraps_protocol_links(string input, string expected)
    {
        Assert.Equal(expected, CommandLineParser.NormalizeUrlArgument(input));
        Assert.Equal(expected, CommandLineParser.Parse(["/d", input]).Url);
    }

    [Fact]
    public void Bare_url_argument_means_download()
    {
        var o = CommandLineParser.Parse(["https://example.com/x.bin"]);

        Assert.Equal("https://example.com/x.bin", o.Url);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Unix_style_path_after_p_is_accepted()
    {
        var o = CommandLineParser.Parse(["/p", "/home/user/dl"]);

        Assert.Equal("/home/user/dl", o.SaveFolder);
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void Cleanup_and_exit_switches()
    {
        var o = CommandLineParser.Parse(["/cleanup"]);
        Assert.True(o.Cleanup);
        Assert.Empty(o.Errors);

        Assert.True(CommandLineParser.Parse(["/exit"]).Exit);
    }
}
