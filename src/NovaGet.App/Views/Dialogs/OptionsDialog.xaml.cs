using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.App.ViewModels.Options;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Downloads → Options (section 9): ten tabs, saved together on OK.</summary>
public partial class OptionsDialog : DialogWindow
{
    private readonly OptionsService _options;
    private readonly SettingsPackageService _package;
    private readonly SoundService? _sounds;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly Action<string?> _showHelp;
    private OptionsViewModel _vm;

    internal OptionsDialog(
        OptionsService options,
        SettingsPackageService package,
        SoundService? sounds,
        IDialogService dialogs,
        AppPaths paths,
        Action<string?> showHelp,
        OptionsPage page = OptionsPage.General)
    {
        _options = options;
        _package = package;
        _sounds = sounds;
        _dialogs = dialogs;
        _paths = paths;
        _showHelp = showHelp;
        InitializeComponent();
        _vm = options.CreateViewModel();
        Load();
        Tabs.SelectedIndex = (int)page;

        HttpPasswordBox.PasswordChanged += (_, _) => _vm.HttpPassword = HttpPasswordBox.Password;
        HttpsPasswordBox.PasswordChanged += (_, _) => _vm.HttpsPassword = HttpsPasswordBox.Password;
        FtpPasswordBox.PasswordChanged += (_, _) => _vm.FtpPassword = FtpPasswordBox.Password;
        SocksPasswordBox.PasswordChanged += (_, _) => _vm.SocksPassword = SocksPasswordBox.Password;
        DialPasswordBox.PasswordChanged += (_, _) => _vm.DialUpPassword = DialPasswordBox.Password;
    }

    internal OptionsViewModel ViewModel => _vm;

    /// <summary>Set after OK.</summary>
    internal OptionsApplyResult? Result { get; private set; }

    internal void SelectPage(OptionsPage page) => Tabs.SelectedIndex = (int)page;

    private void Load()
    {
        DataContext = _vm;
        HttpPasswordBox.Password = _vm.HttpPassword;
        HttpsPasswordBox.Password = _vm.HttpsPassword;
        FtpPasswordBox.Password = _vm.FtpPassword;
        SocksPasswordBox.Password = _vm.SocksPassword;
        DialPasswordBox.Password = _vm.DialUpPassword;
    }

    // ------------------------------------------------------------------ OK / Help

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // Commit the focused text box (its binding updates on lost focus).
        (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        if (FindInvalidInput(Tabs) is { } invalid)
        {
            var tab = FindParentTab(invalid);
            if (tab is not null)
            {
                Tabs.SelectedItem = tab;
            }

            _dialogs.Error(Localizer.Get("Error_InvalidNumber"));
            Dispatcher.BeginInvoke(() => (invalid as IInputElement)?.Focus());
            return;
        }

        if (_vm.Validate() is { } error)
        {
            SelectPage(error.Page);
            _dialogs.Error(error.Message);
            return;
        }

        if (!_options.CanChangeTempDirectory(_vm))
        {
            SelectPage(OptionsPage.SaveTo);
            _dialogs.Error(Localizer.Get("Options_ErrorTempBusy"));
            return;
        }

        Result = _options.Apply(_vm);
        if (Result.StartupFailed)
        {
            _dialogs.Error(Localizer.Get("Options_ErrorStartup"));
        }

        if (Result.TempFoldersNotMoved.Count > 0)
        {
            _dialogs.Error(Localizer.Get("Options_ErrorTempMove"));
        }

        if (Result.LanguageChanged)
        {
            _dialogs.Info(Localizer.Get("Msg_LanguageRestart"));
        }

        Accept();
    }

    private void OnHelp(object sender, RoutedEventArgs e) => _showHelp("options");

