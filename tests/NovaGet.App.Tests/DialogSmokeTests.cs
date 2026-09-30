using System.Windows;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Paths;
using NovaGet.Core.Settings;

namespace NovaGet.App.Tests;

/// <summary>Every dialog must at least load its XAML and lay out.</summary>
[Collection(WpfCollection.Name)]
public sealed class DialogSmokeTests(WpfFixture wpf)
{
    public static TheoryData<string> Dialogs =>
    [
        "About", "TellAFriend", "Find", "SpeedLimiter", "Input", "Category", "MessageCheck", "Toolbar", "Columns",
    ];

    [Theory]
    [MemberData(nameof(Dialogs))]
    public void Dialog_loads(string name)
    {
        wpf.Run(() =>
        {
            Window dialog = name switch
            {
                "About" => new AboutDialog(),
                "TellAFriend" => new TellAFriendDialog(),
                "Find" => new FindDialog("abc", matchCase: true),
                "SpeedLimiter" => new SpeedLimiterDialog(256, queueOnly: true),
                "Input" => new InputDialog("Title", "Prompt", "value"),
                "Category" => new CategoryDialog(null, new AppSettings(), AppPaths.ForRoot(Path.GetTempPath())),
                "MessageCheck" => new MessageCheckDialog("Title", "Message?", "Check", true),
                "Toolbar" => ReorderDialogs.ForToolbar([("A", "Alpha", NovaGet.App.Services.AppImages.Get("resume", 48), true)], ["A"]),
                "Columns" => ReorderDialogs.ForColumns([("FileName", "File Name", true), ("Size", "Size", false)], [("FileName", "File Name", true), ("Size", "Size", true)], "FileName"),
                _ => throw new ArgumentOutOfRangeException(nameof(name)),
            };
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10000;
            dialog.Show();
            dialog.UpdateLayout();
            Assert.True(dialog.ActualWidth > 0);
            dialog.Close();
        });
    }
}
