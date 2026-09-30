using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NovaGet.App.ViewModels;
using NovaGet.App.Views;
using NovaGet.Core.Models;
using NovaGet.Core.Services;

namespace NovaGet.App.Tests;

[Collection(WpfCollection.Name)]
public sealed class MainWindowSmokeTests(WpfFixture wpf)
{
    [Fact]
    public void Main_window_loads_with_menus_toolbar_tree_and_columns()
    {
        using var host = new AppHost();
        host.Get<IDownloadService>().Add(new DownloadRequest { Url = "https://example.com/a.zip", FileName = "a.zip" });

        wpf.Run(() =>
        {
            var window = host.Get<MainWindow>();
            window.Left = -10000;
            window.Show();
            window.UpdateLayout();
            var vm = (MainViewModel)window.DataContext;

            Assert.Equal(12, vm.Toolbar.Count);
            Assert.Equal(5, vm.Tree.Count);
            Assert.Equal(5, vm.Tree[0].Children.Count);             // the built-in categories
            Assert.Equal(2, vm.Tree[4].Children.Count);             // main + sync queues
            Assert.Single(vm.ItemsView.Cast<object>());
            var grid = (DataGrid)window.FindName("DownloadList");
            Assert.Equal(12, grid.Columns.Count);
            Assert.Equal(5, ((Menu)LogicalChild<Menu>(window)!).Items.Count);
            window.Hide();
        });
    }

    [Fact]
    public void Ten_thousand_rows_stay_responsive()
    {
        using var host = new AppHost();
        var service = host.Get<IDownloadService>();
        for (var i = 0; i < 10_000; i++)
        {
            service.Add(new DownloadRequest { Url = $"https://example.com/file{i}.zip", FileName = $"file{i:D5}.zip", Size = i * 1024L });
        }

        wpf.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            var window = host.Get<MainWindow>();
            window.Left = -10000;
            window.Show();
            window.UpdateLayout();
            var loaded = clock.Elapsed;
            var vm = (MainViewModel)window.DataContext;
            Assert.Equal(10_000, vm.Items.Count);

            clock.Restart();
            vm.ToggleSort("Size");
            vm.ToggleSort("Size");
            vm.SelectedNode = vm.Tree[1]; // Unfinished
            window.UpdateLayout();
            var reshaped = clock.Elapsed;

            Assert.True(loaded < TimeSpan.FromSeconds(10), $"window took {loaded.TotalSeconds:0.0} s to load");
            Assert.True(reshaped < TimeSpan.FromSeconds(3), $"sort + filter took {reshaped.TotalSeconds:0.0} s");
            window.Hide();
        });
    }

    [Fact]
    public void Downloads_follow_status_changes_into_finished()
    {
        using var host = new AppHost();
        var service = host.Get<IDownloadService>();
        var added = service.Add(new DownloadRequest { Url = "https://example.com/b.zip", FileName = "b.zip" });

        wpf.Run(() =>
        {
            var window = host.Get<MainWindow>();
            window.Left = -10000;
            window.Show();
            var vm = (MainViewModel)window.DataContext;
            vm.SelectedNode = vm.Tree[2]; // Finished
            Assert.Empty(vm.ItemsView.Cast<object>());

            var download = service.Find(added.Id)!;
            download.Status = DownloadStatus.Completed;
            service.Save(download);
            window.UpdateLayout();

            Assert.Single(vm.ItemsView.Cast<object>());
            window.Hide();
        });
    }

    private static T? LogicalChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                return match;
            }

            if (LogicalChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
