using CommunityToolkit.Mvvm.ComponentModel;
using NovaGet.App.Localization;
using NovaGet.Core.Integration;
using NovaGet.Core.Models;
using NovaGet.Core.Settings;

namespace NovaGet.App.ViewModels.Options;

/// <summary>A dropdown entry: the stored value and its localized text.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Options → General: one row of the browser checklist.</summary>
public sealed partial class BrowserOption(BrowserInfo browser, bool installed, bool integrated) : ObservableObject
{
    public BrowserInfo Browser { get; } = browser;

    public string Name => Browser.DisplayName;

    public bool IsInstalled { get; } = installed;

    public string Status => Localizer.Get(IsInstalled ? "Options_BrowserFound" : "Options_BrowserNotFound");

    [ObservableProperty]
    private bool _isIntegrated = integrated;
}

/// <summary>Options → Save To: a category being edited (saved on OK).</summary>
public sealed partial class CategoryEdit : ObservableObject
{
    public CategoryEdit(Category category, string defaultFolder)
    {
        Original = category;
        Id = category.Id;
        IsBuiltIn = category.IsBuiltIn;
        DefaultFolder = defaultFolder;
        _name = category.Name;
        _extensions = category.Extensions;
        _folder = string.IsNullOrWhiteSpace(category.DefaultSaveDir) ? defaultFolder : category.DefaultSaveDir;
    }

    public Category Original { get; }

    /// <summary>0 for a category added in this dialog.</summary>
    public long Id { get; }

    public bool IsBuiltIn { get; }

    /// <summary>What applies when no folder is set (<c>Downloads\&lt;Category&gt;</c>).</summary>
    public string DefaultFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name;

    [ObservableProperty]
    private string _extensions;

    [ObservableProperty]
    private string _folder;

    public string Title => IsBuiltIn ? MainViewModel.CategoryTitle(Original) : Name;

    /// <summary>The folder to store: null while it is the built-in default.</summary>
    public string? FolderToSave =>
        string.IsNullOrWhiteSpace(Folder) || string.Equals(Folder.Trim(), DefaultFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : Folder.Trim();

    public bool IsChanged =>
        Id == 0
        || !string.Equals(Name, Original.Name, StringComparison.Ordinal)
        || !string.Equals(Extensions, Original.Extensions, StringComparison.Ordinal)
        || !string.Equals(FolderToSave, string.IsNullOrWhiteSpace(Original.DefaultSaveDir) ? null : Original.DefaultSaveDir, StringComparison.Ordinal);

    public override string ToString() => Title;
}

/// <summary>Options → Connection → Exceptions: a server with its own connection limit.</summary>
public sealed partial class ServerExceptionEdit(long id, string host, int maxConnections) : ObservableObject
{
    public long Id { get; } = id;

    [ObservableProperty]
    private string _host = host;

    [ObservableProperty]
    private int _maxConnections = maxConnections;
}

/// <summary>Options → Site Logins: one saved login (the password is shown as dots).</summary>
public sealed partial class SiteLoginEdit(long id, string urlPattern, string user, string password) : ObservableObject
{
    public long Id { get; } = id;

    [ObservableProperty]
    private string _urlPattern = urlPattern;

    [ObservableProperty]
    private string _user = user;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedPassword))]
    private string _password = password;

    public bool IsChanged { get; set; }

    public string MaskedPassword => new('●', Math.Min(Password.Length, 12));
}

/// <summary>Options → Sounds: one event with its switch and file.</summary>
public sealed partial class SoundRow(SoundEvent soundEvent, SoundEntry entry) : ObservableObject
{
    public SoundEvent Event { get; } = soundEvent;

    public string Name => Localizer.Get("Sound_" + Event);

    [ObservableProperty]
    private bool _enabled = entry.Enabled;

    [ObservableProperty]
    private string _path = entry.Path;
}
