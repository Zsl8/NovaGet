using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaGet.App.Localization;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Grabber;

namespace NovaGet.App.ViewModels;

/// <summary>A file type group in step 4 ("Images", "Video", …, "All files").</summary>
public sealed partial class FileTypeChoice(string key, string extensions) : ObservableObject
{
    public string Key { get; } = key;

    public string Label => Localizer.Get($"Grabber_Type_{Key}");

    public string Extensions { get; } = extensions;

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>Tasks → Run site grabber: the 4-step wizard (section 14).</summary>
public sealed partial class GrabberWizardViewModel : ObservableObject
{
    public const int StepCount = 4;

    private bool _loading;
    private bool _folderEdited;
    private bool _nameEdited;

    public GrabberWizardViewModel(GrabberSettings? existing = null, string? projectName = null)
    {
        Templates = [.. Enum.GetValues<GrabberTemplate>().Select(t => new ChoiceItem((long)t, Localizer.Get($"Grabber_Template_{t}")))];
        FileTypes =
        [
            new FileTypeChoice("Images", GrabberSettings.ImageExtensions),
            new FileTypeChoice("Video", GrabberSettings.VideoExtensions),
            new FileTypeChoice("Audio", GrabberSettings.AudioExtensions),
            new FileTypeChoice("Documents", "doc docx pdf ppt pptx xls xlsx txt rtf odt ods epub"),
            new FileTypeChoice("Compressed", "zip rar 7z gz tar tgz bz2 xz r0* r1*"),
            new FileTypeChoice("Programs", "exe msi msix apk iso img bin dmg"),
            new FileTypeChoice("All", "*"),
        ];
        _nameEdited = existing is not null;
        _folderEdited = existing is not null;
        _projectName = projectName ?? string.Empty;
        Load(existing ?? GrabberSettings.ForTemplate(GrabberTemplate.Images));
    }

    public IReadOnlyList<ChoiceItem> Templates { get; }

