using System.Text.RegularExpressions;
using System.Xml.Linq;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>Every string key the app uses must exist in Strings.resx (a typo would show the raw key).</summary>
public sealed partial class LocalizationKeyTests
{
    private static readonly string s_appFolder = RepoPaths.Combine("src", "NovaGet.App");

    private static HashSet<string> ResxKeys(string file) =>
        [.. XDocument.Load(file).Root!.Elements("data").Select(d => (string)d.Attribute("name")!)];

    [Fact]
    public void All_used_keys_exist_in_the_english_table()
    {
        var keys = ResxKeys(Path.Combine(s_appFolder, "Localization", "Strings.resx"));
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(s_appFolder, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in XamlKey().Matches(text))
            {
                used.Add(m.Groups[1].Value);
            }

            foreach (Match m in CodeKey().Matches(text))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        foreach (var name in new[] { "General", "Compressed", "Documents", "Music", "Programs", "Video" })
        {
            used.Add("Category_" + name); // built-in category titles are looked up by name
        }

        foreach (var error in Enum.GetNames<NovaGet.Core.ImportExport.BatchError>())
        {
            used.Add("Batch_Error_" + error); // batch errors are looked up by name
        }

        Assert.NotEmpty(used);
        var missing = used.Where(k => !keys.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Missing string keys: " + string.Join(", ", missing));
    }

    [Fact]
    public void Translations_only_contain_known_keys()
    {
        var english = ResxKeys(Path.Combine(s_appFolder, "Localization", "Strings.resx"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(s_appFolder, "Localization"), "Strings.*.resx"))
        {
            var extra = ResxKeys(file).Where(k => !english.Contains(k)).ToList();
            Assert.True(extra.Count == 0, $"{Path.GetFileName(file)} has unknown keys: {string.Join(", ", extra)}");
        }
    }

    [GeneratedRegex("""\{l:Loc\s+(?:Key=)?([A-Za-z0-9_]+)""")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"(?:Localizer\.(?:Get|Format|Plain)|\bItem|\bAdd|\bLoc)\(\s*""([A-Z][A-Za-z0-9]*_[A-Za-z0-9_]+)""")]
    private static partial Regex CodeKey();
}
