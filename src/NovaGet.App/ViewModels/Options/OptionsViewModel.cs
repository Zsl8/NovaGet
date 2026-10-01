using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaGet.App.Localization;
using NovaGet.Core.Integration;
using NovaGet.Core.Models;
using NovaGet.Core.Network;
using NovaGet.Core.Paths;
using NovaGet.Core.Security;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.ViewModels.Options;

/// <summary>The tabs of the Options dialog, in display order (section 9).</summary>
public enum OptionsPage
{
    General,
    FileTypes,
    SaveTo,
    Downloads,
    Connection,
    Proxy,
    SiteLogins,
    DialUp,
    Sounds,
    Advanced,
}

/// <summary>Everything the Options dialog shows besides settings.json.</summary>
public sealed record OptionsContext
{
    public required AppSettings Settings { get; init; }

    public required AppPaths Paths { get; init; }

    public required ISecretProtector Protector { get; init; }

    public bool LaunchOnStartup { get; init; }

    public IReadOnlySet<string> InstalledBrowsers { get; init; } = new HashSet<string>();

    public IReadOnlyList<string> RasEntries { get; init; } = [];

    public IReadOnlyList<CultureInfo> Languages { get; init; } = [CultureInfo.GetCultureInfo("en")];

    public IReadOnlyList<Category> Categories { get; init; } = [];

    public IReadOnlyList<ServerException> ServerExceptions { get; init; } = [];

    public IReadOnlyList<SiteLogin> SiteLogins { get; init; } = [];

    public IPacResolver? PacResolver { get; init; }
}

/// <summary>A problem that keeps OK from closing the dialog.</summary>
public sealed record OptionsError(OptionsPage Page, string Message);

