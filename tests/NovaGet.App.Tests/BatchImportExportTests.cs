using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.CommandLine;
using NovaGet.Core.ImportExport;
using NovaGet.Core.Models;
using NovaGet.Core.Services;

namespace NovaGet.App.Tests;

[Collection(WpfCollection.Name)]
public sealed class BatchImportExportTests(WpfFixture wpf)
{
    [Fact]
    public void Batch_preview_follows_the_input()
    {
        var vm = new BatchViewModel("http://www.site.com/images/img*.jpg") { From = "1", To = "150", WildcardSize = 3 };

        Assert.Equal("http://www.site.com/images/img001.jpg", vm.FirstFile);
        Assert.Equal("http://www.site.com/images/img150.jpg", vm.LastFile);
        Assert.Null(vm.Error);
        Assert.False(string.IsNullOrEmpty(vm.CountText));

        vm.IsLetters = true;
        Assert.False(vm.IsSizeEnabled);
        Assert.Equal(("a", "z"), (vm.From, vm.To));
        Assert.Equal("http://www.site.com/images/imgz.jpg", vm.LastFile);

        vm.Address = "http://www.site.com/images/img.jpg";
        Assert.NotNull(vm.Error);
        Assert.Equal(string.Empty, vm.FirstFile);

        vm.IsNumbers = true;
        vm.Address = "http://example.com/*";
        vm.WildcardSize = 1;
        vm.IncreaseSizeCommand.Execute(null);
        vm.IncreaseSizeCommand.Execute(null);
        Assert.Equal("http://example.com/001", vm.FirstFile);
        vm.DecreaseSizeCommand.Execute(null);
        Assert.Equal(2, vm.WildcardSize);
    }

    [Fact]
    public void Every_batch_error_has_a_message() =>
        Assert.All(Enum.GetValues<BatchError>(), e => Assert.False(string.IsNullOrWhiteSpace(BatchViewModel.ErrorText(e))));

    [Fact]
    public void Batch_dialog_loads()
    {
        wpf.Run(() =>
        {
            var dialog = new BatchDialog("http://example.com/a*.zip") { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000 };
            dialog.Show();
            dialog.UpdateLayout();
            Assert.Equal("http://example.com/a1.zip", dialog.ViewModel.FirstFile);
            Assert.Null(dialog.Login);
            dialog.Close();
        });
    }

    [Fact]
    public void Links_can_start_all_checked()
    {
        wpf.Run(() =>
        {
            var vm = new LinksViewModel(new LinksRequest
            {
                Title = "Batch",
                Links = [new LinkEntry(new Uri("https://example.com/a.jpg")), new LinkEntry(new Uri("https://example.com/b.unknown")) { FileName = "named.bin" }],
                CheckAll = true,
                Categories = [new ChoiceItem(1, "General")],
                FolderForCategory = _ => Path.GetTempPath(),
                Queues = [new ChoiceItem(1, "Main download queue")],
            });

            Assert.Equal(2, vm.SelectedLinks.Count);
            Assert.Equal("named.bin", vm.Items[1].FileName);
        });
    }

    [Fact]
    public void Export_writes_the_downloads_as_ef2()
    {
        var dialogs = new ScriptedDialogs();
        using var app = new AppHost(s => s.AddSingleton<IDialogService>(dialogs));
        var downloads = app.Get<IDownloadService>();
        var first = downloads.Add(new DownloadRequest
        {
            Url = "https://cdn.example.com/signed/file.zip?sig=1",
            OriginalUrl = "https://example.com/file.zip",
            Referrer = "https://example.com/page",
            Cookies = "a=b",
            FileName = "file.zip",
        });
        downloads.Add(new DownloadRequest { Url = "ftp://example.com/b.iso" });
        dialogs.SaveFilePath = Path.Combine(app.Paths.RoamingDir, "out.ef2");

        wpf.Run(() => app.Get<DownloadUiService>().Export(ef2: true, ids: null));
        var all = DownloadListFormats.ParseEf2(File.ReadAllText(dialogs.SaveFilePath));
        wpf.Run(() => app.Get<DownloadUiService>().Export(ef2: false, ids: [first.Id]));
        var one = DownloadListFormats.ParseText(File.ReadAllText(dialogs.SaveFilePath));

        Assert.Equal(["https://example.com/file.zip", "ftp://example.com/b.iso"], all.Downloads.Select(d => d.Url.AbsoluteUri));
        Assert.Equal(("https://example.com/page", "a=b", "file.zip"), (all.Downloads[0].Referrer, all.Downloads[0].Cookies, all.Downloads[0].FileName));
        Assert.Equal(["https://example.com/file.zip"], one.Downloads.Select(d => d.Url.AbsoluteUri));
        Assert.Equal(2, dialogs.Infos.Count);
    }

