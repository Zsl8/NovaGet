using System.Windows;

namespace NovaGet.App.Views.Dialogs;

/// <summary>"Show queue selection panel on Download Later".</summary>
public partial class QueuePickDialog : DialogWindow
{
    public QueuePickDialog(IReadOnlyList<ChoiceItem> queues)
    {
        InitializeComponent();
        QueueList.ItemsSource = queues;
        QueueList.SelectedIndex = 0;
    }

    public long? SelectedQueueId => (QueueList.SelectedItem as ChoiceItem)?.Id;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (SelectedQueueId is not null)
        {
            Accept();
        }
    }
}