/// <summary>
/// Options dialog state. Works on a copy of the settings and staged lists of categories, server exceptions and
/// site logins; nothing is saved until <see cref="Services.OptionsService.Apply"/> runs on OK.
/// Simple switches bind straight to <see cref="Settings"/>; values that other controls react to are properties here.
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject
{
    private readonly OptionsContext _context;

    public OptionsViewModel(OptionsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        Settings = SettingsJson.Clone(context.Settings);
        var s = Settings;
        var protector = context.Protector;

        // General
        _launchOnStartup = context.LaunchOnStartup;
        foreach (var browser in BrowserCatalog.All)
        {
            Browsers.Add(new BrowserOption(browser, context.InstalledBrowsers.Contains(browser.Id),
                s.General.IntegratedBrowsers.Contains(browser.Id, StringComparer.OrdinalIgnoreCase)));
        }

        CaptureKeys = [.. Enum.GetValues<CaptureModifier>().Select(k => new Choice<CaptureModifier>(k, Localizer.Get("Key_" + k)))];
        Skins = [new Choice<string>("Default", Localizer.Get("Options_SkinDefault"))];
        Languages = [.. context.Languages.Select(c => new Choice<string>(c.Name, LanguageName(c)))];
        if (!Languages.Any(l => string.Equals(l.Value, s.General.Language, StringComparison.OrdinalIgnoreCase)))
        {
            s.General.Language = Languages[0].Value;
        }

        // File Types
        _autoCaptureExtensions = s.FileTypes.AutoCaptureExtensions;
        ExcludedSites = [.. s.FileTypes.ExcludedSites];
        ExcludedAddresses = [.. s.FileTypes.ExcludedAddresses];

        // Save To
        DefaultTempDirectory = context.Paths.DefaultTempDir;
        _tempDirectory = string.IsNullOrWhiteSpace(s.SaveTo.TempDirectory) ? DefaultTempDirectory : s.SaveTo.TempDirectory;
        _useSameDirectory = s.SaveTo.UseSameDirectoryForAllCategories;
        _sameDirectory = s.SaveTo.SameDirectory ?? context.Paths.UserDownloadsDir;
        foreach (var category in context.Categories)
        {
            Categories.Add(new CategoryEdit(category, BuiltInFolder(category)));
        }

        _selectedCategory = Categories.FirstOrDefault();

        // Downloads
        DuplicateActions = [.. Enum.GetValues<DuplicateDownloadAction>().Select(a => new Choice<DuplicateDownloadAction>(a, Localizer.Get("DupAction_" + a)))];
        DoubleClickActions = [.. Enum.GetValues<CompletedDoubleClickAction>().Select(a => new Choice<CompletedDoubleClickAction>(a, Localizer.Get("DoubleClick_" + a)))];
        _virusProgram = s.Downloads.VirusScan.Program;
        _virusArguments = s.Downloads.VirusScan.Arguments;

        // Connection
        ConnectionTypes = [.. Enum.GetValues<ConnectionSpeedType>().Select(t => new Choice<ConnectionSpeedType>(t, Localizer.Get("ConnType_" + t)))];
        ConnectionCounts = [.. ConnectionSettings.AllowedConnectionCounts];
        _connectionType = s.Connection.ConnectionType;
        _maxConnections = s.Connection.DefaultMaxConnections;
        foreach (var exception in context.ServerExceptions)
        {
            ServerExceptions.Add(new ServerExceptionEdit(exception.Id, exception.Host, exception.MaxConnections));
        }

        // Proxy
        _proxyMode = s.Proxy.Mode;
        SocksVersions = [.. Enum.GetValues<SocksVersion>().Select(v => new Choice<SocksVersion>(v, Localizer.Get("Socks_" + v)))];
        HttpPassword = protector.Unprotect(s.Proxy.Http.ProtectedPassword) ?? string.Empty;
        HttpsPassword = protector.Unprotect(s.Proxy.Https.ProtectedPassword) ?? string.Empty;
        FtpPassword = protector.Unprotect(s.Proxy.Ftp.ProtectedPassword) ?? string.Empty;
        SocksPassword = protector.Unprotect(s.Proxy.Socks.ProtectedPassword) ?? string.Empty;

        // Site Logins
        foreach (var login in context.SiteLogins)
        {
            SiteLogins.Add(new SiteLoginEdit(login.Id, login.UrlPattern, login.User, login.Password));
        }

        // Dial Up
        var entries = new List<Choice<string>> { new(string.Empty, Localizer.Get("Options_DialNone")) };
        entries.AddRange(context.RasEntries.Select(e => new Choice<string>(e, e)));
        if (!string.IsNullOrEmpty(s.DialUp.ConnectionName) && !context.RasEntries.Contains(s.DialUp.ConnectionName))
        {
            entries.Add(new Choice<string>(s.DialUp.ConnectionName, s.DialUp.ConnectionName));
        }

        RasEntries = entries;

        DialUpPassword = protector.Unprotect(s.DialUp.ProtectedPassword) ?? string.Empty;

        // Sounds
        foreach (var soundEvent in Enum.GetValues<SoundEvent>())
        {
            Sounds.Add(new SoundRow(soundEvent, s.Sounds.Get(soundEvent)));
        }
    }

    /// <summary>The settings being edited (a copy).</summary>
    public AppSettings Settings { get; }

    // ------------------------------------------------------------------ General

    [ObservableProperty]
    private bool _launchOnStartup;

    public ObservableCollection<BrowserOption> Browsers { get; } = [];

    public IReadOnlyList<Choice<CaptureModifier>> CaptureKeys { get; }

    public IReadOnlyList<Choice<string>> Skins { get; }

    public IReadOnlyList<Choice<string>> Languages { get; }

    // ------------------------------------------------------------------ File Types

    [ObservableProperty]
    private string _autoCaptureExtensions;

    public ObservableCollection<string> ExcludedSites { get; }

    public ObservableCollection<string> ExcludedAddresses { get; }

    [RelayCommand]
    private void ResetExtensions() => AutoCaptureExtensions = FileTypeSettings.DefaultAutoCaptureExtensions;

    // ------------------------------------------------------------------ Save To

    public ObservableCollection<CategoryEdit> Categories { get; } = [];

    /// <summary>Saved categories removed in this dialog.</summary>
    public List<long> DeletedCategoryIds { get; } = [];

    [ObservableProperty]
    private CategoryEdit? _selectedCategory;

    public string DefaultTempDirectory { get; }

    [ObservableProperty]
    private string _tempDirectory;

    [ObservableProperty]
    private bool _useSameDirectory;

    [ObservableProperty]
    private string _sameDirectory;

    /// <summary>The built-in folder of a category (what an empty folder means).</summary>
    public string BuiltInFolder(Category category)
    {
        var plain = new Category { Id = category.Id, Name = category.Name, IsBuiltIn = category.IsBuiltIn };
        var settings = SettingsJson.Clone(Settings);
        settings.SaveTo.UseSameDirectoryForAllCategories = false;
        return SaveLocationResolver.FolderFor(plain, settings, _context.Paths);
    }

    public void AddCategory(string name, string extensions, string? folder)
    {
        var category = new Category { Name = name, Extensions = extensions };
        var edit = new CategoryEdit(category, BuiltInFolder(category)) { Folder = folder ?? BuiltInFolder(category) };
        Categories.Add(edit);
        SelectedCategory = edit;
    }

    public bool RemoveCategory(CategoryEdit category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (category.IsBuiltIn)
        {
            return false;
        }

        if (category.Id != 0)
        {
            DeletedCategoryIds.Add(category.Id);
        }

        var index = Categories.IndexOf(category);
        Categories.Remove(category);
        SelectedCategory = Categories.Count == 0 ? null : Categories[Math.Clamp(index, 0, Categories.Count - 1)];
        return true;
    }

    // ------------------------------------------------------------------ Downloads

    public IReadOnlyList<Choice<DuplicateDownloadAction>> DuplicateActions { get; }

    public IReadOnlyList<Choice<CompletedDoubleClickAction>> DoubleClickActions { get; }

    [ObservableProperty]
    private string _virusProgram;

    [ObservableProperty]
    private string _virusArguments;

    [RelayCommand]
    private void UseWindowsDefender()
    {
        VirusProgram = VirusScanSettings.WindowsDefenderProgram;
        VirusArguments = VirusScanSettings.WindowsDefenderArguments;
        Settings.Downloads.VirusScan.Enabled = true;
        OnPropertyChanged(nameof(Settings));
    }

    // ------------------------------------------------------------------ Connection

    public IReadOnlyList<Choice<ConnectionSpeedType>> ConnectionTypes { get; }

    public IReadOnlyList<int> ConnectionCounts { get; }

    [ObservableProperty]
    private ConnectionSpeedType _connectionType;

    [ObservableProperty]
    private int _maxConnections;

    public ObservableCollection<ServerExceptionEdit> ServerExceptions { get; } = [];

    public List<long> DeletedServerExceptionIds { get; } = [];

    partial void OnConnectionTypeChanged(ConnectionSpeedType value)
    {
        if (value != ConnectionSpeedType.Custom)
        {
            MaxConnections = ConnectionSettings.ConnectionsFor(value);
        }
    }

    partial void OnMaxConnectionsChanged(int value)
    {
        if (ConnectionType != ConnectionSpeedType.Custom && ConnectionSettings.ConnectionsFor(ConnectionType) != value)
        {
            ConnectionType = ConnectionSpeedType.Custom;
        }
    }

    public void RemoveServerException(ServerExceptionEdit exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Id != 0)
        {
            DeletedServerExceptionIds.Add(exception.Id);
        }

        ServerExceptions.Remove(exception);
    }

    // ------------------------------------------------------------------ Proxy/Socks

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoProxy), nameof(IsSystemProxy), nameof(IsManualProxy), nameof(IsPacProxy))]
    private ProxyMode _proxyMode;

    public bool IsNoProxy
    {
        get => ProxyMode == ProxyMode.None;
        set => SetMode(value, ProxyMode.None);
    }

    public bool IsSystemProxy
    {
        get => ProxyMode == ProxyMode.System;
        set => SetMode(value, ProxyMode.System);
    }

    public bool IsManualProxy
    {
        get => ProxyMode == ProxyMode.Manual;
        set => SetMode(value, ProxyMode.Manual);
    }

    public bool IsPacProxy
    {
        get => ProxyMode == ProxyMode.AutoConfigScript;
        set => SetMode(value, ProxyMode.AutoConfigScript);
    }

    public IReadOnlyList<Choice<SocksVersion>> SocksVersions { get; }

    public string HttpPassword { get; set; }

    public string HttpsPassword { get; set; }

    public string FtpPassword { get; set; }

    public string SocksPassword { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestProxyCommand))]
    private string _proxyTestUrl = "https://www.example.com/";

    [ObservableProperty]
    private string _proxyTestResult = string.Empty;

    [RelayCommand(CanExecute = nameof(CanTestProxy))]
    private async Task TestProxyAsync()
    {
        if (!Uri.TryCreate(ProxyTestUrl.Trim(), UriKind.Absolute, out var url))
        {
            ProxyTestResult = Localizer.Get("Options_ProxyTestBadUrl");
            return;
        }

        ProxyTestResult = Localizer.Get("Options_ProxyTesting");
        var proxy = SettingsJson.Clone(Settings).Proxy;
        StoreProxy(proxy);
        var result = await ProxyTester.TestAsync(proxy, _context.Protector, url, TimeSpan.FromSeconds(Math.Max(5, Settings.Connection.TimeoutSeconds)), _context.PacResolver);
        ProxyTestResult = Localizer.Format(result.Success ? "Options_ProxyTestOk" : "Options_ProxyTestFailed",
            result.Message, Math.Round(result.Elapsed.TotalMilliseconds));
    }

    private bool CanTestProxy() => !string.IsNullOrWhiteSpace(ProxyTestUrl);

    private void SetMode(bool selected, ProxyMode mode)
    {
        if (selected)
        {
            ProxyMode = mode;
        }
    }

    // ------------------------------------------------------------------ Site Logins

    public ObservableCollection<SiteLoginEdit> SiteLogins { get; } = [];

    public List<long> DeletedSiteLoginIds { get; } = [];

    public void RemoveSiteLogin(SiteLoginEdit login)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (login.Id != 0)
        {
            DeletedSiteLoginIds.Add(login.Id);
        }

        SiteLogins.Remove(login);
    }

    // ------------------------------------------------------------------ Dial Up / VPN

    /// <summary>"(None)" and the phonebook entries.</summary>
    public IReadOnlyList<Choice<string>> RasEntries { get; }

    public string DialUpPassword { get; set; }

    // ------------------------------------------------------------------ Sounds

    public ObservableCollection<SoundRow> Sounds { get; } = [];

    // ------------------------------------------------------------------ OK

    /// <summary>Checks what bindings can't (paths, required fields). Null when everything is fine.</summary>
    public OptionsError? Validate()
    {
        if (!IsValidPath(TempDirectory))
        {
            return new OptionsError(OptionsPage.SaveTo, Localizer.Get("Options_ErrorTempDir"));
        }

        if (UseSameDirectory && !IsValidPath(SameDirectory))
        {
            return new OptionsError(OptionsPage.SaveTo, Localizer.Get("Options_ErrorSameDir"));
        }

        if (Categories.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Folder) && !IsValidPath(c.Folder)) is { } badCategory)
        {
            SelectedCategory = badCategory;
            return new OptionsError(OptionsPage.SaveTo, Localizer.Format("Options_ErrorCategoryDir", badCategory.Title));
        }

        if (Settings.Downloads.VirusScan.Enabled && string.IsNullOrWhiteSpace(VirusProgram))
        {
            return new OptionsError(OptionsPage.Downloads, Localizer.Get("Options_ErrorVirusProgram"));
        }

        if (Settings.Downloads.UseCustomUserAgent && string.IsNullOrWhiteSpace(Settings.Downloads.CustomUserAgent))
        {
            return new OptionsError(OptionsPage.Downloads, Localizer.Get("Options_ErrorUserAgent"));
        }

        if (Settings.Downloads.UseCustomUserAgent && Settings.Downloads.CustomUserAgent.Any(char.IsControl))
        {
            return new OptionsError(OptionsPage.Downloads, Localizer.Get("Options_ErrorUserAgent"));
        }

        if (ProxyMode == ProxyMode.AutoConfigScript
            && (!Uri.TryCreate(Settings.Proxy.PacUrl.Trim(), UriKind.Absolute, out var pac) || pac.Scheme is not ("http" or "https" or "file")))
        {
            return new OptionsError(OptionsPage.Proxy, Localizer.Get("Options_ErrorPacUrl"));
        }

        if (ProxyMode == ProxyMode.Manual)
        {
            var proxy = Settings.Proxy;
            var servers = proxy.UseSameProxyForAllProtocols ? [proxy.Http] : new[] { proxy.Http, proxy.Https, proxy.Ftp };
            if (servers.All(p => string.IsNullOrWhiteSpace(p.Host)) && !(proxy.Socks.Enabled && !string.IsNullOrWhiteSpace(proxy.Socks.Host)))
            {
                return new OptionsError(OptionsPage.Proxy, Localizer.Get("Options_ErrorProxyHost"));
            }

            if (servers.Any(p => !IsValidHost(p.Host)) || (proxy.Socks.Enabled && !IsValidHost(proxy.Socks.Host)))
            {
                return new OptionsError(OptionsPage.Proxy, Localizer.Get("Options_ErrorProxyHost"));
            }

            if (servers.Any(p => p.Port is < 1 or > 65535) || proxy.Socks.Port is < 1 or > 65535)
            {
                return new OptionsError(OptionsPage.Proxy, Localizer.Get("Options_ErrorPort"));
            }
        }

        return null;
    }

    /// <summary>Writes the lists and secrets back into <see cref="Settings"/> (call after <see cref="Validate"/>).</summary>
    public AppSettings BuildSettings()
    {
        var s = Settings;
        s.General.IntegratedBrowsers = [.. Browsers.Where(b => b.IsIntegrated).Select(b => b.Browser.Id)];
        s.FileTypes.AutoCaptureExtensions = string.Join(' ', CategoryMatcher.SplitPatterns(AutoCaptureExtensions));
        s.FileTypes.ExcludedSites = [.. ExcludedSites];
        s.FileTypes.ExcludedAddresses = [.. ExcludedAddresses];
        s.SaveTo.TempDirectory = SamePath(TempDirectory, DefaultTempDirectory) ? null : TempDirectory.Trim();
        s.SaveTo.UseSameDirectoryForAllCategories = UseSameDirectory;
        s.SaveTo.SameDirectory = string.IsNullOrWhiteSpace(SameDirectory) ? null : SameDirectory.Trim();
        s.Downloads.VirusScan.Program = VirusProgram.Trim();
        s.Downloads.VirusScan.Arguments = VirusArguments.Trim();
        s.Downloads.CustomUserAgent = s.Downloads.CustomUserAgent.Trim();
        s.Connection.ConnectionType = ConnectionType;
        s.Connection.DefaultMaxConnections = MaxConnections;
        StoreProxy(s.Proxy);
        s.DialUp.ProtectedPassword = _context.Protector.Protect(DialUpPassword);
        foreach (var row in Sounds)
        {
            var entry = s.Sounds.Get(row.Event);
            entry.Enabled = row.Enabled;
            entry.Path = row.Path.Trim();
        }

        return s;
    }

    /// <summary>The temp folder that will be used (for moving partial downloads when it changes).</summary>
    public static string EffectiveTempDirectory(AppSettings settings, AppPaths paths) =>
        Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(settings.SaveTo.TempDirectory) ? paths.DefaultTempDir : settings.SaveTo.TempDirectory);

    private void StoreProxy(ProxySettings proxy)
    {
        var protector = _context.Protector;
        proxy.Mode = ProxyMode;
        proxy.PacUrl = proxy.PacUrl.Trim();
        proxy.Http.Host = proxy.Http.Host.Trim();
        proxy.Https.Host = proxy.Https.Host.Trim();
        proxy.Ftp.Host = proxy.Ftp.Host.Trim();
        proxy.Socks.Host = proxy.Socks.Host.Trim();
        proxy.Http.ProtectedPassword = protector.Protect(HttpPassword);
        proxy.Https.ProtectedPassword = protector.Protect(HttpsPassword);
        proxy.Ftp.ProtectedPassword = protector.Protect(FtpPassword);
        proxy.Socks.ProtectedPassword = protector.Protect(SocksPassword);
    }

    private static string LanguageName(CultureInfo culture)
    {
        var native = culture.NativeName;
        return string.Equals(native, culture.EnglishName, StringComparison.Ordinal) ? native : $"{native} ({culture.EnglishName})";
    }

    private static bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            return Path.IsPathFullyQualified(expanded) && expanded.IndexOfAny(Path.GetInvalidPathChars()) < 0
                && Path.GetFullPath(expanded).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsValidHost(string host) =>
        string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim().Trim('[', ']')) != UriHostNameType.Unknown;

    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
