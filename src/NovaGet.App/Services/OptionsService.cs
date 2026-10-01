using System.IO;
using Microsoft.Extensions.Logging;
using NovaGet.App.ViewModels.Options;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Engine.Storage;
using NovaGet.Core.Models;
using NovaGet.Core.Network;
using NovaGet.Core.Paths;
using NovaGet.Core.Security;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>What happened when the Options dialog's OK was applied.</summary>
public sealed record OptionsApplyResult(bool LanguageChanged, bool CategoriesChanged, bool StartupFailed, IReadOnlyList<string> TempFoldersNotMoved);

/// <summary>Builds the Options dialog's state and saves it on OK.</summary>
public sealed class OptionsService(
    ISettingsService settings,
    ICategoryRepository categories,
    IServerExceptionRepository serverExceptions,
    ISiteLoginRepository siteLogins,
    IDownloadService downloads,
    ISecretProtector protector,
    AppPaths paths,
    ILogger<OptionsService> logger,
    SiteCredentials? siteCredentials = null,
    NovaGet.Core.Network.IPacResolver? pacResolver = null)
{
    /// <summary>Raised after options were applied (on the UI thread).</summary>
    public event EventHandler<OptionsApplyResult>? Applied;

    public OptionsViewModel CreateViewModel() => new(new OptionsContext
    {
        Settings = settings.Current,
        Paths = paths,
        Protector = protector,
        LaunchOnStartup = OperatingSystem.IsWindows() && StartupRegistration.IsEnabled(),
        InstalledBrowsers = OperatingSystem.IsWindows() ? BrowserDetector.InstalledBrowserIds() : new HashSet<string>(),
        RasEntries = RasPhonebook.ReadEntries(RasPhonebook.DefaultFiles()),
        Languages = Localization.Localizer.AvailableLanguages(System.IO.Path.Combine(paths.ExecutableDir, "lang")),
        Categories = categories.GetAll(),
        ServerExceptions = serverExceptions.GetAll(),
        SiteLogins = siteLogins.GetAll(),
        PacResolver = pacResolver,
    });

    /// <summary>The temporary folder can only move while nothing is downloading.</summary>
    public bool CanChangeTempDirectory(OptionsViewModel options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !TempDirectoryChanges(options) || downloads.GetStatistics().Active == 0;
    }

    public OptionsApplyResult Apply(OptionsViewModel options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var edited = options.BuildSettings();
        var before = settings.Current;
        var languageChanged = !string.Equals(before.General.Language, edited.General.Language, StringComparison.OrdinalIgnoreCase);
        var oldTemp = OptionsViewModel.EffectiveTempDirectory(before, paths);
        var newTemp = OptionsViewModel.EffectiveTempDirectory(edited, paths);

        var categoriesChanged = SaveCategories(options);
        SaveServerExceptions(options);
        SaveSiteLogins(options);
        siteCredentials?.Invalidate();

        IReadOnlyList<string> notMoved = [];
        if (!string.Equals(oldTemp, newTemp, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var moved = TempDirectoryMover.Move(oldTemp, newTemp);
                notMoved = moved.Failed;
                logger.LogInformation("Temp folder changed to {Folder}; moved {Count} unfinished downloads", newTemp, moved.Moved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not move temp files to {Folder}", newTemp);
                notMoved = ["*"];
            }
        }

        settings.Update(s => OptionsApplier.Apply(s, edited));

        var startupFailed = false;
        if (OperatingSystem.IsWindows() && options.LaunchOnStartup != StartupRegistration.IsEnabled())
        {
            startupFailed = !StartupRegistration.Set(options.LaunchOnStartup);
        }

        var result = new OptionsApplyResult(languageChanged, categoriesChanged, startupFailed, notMoved);
        Applied?.Invoke(this, result);
        return result;
    }

    private bool TempDirectoryChanges(OptionsViewModel options)
    {
        var current = OptionsViewModel.EffectiveTempDirectory(settings.Current, paths);
        var edited = string.IsNullOrWhiteSpace(options.TempDirectory) ? paths.DefaultTempDir : Environment.ExpandEnvironmentVariables(options.TempDirectory.Trim());
        return !string.Equals(current.TrimEnd('\\', '/'), edited.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private bool SaveCategories(OptionsViewModel options)
    {
        var changed = false;
        foreach (var id in options.DeletedCategoryIds)
        {
            changed |= categories.Delete(id);
        }

        foreach (var edit in options.Categories.Where(c => c.IsChanged))
        {
            changed = true;
            var extensions = string.Join(' ', CategoryMatcher.SplitPatterns(edit.Extensions));
            if (edit.Id == 0)
            {
                categories.Insert(new Category { Name = edit.Name.Trim(), Extensions = extensions, DefaultSaveDir = edit.FolderToSave });
                continue;
            }

            var category = categories.Get(edit.Id);
            if (category is null)
            {
                continue;
            }

            if (!category.IsBuiltIn)
            {
                category.Name = edit.Name.Trim();
            }

            category.Extensions = extensions;
            category.DefaultSaveDir = edit.FolderToSave;
            categories.Update(category);
        }

        return changed;
    }

    private void SaveServerExceptions(OptionsViewModel options)
    {
        foreach (var id in options.DeletedServerExceptionIds)
        {
            serverExceptions.Delete(id);
        }

        var saved = serverExceptions.GetAll().ToDictionary(e => e.Id);
        foreach (var edit in options.ServerExceptions)
        {
            var exception = new ServerException { Id = edit.Id, Host = edit.Host.Trim(), MaxConnections = edit.MaxConnections };
            if (edit.Id == 0)
            {
                serverExceptions.Insert(exception);
            }
            else if (saved.TryGetValue(edit.Id, out var old) && (old.Host != exception.Host || old.MaxConnections != exception.MaxConnections))
            {
                serverExceptions.Update(exception);
            }
        }
    }

    private void SaveSiteLogins(OptionsViewModel options)
    {
        foreach (var id in options.DeletedSiteLoginIds)
        {
            siteLogins.Delete(id);
        }

        foreach (var edit in options.SiteLogins.Where(l => l.Id == 0 || l.IsChanged))
        {
            var login = new SiteLogin { Id = edit.Id, UrlPattern = edit.UrlPattern.Trim(), User = edit.User, Password = edit.Password };
            if (edit.Id == 0)
            {
                siteLogins.Insert(login);
            }
            else
            {
                siteLogins.Update(login);
            }
        }
    }
}