    private static DependencyObject? FindInvalidInput(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (Validation.GetHasError(child))
            {
                return child;
            }

            if (FindInvalidInput(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static TabItem? FindParentTab(DependencyObject element)
    {
        for (var current = element; current is not null; current = LogicalTreeHelper.GetParent(current))
        {
            if (current is TabItem tab)
            {
                return tab;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ General

    private void OnInstallExtension(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserOption option)
        {
            return;
        }

        var guide = Path.Combine(_paths.ExecutableDir, "docs", "install-extension.html");
        if (File.Exists(guide))
        {
            ShellService.OpenUrl(new Uri(guide).AbsoluteUri + "#" + option.Browser.HelpAnchor);
        }
        else
        {
            _dialogs.Error(Localizer.Format("Error_FileMissing", guide));
        }
    }

    private void OnEditContextMenu(object sender, RoutedEventArgs e)
    {
        var dialog = new ContextMenuItemsDialog(_vm.Settings.General.ContextMenu) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.Settings.General.ContextMenu = dialog.Result;
        }
    }

    private void OnEditWebPanel(object sender, RoutedEventArgs e)
    {
        var dialog = new WebPlayerPanelDialog(_vm.Settings.General.WebPlayerPanel) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.Settings.General.WebPlayerPanel = dialog.Result;
        }
    }

    // ------------------------------------------------------------------ File Types

    private (System.Collections.ObjectModel.ObservableCollection<string> List, ListBox Box, bool IsSite) PatternList(object sender) =>
        (sender as FrameworkElement)?.Tag as string == "addresses"
            ? (_vm.ExcludedAddresses, AddressesList, false)
            : (_vm.ExcludedSites, SitesList, true);

    private void OnAddPattern(object sender, RoutedEventArgs e)
    {
        var (list, box, isSite) = PatternList(sender);
        if (AskPattern(isSite, string.Empty) is { } value && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
            box.SelectedItem = value;
        }
    }

    private void OnEditPattern(object sender, RoutedEventArgs e)
    {
        var (list, box, isSite) = PatternList(sender);
        if (box.SelectedItem is not string current)
        {
            return;
        }

        if (AskPattern(isSite, current) is { } value)
        {
            list[list.IndexOf(current)] = value;
            box.SelectedItem = value;
        }
    }

    private void OnDeletePattern(object sender, RoutedEventArgs e)
    {
        var (list, box, _) = PatternList(sender);
        if (box.SelectedItem is string current)
        {
            var index = list.IndexOf(current);
            list.Remove(current);
            if (list.Count > 0)
            {
                box.SelectedIndex = Math.Min(index, list.Count - 1);
            }
        }
    }

    private string? AskPattern(bool isSite, string value)
    {
        var dialog = new InputDialog(
            Localizer.Get(isSite ? "Options_ExcludedSiteTitle" : "Options_ExcludedAddressTitle"),
            Localizer.Get(isSite ? "Options_ExcludedSitePrompt" : "Options_ExcludedAddressPrompt"),
            value,
            text => text.Length > (isSite ? 255 : 2048) || text.Any(char.IsWhiteSpace) ? Localizer.Get("Options_ErrorPattern") : null)
        { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    // ------------------------------------------------------------------ Save To

    private void OnAddCategory(object sender, RoutedEventArgs e)
    {
        var dialog = new CategoryDialog(null, _vm.Settings, _paths) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (_vm.Categories.Any(c => string.Equals(c.Name, dialog.CategoryName, StringComparison.CurrentCultureIgnoreCase)
            || string.Equals(c.Title, dialog.CategoryName, StringComparison.CurrentCultureIgnoreCase)))
        {
            _dialogs.Error(Localizer.Get("Options_ErrorCategoryExists"));
            return;
        }

        _vm.AddCategory(dialog.CategoryName, dialog.Extensions, dialog.Folder);
    }

    private void OnEditCategory(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedCategory is not { } edit)
        {
            return;
        }

        var category = new Category
        {
            Id = edit.Id,
            Name = edit.Name,
            IsBuiltIn = edit.IsBuiltIn,
            Extensions = edit.Extensions,
            DefaultSaveDir = edit.FolderToSave,
        };
        var dialog = new CategoryDialog(category, _vm.Settings, _paths) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!edit.IsBuiltIn)
        {
            edit.Name = dialog.CategoryName;
        }

        edit.Extensions = dialog.Extensions;
        edit.Folder = dialog.Folder ?? edit.DefaultFolder;
    }

    private void OnDeleteCategory(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedCategory is { IsBuiltIn: false } edit
            && _dialogs.Confirm(Localizer.Format("Confirm_DeleteCategory", edit.Title)))
        {
            _vm.RemoveCategory(edit);
        }
    }

    private void OnBrowseCategoryFolder(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedCategory is { } edit && _dialogs.PickFolder(edit.Folder) is { } folder)
        {
            edit.Folder = folder;
        }
    }

    private void OnBrowseSameFolder(object sender, RoutedEventArgs e)
    {
        if (_dialogs.PickFolder(_vm.SameDirectory) is { } folder)
        {
            _vm.SameDirectory = folder;
        }
    }

    private void OnBrowseTempFolder(object sender, RoutedEventArgs e)
    {
        if (_dialogs.PickFolder(_vm.TempDirectory) is { } folder)
        {
            _vm.TempDirectory = folder;
        }
    }

    // ------------------------------------------------------------------ Downloads

    private void OnBrowseScanner(object sender, RoutedEventArgs e)
    {
        if (_dialogs.PickOpenFile(Localizer.Get("Filter_Programs")) is { } program)
        {
            _vm.VirusProgram = program;
        }
    }

    // ------------------------------------------------------------------ Connection

    private void OnAddServerException(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerExceptionDialog(string.Empty, _vm.MaxConnections) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var existing = _vm.ServerExceptions.FirstOrDefault(x => string.Equals(x.Host, dialog.Host, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.MaxConnections = dialog.MaxConnections;
                ExceptionsList.SelectedItem = existing;
                return;
            }

            var added = new ServerExceptionEdit(0, dialog.Host, dialog.MaxConnections);
            _vm.ServerExceptions.Add(added);
            ExceptionsList.SelectedItem = added;
        }
    }

    private void OnEditServerException(object sender, RoutedEventArgs e)
    {
        if (ExceptionsList.SelectedItem is not ServerExceptionEdit edit)
        {
            return;
        }

        var dialog = new ServerExceptionDialog(edit.Host, edit.MaxConnections) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            edit.Host = dialog.Host;
            edit.MaxConnections = dialog.MaxConnections;
            ExceptionsList.Items.Refresh();
        }
    }