    public IReadOnlyList<FileTypeChoice> FileTypes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle), nameof(CanGoBack), nameof(IsLastStep), nameof(IsNotLastStep))]
    private int _step;

    // ---- step 1

    [ObservableProperty]
    private long _templateId;

    [ObservableProperty]
    private string _startUrl = string.Empty;

    [ObservableProperty]
    private string _projectName;

    [ObservableProperty]
    private bool _useAuthorization;

    [ObservableProperty]
    private string _userName = string.Empty;

    /// <summary>Set by the dialog from its password box (never bound).</summary>
    public string? Password { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginStatus))]
    private string? _cookies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginStatus))]
    private bool _isWaitingForLogin;

    public string LoginStatus => IsWaitingForLogin
        ? Localizer.Get("Grabber_LoginWaiting")
        : string.IsNullOrEmpty(Cookies) ? string.Empty : Localizer.Get("Grabber_LoginReceived");

    // ---- step 2

    [ObservableProperty]
    private string _saveFolder = string.Empty;

    [ObservableProperty]
    private bool _saveByCategory;

    [ObservableProperty]
    private bool _convertLinks;

    // ---- step 3

    [ObservableProperty]
    private int _depth;

    [ObservableProperty]
    private bool _stayOnSite;

    [ObservableProperty]
    private bool _exploreSubdomains;

    [ObservableProperty]
    private bool _followExternalLinks;

    [ObservableProperty]
    private int _externalDepth;

    /// <summary>0 = unlimited.</summary>
    [ObservableProperty]
    private int _maxPages;

    /// <summary>One wildcard filter per line.</summary>
    [ObservableProperty]
    private string _includeFilters = string.Empty;

    [ObservableProperty]
    private string _excludeFilters = string.Empty;

    [ObservableProperty]
    private bool _obeyRobots;

    [ObservableProperty]
    private int _delayMs;

    [ObservableProperty]
    private int _maxParallel;

    // ---- step 4

    [ObservableProperty]
    private string _customExtensions = string.Empty;

    [ObservableProperty]
    private long _minSizeKB;

    [ObservableProperty]
    private long _maxSizeKB;

    [ObservableProperty]
    private bool _onlyMatching;

    public string StepTitle => Localizer.Format("Grabber_StepTitle", Step + 1, StepCount, Localizer.Get($"Grabber_Step{Step + 1}"));

    public bool CanGoBack => Step > 0;

    public bool IsLastStep => Step == StepCount - 1;

    public bool IsNotLastStep => !IsLastStep;

    public GrabberTemplate Template => (GrabberTemplate)TemplateId;

    /// <summary>Why the step can't be left, or null.</summary>
    public string? Validate(int step)
    {
        switch (step)
        {
            case 0:
                if (!Uri.TryCreate(StartUrl.Trim(), UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
                {
                    return Localizer.Get("Grabber_Error_StartUrl");
                }

                return string.IsNullOrWhiteSpace(ProjectName) ? Localizer.Get("Grabber_Error_Name") : null;
            case 1:
                return Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(SaveFolder.Trim())) ? null : Localizer.Get("Error_InvalidPath");
            case 2:
                return Depth is < 0 or > 20 || ExternalDepth is < 0 or > 20 || MaxPages < 0 || DelayMs is < 0 or > 600_000 || MaxParallel is < 1 or > 16
                    ? Localizer.Get("Grabber_Error_Exploration")
                    : null;
            default:
                if (string.IsNullOrWhiteSpace(FileTypesText))
                {
                    return Localizer.Get("Grabber_Error_FileTypes");
                }

                return MinSizeKB < 0 || MaxSizeKB < 0 || (MaxSizeKB > 0 && MinSizeKB > MaxSizeKB) ? Localizer.Get("Grabber_Error_Size") : null;
        }
    }

    /// <summary>The first step with a problem and its message, or null when everything is fine.</summary>
    public (int Step, string Message)? ValidateAll()
    {
        for (var step = 0; step < StepCount; step++)
        {
            if (Validate(step) is { } message)
            {
                return (step, message);
            }
        }

        return null;
    }

    public GrabberSettings Build() => new()
    {
        Template = Template,
        StartUrl = StartUrl.Trim(),
        UseAuthorization = UseAuthorization,
        UserName = UseAuthorization && !string.IsNullOrWhiteSpace(UserName) ? UserName.Trim() : null,
        Password = UseAuthorization && !string.IsNullOrEmpty(Password) ? Password : null,
        Cookies = string.IsNullOrEmpty(Cookies) ? null : Cookies,
        SaveFolder = SaveFolder.Trim(),
        SaveByCategory = SaveByCategory,
        ConvertLinks = ConvertLinks,
        Depth = Depth,
        StayOnSite = StayOnSite,
        ExploreSubdomains = ExploreSubdomains,
        FollowExternalLinks = FollowExternalLinks,
        ExternalDepth = ExternalDepth,
        MaxPages = MaxPages,
        IncludeFilters = Lines(IncludeFilters),
        ExcludeFilters = Lines(ExcludeFilters),
        ObeyRobots = ObeyRobots,
        DelayMs = DelayMs,
        MaxParallel = MaxParallel,
        FileTypes = FileTypesText,
        MinSizeKB = MinSizeKB,
        MaxSizeKB = MaxSizeKB,
        OnlyMatching = OnlyMatching,
    };

    /// <summary>The checked groups' extensions plus the custom ones.</summary>
    public string FileTypesText =>
        string.Join(' ', FileTypes.Where(f => f.IsChecked).Select(f => f.Extensions).Append(CustomExtensions.Trim()).Where(t => t.Length > 0));

    partial void OnTemplateIdChanged(long value)
    {
        if (!_loading)
        {
            // A template sets steps 2–4; the start page and login stay.
            Load(GrabberSettings.ForTemplate((GrabberTemplate)value, Build()), keepStart: true);
        }
    }

    partial void OnStartUrlChanged(string value)
    {
        if (!_nameEdited && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url) && url.Host.Length > 0)
        {
            _loading = true;
            ProjectName = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
            _loading = false;
        }
    }

    partial void OnProjectNameChanged(string value)
    {
        if (!_loading)
        {
            _nameEdited = true;
        }

        if (!_folderEdited && !string.IsNullOrWhiteSpace(value))
        {
            _loading = true;
            SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
                NovaGet.Core.Engine.Naming.FileNameSanitizer.Sanitize(value.Trim()));
            _loading = false;
        }
    }

    partial void OnSaveFolderChanged(string value)
    {
        if (!_loading)
        {
            _folderEdited = true;
        }
    }

    private void Load(GrabberSettings settings, bool keepStart = false)
    {
        _loading = true;
        try
        {
            TemplateId = (long)settings.Template;
            if (!keepStart)
            {
                StartUrl = settings.StartUrl;
                UseAuthorization = settings.UseAuthorization;
                UserName = settings.UserName ?? string.Empty;
                Password = settings.Password;
                Cookies = settings.Cookies;
                if (!string.IsNullOrWhiteSpace(settings.SaveFolder))
                {
                    SaveFolder = settings.SaveFolder;
                }
            }

            SaveByCategory = settings.SaveByCategory;
            ConvertLinks = settings.ConvertLinks;
            Depth = settings.Depth;
            StayOnSite = settings.StayOnSite;
            ExploreSubdomains = settings.ExploreSubdomains;
            FollowExternalLinks = settings.FollowExternalLinks;
            ExternalDepth = settings.ExternalDepth;
            MaxPages = settings.MaxPages;
            IncludeFilters = string.Join(Environment.NewLine, settings.IncludeFilters);
            ExcludeFilters = string.Join(Environment.NewLine, settings.ExcludeFilters);
            ObeyRobots = settings.ObeyRobots;
            DelayMs = settings.DelayMs;
            MaxParallel = settings.MaxParallel;
            MinSizeKB = settings.MinSizeKB;
            MaxSizeKB = settings.MaxSizeKB;
            OnlyMatching = settings.OnlyMatching;

            // Tick the groups the file types cover; whatever is left goes to "custom".
            var wanted = settings.FileTypes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in FileTypes)
            {
                var extensions = choice.Extensions.Split(' ');
                choice.IsChecked = extensions.All(wanted.Contains);
                if (choice.IsChecked)
                {
                    wanted.ExceptWith(extensions);
                }
            }

            CustomExtensions = string.Join(' ', settings.FileTypes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(wanted.Contains).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            _loading = false;
        }
    }

    private static string[] Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
