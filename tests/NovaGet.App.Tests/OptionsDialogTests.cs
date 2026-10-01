using System.Windows;
using NovaGet.App.Services;
using NovaGet.App.ViewModels.Options;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Tests;

[Collection(WpfCollection.Name)]
public sealed class OptionsDialogTests(WpfFixture wpf)
{
    [Fact]
    public void Every_tab_loads()
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var dialog = new OptionsDialog(
                app.Get<OptionsService>(), app.Get<SettingsPackageService>(), app.Get<SoundService>(), app.Get<IDialogService>(),
                app.Paths, _ => { }, OptionsPage.Proxy)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
            };
            dialog.Show();
            Assert.Equal((int)OptionsPage.Proxy, dialog.Tabs.SelectedIndex);
            Assert.Equal(10, dialog.Tabs.Items.Count);
            for (var i = 0; i < dialog.Tabs.Items.Count; i++)
            {
                dialog.Tabs.SelectedIndex = i;
                dialog.UpdateLayout();
                Assert.True(((FrameworkElement)dialog.Tabs.SelectedContent).ActualHeight > 0, $"tab {i}");
            }

            dialog.Close();
        });
    }

    [Fact]
    public void Ok_saves_settings_and_lists()
    {
        using var app = new AppHost();
        var options = app.Get<OptionsService>();
        var settings = app.Get<ISettingsService>();
        wpf.Run(() =>
        {
            var vm = options.CreateViewModel();
            vm.ConnectionType = ConnectionSpeedType.HighSpeed;
            vm.Settings.Downloads.ShowStartDialog = false;
            vm.ExcludedSites.Add("*.mirror.example");
            vm.AddCategory("Books", "epub mobi", null);
            vm.ServerExceptions.Add(new ServerExceptionEdit(0, "slow.example", 2));
            vm.SiteLogins.Add(new SiteLoginEdit(0, "*.private.example", "alice", "pw"));
            vm.ProxyMode = ProxyMode.Manual;
            vm.Settings.Proxy.Http.Host = "proxy.example";
            vm.HttpPassword = "proxy-pw";
            vm.Sounds[0].Enabled = false;
            Assert.Null(vm.Validate());

            var result = options.Apply(vm);

            Assert.True(result.CategoriesChanged);
            Assert.False(result.LanguageChanged);
            Assert.Equal(16, settings.Current.Connection.DefaultMaxConnections);
            Assert.False(settings.Current.Downloads.ShowStartDialog);
            Assert.Contains("*.mirror.example", settings.Current.FileTypes.ExcludedSites);
            Assert.Equal(ProxyMode.Manual, settings.Current.Proxy.Mode);
            Assert.NotEqual("proxy-pw", settings.Current.Proxy.Http.ProtectedPassword);
            Assert.False(settings.Current.Sounds.DownloadComplete.Enabled);
            Assert.Contains(app.Get<ICategoryRepository>().GetAll(), c => c.Name == "Books" && c.Extensions == "epub mobi");
            Assert.Contains(app.Get<IServerExceptionRepository>().GetAll(), e => e.Host == "slow.example" && e.MaxConnections == 2);
            var login = Assert.Single(app.Get<ISiteLoginRepository>().GetAll());
            Assert.Equal("pw", login.Password);

            // Reopening shows what was saved, passwords included.
            var again = options.CreateViewModel();
            Assert.Equal("proxy-pw", again.HttpPassword);
            Assert.Equal(16, again.MaxConnections);
            Assert.Equal(ConnectionSpeedType.HighSpeed, again.ConnectionType);
        });
    }

    [Fact]
    public void Deleting_staged_items_removes_them_on_ok()
    {
        using var app = new AppHost();
        var options = app.Get<OptionsService>();
        app.Get<IServerExceptionRepository>().Insert(new NovaGet.Core.Models.ServerException { Host = "a.example", MaxConnections = 4 });
        app.Get<ICategoryRepository>().Insert(new NovaGet.Core.Models.Category { Name = "Temp stuff" });
        wpf.Run(() =>
        {
            var vm = options.CreateViewModel();
            vm.RemoveServerException(vm.ServerExceptions.Single());
            Assert.False(vm.RemoveCategory(vm.Categories.First(c => c.IsBuiltIn)));
            Assert.True(vm.RemoveCategory(vm.Categories.Single(c => c.Name == "Temp stuff")));

            options.Apply(vm);

            Assert.Empty(app.Get<IServerExceptionRepository>().GetAll());
            Assert.DoesNotContain(app.Get<ICategoryRepository>().GetAll(), c => c.Name == "Temp stuff");
        });
    }

    [Fact]
    public void Connection_presets_and_custom_counts_stay_in_sync()
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var vm = app.Get<OptionsService>().CreateViewModel();

            vm.ConnectionType = ConnectionSpeedType.LowSpeed;
            Assert.Equal(2, vm.MaxConnections);

            vm.MaxConnections = 24;
            Assert.Equal(ConnectionSpeedType.Custom, vm.ConnectionType);
            Assert.Equal(24, vm.MaxConnections);
        });
    }

    [Theory]
    [InlineData("pac", OptionsPage.Proxy)]
    [InlineData("manual", OptionsPage.Proxy)]
    [InlineData("temp", OptionsPage.SaveTo)]
    [InlineData("scanner", OptionsPage.Downloads)]
    public void Validation_points_at_the_tab(string problem, OptionsPage page)
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var vm = app.Get<OptionsService>().CreateViewModel();
            switch (problem)
            {
                case "pac":
                    vm.ProxyMode = ProxyMode.AutoConfigScript;
                    vm.Settings.Proxy.PacUrl = "not a url";
                    break;
                case "manual":
                    vm.ProxyMode = ProxyMode.Manual;
                    break;
                case "temp":
                    vm.TempDirectory = "relative\\path";
                    break;
                case "scanner":
                    vm.Settings.Downloads.VirusScan.Enabled = true;
                    vm.VirusProgram = string.Empty;
                    break;
            }

            Assert.Equal(page, vm.Validate()?.Page);
        });
    }

    [Fact]
    public void Defender_preset_fills_the_scanner()
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var vm = app.Get<OptionsService>().CreateViewModel();
            vm.UseWindowsDefenderCommand.Execute(null);

            Assert.True(vm.Settings.Downloads.VirusScan.Enabled);
            Assert.Equal(VirusScanSettings.WindowsDefenderProgram, vm.VirusProgram);
            Assert.Contains("[file]", vm.VirusArguments, StringComparison.Ordinal);
        });
    }
}
