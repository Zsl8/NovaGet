using System.Windows;

namespace NovaGet.App.Views.Dialogs;

public partial class FindDialog : DialogWindow
{
    public FindDialog(string text, bool matchCase)
    {
        InitializeComponent();
        SearchBox.Text = text;
        SearchBox.SelectAll();
        MatchCaseBox.IsChecked = matchCase;
    }

    public string SearchText => SearchBox.Text;

    public bool MatchCase => MatchCaseBox.IsChecked == true;

    private void OnFind(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            Accept();
        }
    }
}
