using System.Text.Json;
using System.Text.RegularExpressions;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>The browser extension's manifests, files and strings must be consistent (it can't be tested in a browser here).</summary>
public sealed partial class ExtensionPackageTests
{
    private static readonly string[] SpecPermissions = ["downloads", "webRequest", "cookies", "contextMenus", "nativeMessaging", "storage", "tabs", "scripting"];

    private static JsonElement Manifest(string flavor) =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("browser-extension", flavor, "manifest.json"))).RootElement;

    private static JsonElement Ids() =>
        JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine("browser-extension", "extension-ids.json"))).RootElement;

    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    public void Manifests_ask_for_exactly_the_documented_permissions(string flavor)
    {
        var manifest = Manifest(flavor);

        Assert.Equal(3, manifest.GetProperty("manifest_version").GetInt32());
        Assert.Equal(SpecPermissions.Order(), manifest.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Order());
        Assert.Equal(["<all_urls>"], manifest.GetProperty("host_permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void Identities_are_pinned()
    {
        var ids = Ids();
        Assert.Equal(ids.GetProperty("chromiumPublicKey").GetString(), Manifest("chromium").GetProperty("key").GetString());
        Assert.Equal(ids.GetProperty("firefoxId").GetString(),
            Manifest("firefox").GetProperty("browser_specific_settings").GetProperty("gecko").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    public void Every_referenced_file_exists(string flavor)
    {
        var manifest = Manifest(flavor);
        var files = new List<string>();
        files.AddRange(manifest.GetProperty("icons").EnumerateObject().Select(p => p.Value.GetString()!));
        files.Add(manifest.GetProperty("action").GetProperty("default_popup").GetString()!);
        files.AddRange(manifest.GetProperty("content_scripts").EnumerateArray().SelectMany(c => c.GetProperty("js").EnumerateArray()).Select(j => j.GetString()!));
        var background = manifest.GetProperty("background");
        files.AddRange(background.TryGetProperty("service_worker", out var worker)
            ? [worker.GetString()!]
            : background.GetProperty("scripts").EnumerateArray().Select(s => s.GetString()!));

        Assert.All(files, file => Assert.True(File.Exists(RepoPaths.Combine("browser-extension", "src", file)), file));
    }

    [Fact]
    public void Only_the_drm_observer_runs_in_the_page_context()
    {
        var chromium = Manifest("chromium").GetProperty("content_scripts").EnumerateArray().ToList();
        var main = Assert.Single(chromium, c => c.TryGetProperty("world", out var w) && w.GetString() == "MAIN");
        Assert.Equal(["eme.js"], main.GetProperty("js").EnumerateArray().Select(j => j.GetString()));
        Assert.All(Manifest("firefox").GetProperty("content_scripts").EnumerateArray(), c => Assert.False(c.TryGetProperty("world", out _)));

        // It observes only: no network, no messaging, no storage from the page context.
        var eme = File.ReadAllText(RepoPaths.Combine("browser-extension", "src", "eme.js"));
        Assert.DoesNotContain("fetch(", eme, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", eme, StringComparison.Ordinal);
        Assert.DoesNotContain("postMessage", eme, StringComparison.Ordinal);
        Assert.Contains("original.apply(this, args)", eme, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_message_used_exists_in_each_locale()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Combine("browser-extension", "src"), "*.*")
                     .Concat([RepoPaths.Combine("browser-extension", "chromium", "manifest.json"), RepoPaths.Combine("browser-extension", "firefox", "manifest.json")]))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in MessageRegex().Matches(text))
            {
                used.Add(m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
            }
        }

        Assert.NotEmpty(used);
        foreach (var locale in Directory.EnumerateDirectories(RepoPaths.Combine("browser-extension", "src", "_locales")))
        {
            var messages = JsonDocument.Parse(File.ReadAllText(Path.Combine(locale, "messages.json"))).RootElement;
            Assert.All(used, key => Assert.True(messages.TryGetProperty(key, out _), $"{Path.GetFileName(locale)} lacks {key}"));
        }
    }

    [GeneratedRegex(@"__MSG_(\w+)__|getMessage\('(\w+)'|data-i18n=""(\w+)""")]
    private static partial Regex MessageRegex();
}