    [Fact]
    public void Import_offers_the_list_all_checked_and_keeps_the_details()
    {
        var dialogs = new ScriptedDialogs();
        using var app = new AppHost(s => s.AddSingleton<IDialogService>(dialogs));
        var file = Path.Combine(app.Paths.RoamingDir, "in.ef2");
        File.WriteAllText(file, "<\r\nhttps://example.com/one.zip\r\nreferer: https://example.com/\r\ncookie: s=1\r\nfilename: First.zip\r\n>\r\n<\r\njavascript:alert(1)\r\n>\r\n<\r\nhttps://example.com/two.zip\r\n>\r\n");
        dialogs.OpenFilePath = file;
        LinksViewModel? offered = null;
        dialogs.OnModal = window =>
        {
            var links = Assert.IsType<LinksDialog>(window);
            offered = links.ViewModel;
            typeof(LinksDialog).GetProperty(nameof(LinksDialog.QueueId))!.SetValue(links, (long?)DownloadQueue.MainQueueId); // "Download Later"
            return true;
        };

        wpf.Run(() => app.Get<DownloadUiService>().Import(ef2: true));

        Assert.NotNull(offered);
        Assert.Equal(2, offered.SelectedLinks.Count);
        Assert.Single(dialogs.Infos); // one entry skipped
        var added = app.Get<IDownloadService>().GetAll();
        var one = Assert.Single(added, d => d.Url == "https://example.com/one.zip");
        Assert.Equal(("https://example.com/", "s=1", "First.zip", DownloadStatus.Queued), (one.Referrer, one.Cookies, one.FileName, one.Status));
        Assert.Contains(added, d => d.Url == "https://example.com/two.zip");
    }

    [Fact]
    public async Task Command_line_completion_switches_apply_to_the_download()
    {
        using var app = new AppHost();
        var ui = app.Get<DownloadUiService>();
        var options = new CommandLineOptions { Url = "https://example.com/file.zip", Silent = true, ExitWhenDone = true, HangUpWhenDone = true, AddToQueueOnly = true };

        var id = await ui.AddFromCommandLineAsync(options);

        Assert.NotNull(id);
        var completion = ui.CompletionFor(id.Value);
        Assert.True(completion.ExitWhenDone);
        Assert.True(completion.HangUp);
        Assert.False(completion.ShowCompleteDialog);
        Assert.Equal(DownloadStatus.Queued, app.Get<IDownloadService>().Find(id.Value)!.Status);
    }

    private sealed class ScriptedDialogs : IDialogService
    {
        public List<string> Infos { get; } = [];

        public List<string> Errors { get; } = [];

        public string? SaveFilePath { get; set; }

        public string? OpenFilePath { get; set; }

        public Func<Window, bool?> OnModal { get; set; } = _ => false;

        public Window? ActiveWindow => null;

        public void Info(string message, string? title = null) => Infos.Add(message);

        public void Error(string message, string? title = null) => Errors.Add(message);

        public bool Confirm(string message, string? title = null) => true;

        public (bool Yes, bool Checked) ConfirmWithCheck(string message, string checkText, bool checkDefault, string? title = null) => (true, checkDefault);

        public bool? ShowModal(Window dialog) => OnModal(dialog);

        public string? PickFolder(string? initialFolder, string? title = null) => null;

        public string? PickSaveFile(string? initialPath, string filter, string? title = null) => SaveFilePath;

        public string? PickOpenFile(string filter, string? title = null) => OpenFilePath;
    }
}