    private void OnDeleteServerException(object sender, RoutedEventArgs e)
    {
        if (ExceptionsList.SelectedItem is ServerExceptionEdit edit)
        {
            _vm.RemoveServerException(edit);
        }
    }

    // ------------------------------------------------------------------ Site Logins

    private void OnAddLogin(object sender, RoutedEventArgs e)
    {
        var dialog = new SiteLoginDialog(string.Empty, string.Empty, string.Empty) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var added = new SiteLoginEdit(0, dialog.UrlPattern, dialog.User, dialog.Password);
            _vm.SiteLogins.Add(added);
            LoginsList.SelectedItem = added;
        }
    }

    private void OnEditLogin(object sender, RoutedEventArgs e)
    {
        if (LoginsList.SelectedItem is not SiteLoginEdit edit)
        {
            return;
        }

        var dialog = new SiteLoginDialog(edit.UrlPattern, edit.User, edit.Password) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            edit.UrlPattern = dialog.UrlPattern;
            edit.User = dialog.User;
            edit.Password = dialog.Password;
            edit.IsChanged = true;
            LoginsList.Items.Refresh();
        }
    }

    private void OnDeleteLogin(object sender, RoutedEventArgs e)
    {
        if (LoginsList.SelectedItem is SiteLoginEdit edit && _dialogs.Confirm(Localizer.Format("Options_ConfirmDeleteLogin", edit.UrlPattern)))
        {
            _vm.RemoveSiteLogin(edit);
        }
    }

    // ------------------------------------------------------------------ Sounds

    private void OnBrowseSound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundRow row && _dialogs.PickOpenFile(Localizer.Get("Filter_Wave")) is { } file)
        {
            row.Path = file;
            row.Enabled = true;
        }
    }

    private void OnPlaySound(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SoundRow row && _sounds?.Preview(row.Path) != true)
        {
            _dialogs.Error(Localizer.Format("Options_ErrorSound", row.Path));
        }
    }

    // ------------------------------------------------------------------ Advanced

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e) => ShellService.OpenFolder(_paths.SettingsFile);

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        var path = _dialogs.PickSaveFile("NovaGet-settings.json", Localizer.Get("Filter_Settings"));
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, _package.Export());
            _dialogs.Info(Localizer.Get("Options_Exported"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error(ex.Message);
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        var path = _dialogs.PickOpenFile(Localizer.Get("Filter_Settings"));
        if (path is null || !_dialogs.Confirm(Localizer.Get("Options_ConfirmImport")))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > NovaGet.Core.Settings.SettingsTransfer.MaxFileBytes)
            {
                throw new FormatException(Localizer.Get("Options_ErrorImport"));
            }

            _package.Import(File.ReadAllText(path));
            Reload();
            _dialogs.Info(Localizer.Get("Options_Imported"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _dialogs.Error(Localizer.Format("Options_ErrorImportDetails", ex.Message));
        }
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (_dialogs.Confirm(Localizer.Get("Options_ConfirmReset")))
        {
            _package.ResetToDefaults();
            Reload();
        }
    }

    /// <summary>Shows the saved settings again after an import or reset (other edits in the dialog are dropped).</summary>
    private void Reload()
    {
        var page = Tabs.SelectedIndex;
        _vm = _options.CreateViewModel();
        Load();
        Tabs.SelectedIndex = page;
        if (!string.Equals(Localizer.Culture.Name, _vm.Settings.General.Language, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Info(Localizer.Get("Msg_LanguageRestart"));
        }
    }
}
