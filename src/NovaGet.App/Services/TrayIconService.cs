using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using NovaGet.Core;

namespace NovaGet.App.Services;

/// <summary>The notification-area icon. Always visible while the app runs.</summary>
internal sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _icon;

    public void Initialize(IAppController controller)
    {
        if (_icon is not null)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(CreateItem("Open NovaGet", controller.ShowMainWindow, bold: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("Exit", controller.RequestExit));

        _icon = new TaskbarIcon
        {
            ToolTipText = AppInfo.ProductName,
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/novaget.ico", UriKind.Absolute)),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayMouseDoubleClick += (_, _) => controller.ShowMainWindow();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void SetToolTip(string text)
    {
        if (_icon is not null)
        {
            _icon.ToolTipText = text;
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private static MenuItem CreateItem(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold)
        {
            item.FontWeight = System.Windows.FontWeights.Bold;
        }

        item.Click += (_, _) => action();
        return item;
    }
}
