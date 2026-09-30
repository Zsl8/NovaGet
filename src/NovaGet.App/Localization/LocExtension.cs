using System.Windows.Markup;

namespace NovaGet.App.Localization;

/// <summary><c>{l:Loc Menu_Tasks}</c> — a localized string, resolved when the XAML loads.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Remove access-key underscores (for tooltips).</summary>
    public bool Plain { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Plain ? Localizer.Plain(Key) : Localizer.Get(Key);
}
