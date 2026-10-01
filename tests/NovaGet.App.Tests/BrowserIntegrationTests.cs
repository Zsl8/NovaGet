using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.Views;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Ipc;
using NovaGet.Core.Settings;

namespace NovaGet.App.Tests;

[Collection(WpfCollection.Name)]
public sealed class BrowserIntegrationTests(WpfFixture wpf)
{
    private static IpcRequest Native(string json, string? browser = "chrome") => new()
    {
        Type = IpcRequestTypes.Native,
        Payload = JsonDocument.Parse(json).RootElement.Clone(),
        Args = browser is null ? null : [browser],
    };

    [Fact]
    public void Hello_returns_the_settings_for_the_calling_browser()
    {
        using var app = new AppHost();
        app.Get<ISettingsService>().Update(s => s.General.IntegratedBrowsers = ["firefox"]);
        var service = app.Get<BrowserIntegrationService>();

        var chrome = service.Handle(Native("""{"type":"hello"}"""));
        var firefox = service.Handle(Native("""{"type":"getSettings"}""", "firefox"));

        Assert.True(chrome.Ok);
        Assert.False(chrome.Payload!.Value.GetProperty("enabled").GetBoolean());
        Assert.True(firefox.Payload!.Value.GetProperty("enabled").GetBoolean());
        Assert.True(firefox.Payload!.Value.GetProperty("fileTypes").GetArrayLength() > 10);
    }

    [Fact]
    public void Downloads_from_a_disabled_browser_stay_in_the_browser()
    {
        using var app = new AppHost();
        app.Get<ISettingsService>().Update(s => s.General.IntegratedBrowsers = []);

        var reply = app.Get<BrowserIntegrationService>().Handle(Native("""{"type":"download","url":"https://example.com/a.zip"}"""));

        Assert.False(reply.Ok);
        Assert.Equal(BrowserIntegrationService.Disabled, reply.Error);
    }

    [Theory]
    [InlineData("""{"type":"download","url":"javascript:alert(1)"}""")]
    [InlineData("""{"type":"download","url":"https://example.com/a.zip","postData":"a=1"}""")]
    [InlineData("""{"type":"media","items":[{"url":"https://example.com/v.mp4"}],"protected":true}""")]
    [InlineData("""{"type":"nope"}""")]
    public void Unusable_messages_are_refused(string json)
    {
        using var app = new AppHost();

        Assert.False(app.Get<BrowserIntegrationService>().Handle(Native(json)).Ok);
    }

    [Fact]
    public void Links_dialog_selects_preferred_types_and_filters()
    {
        wpf.Run(() =>
        {
            var request = new LinksRequest
            {
                Title = "Example page",
                Links =
                [
                    (new Uri("https://example.com/files/a.zip"), "Archive"),
                    (new Uri("https://example.com/files/b.zip"), null),
                    (new Uri("https://example.com/about.html"), "About"),
                    (new Uri("https://example.com/"), null),
                ],
                PreferredExtensions = ["zip", "iso"],
                Categories = [new ChoiceItem(1, "General")],
                FolderForCategory = _ => Path.GetTempPath(),
                Queues = [new ChoiceItem(1, "Main download queue")],
            };
            var dialog = new LinksDialog(request) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000 };
            dialog.Show();
            dialog.UpdateLayout();
            var vm = dialog.ViewModel;

            Assert.Equal(["a.zip", "b.zip"], vm.SelectedLinks.Select(l => l.FileName));
            Assert.Equal(["html", "zip", ""], vm.Extensions.Select(e => e.Extension));

            vm.Extensions.Single(e => e.Extension == "html").IsChecked = true;
            Assert.Equal(3, vm.SelectedLinks.Count);

            vm.FilterText = "about";
            Assert.Single(vm.View.Cast<object>());
            vm.SelectNoneCommand.Execute(null);
            Assert.Equal(2, vm.SelectedLinks.Count);

            vm.CategoryId = 1;
            Assert.Equal(Path.GetTempPath(), vm.SaveFolder);
            dialog.Close();
        });
    }

    [Fact]
    public void Dropped_data_formats_are_understood()
    {
        wpf.Run(() =>
        {
            var text = new DataObject(DataFormats.UnicodeText, "https://example.com/a.zip and https://example.com/b.zip");
            Assert.Equal(2, DroppedLinks.From(text).Count);

            var chrome = new DataObject();
            chrome.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes("https://example.com/c.iso\0")));
            Assert.Equal("https://example.com/c.iso", Assert.Single(DroppedLinks.From(chrome)).AbsoluteUri);

            var shortcut = Path.Combine(Path.GetTempPath(), $"novaget-{Guid.NewGuid():N}.url");
            File.WriteAllText(shortcut, "[InternetShortcut]\r\nURL=https://example.com/d.msi\r\n");
            try
            {
                var files = new DataObject(DataFormats.FileDrop, new[] { shortcut });
                Assert.Equal("https://example.com/d.msi", Assert.Single(DroppedLinks.From(files)).AbsoluteUri);
            }
            finally
            {
                File.Delete(shortcut);
            }

            Assert.Empty(DroppedLinks.From(new DataObject(DataFormats.UnicodeText, "no links here")));
        });
    }

    [Fact]
    public void Drop_target_loads()
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var window = new DropTargetWindow(app.Get<ISettingsService>(), app.Get<IAppController>(), _ => Task.CompletedTask);
            window.Show();
            window.UpdateLayout();
            Assert.Equal(64, window.ActualWidth);
            window.Close();
        });
    }
}
