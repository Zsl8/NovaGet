using System.Windows;
using Microsoft.Win32;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Views.Dialogs;

/// <summary>Add or edit a category: name (fixed for built-ins), extensions and save folder.</summary>
public partial class CategoryDialog : DialogWindow
{
    private readonly string _defaultFolder;

    public CategoryDialog(Category? category, AppSettings settings, AppPaths paths)
    {
        InitializeComponent();
        Title = Localizer.Get(category is null ? "CategoryDialog_AddTitle" : "CategoryDialog_EditTitle");
        // The built-in default for this category, i.e. what applies when no custom folder is set.
        var withoutCustomFolder = new Category { Id = category?.Id ?? 0, Name = category?.Name ?? "Other", IsBuiltIn = category?.IsBuiltIn ?? false };
        _defaultFolder = SaveLocationResolver.FolderFor(withoutCustomFolder, settings, paths);
        NameBox.Text = category is null ? string.Empty : MainViewModel.CategoryTitle(category);
        NameBox.IsReadOnly = category?.IsBuiltIn == true;
        ExtensionsBox.Text = category?.Extensions ?? string.Empty;
        FolderBox.Text = category?.DefaultSaveDir ?? _defaultFolder;
    }

    public string CategoryName => NameBox.Text.Trim();

    public string Extensions => string.Join(' ', CategoryMatcher.SplitPatterns(ExtensionsBox.Text));

    /// <summary>Null when the folder is left at the built-in default.</summary>
    public string? Folder =>
        string.IsNullOrWhiteSpace(FolderBox.Text) || string.Equals(FolderBox.Text.Trim(), _defaultFolder, StringComparison.OrdinalIgnoreCase)
            ? null
            : FolderBox.Text.Trim();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (CategoryName.Length == 0)
        {
            ErrorText.Text = Localizer.Get("Error_NameRequired");
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }

        Accept();
    }
}
