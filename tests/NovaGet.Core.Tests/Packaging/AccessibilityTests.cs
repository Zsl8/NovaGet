using System.Text.RegularExpressions;
using System.Xml.Linq;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Packaging;

/// <summary>
/// Section 17: every input has an accessible name, from a Label that targets it, AutomationProperties.Name or LabeledBy,
/// and buttons that show only a symbol or an image are named too.
/// </summary>
public sealed partial class AccessibilityTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Inputs = ["TextBox", "PasswordBox", "ComboBox", "ListBox", "DataGrid", "TreeView", "Slider", "ListView"];

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Combine("src", "NovaGet.App"), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.EndsWith("App.xaml", StringComparison.Ordinal)))
        {
            data.Add(Path.GetRelativePath(RepoPaths.Root, file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void Inputs_and_icon_buttons_have_accessible_names(string relativePath)
    {
        var document = XDocument.Load(Path.Combine(RepoPaths.Root, relativePath), LoadOptions.SetLineInfo);
        var labelled = document.Descendants()
            .Select(e => (string?)e.Attribute("Target"))
            .OfType<string>()
            .Select(t => TargetRegex().Match(t))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var element in document.Descendants().Where(e => e.Name.Namespace == Presentation))
        {
            if (IsInTemplate(element) || HasAutomationName(element))
            {
                continue;
            }

            var name = (string?)element.Attribute(Xaml + "Name");
            if (Inputs.Contains(element.Name.LocalName) && (name is null || !labelled.Contains(name)))
            {
                problems.Add($"{element.Name.LocalName} {name ?? "(unnamed)"} (line {((System.Xml.IXmlLineInfo)element).LineNumber})");
            }
            else if (element.Name.LocalName is "Button" or "RepeatButton" or "ToggleButton" && !HasReadableContent(element))
            {
                problems.Add($"{element.Name.LocalName} {name ?? "(unnamed)"} with symbol or image content (line {((System.Xml.IXmlLineInfo)element).LineNumber})");
            }
        }

        Assert.True(problems.Count == 0, $"{relativePath}: no accessible name for {string.Join("; ", problems)}");
    }

    private static bool HasAutomationName(XElement element) =>
        element.Attributes().Any(a => a.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy");

    private static bool IsInTemplate(XElement element) =>
        element.Ancestors().Any(a => a.Name.LocalName is "DataTemplate" or "ControlTemplate" or "HierarchicalDataTemplate" or "Style");

    /// <summary>Text content (a localized string or a binding to text), not a lone symbol like "▲" or "...".</summary>
    private static bool HasReadableContent(XElement button)
    {
        var content = (string?)button.Attribute("Content");
        if (content is null)
        {
            // Child content: a TextBlock or AccessText with text counts; an Image alone doesn't.
            return button.Descendants().Any(d => d.Name.LocalName is "TextBlock" or "AccessText" or "Label")
                || (button.Attribute("Command") is not null && button.Elements().All(e => e.Name.LocalName.Contains('.')));
        }

        return content.StartsWith('{') || content.Any(char.IsLetter);
    }

    [GeneratedRegex(@"ElementName=(\w+)")]
    private static partial Regex TargetRegex();
}
