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
        "AddUrl", "FileInfo", "QueuePick", "Duplicate", "Complete", "Properties", "MoveRename", "PowerCountdown",
        "ContextMenuItems", "WebPlayerPanel", "ServerException", "SiteLogin", "StreamQuality", "Batch",
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
                "AddUrl" => new AddUrlDialog(["https://example.com/a.zip"], "https://example.com/b.zip", "UA", (_, _) => throw new InvalidOperationException()),
                "FileInfo" => new FileInfoDialog(new FileInfoRequest
                {
                    Url = "https://example.com/a.zip",
                    FileName = "a.zip",
                    Size = 1234,
                    Categories = [new ChoiceItem(1, "General"), new ChoiceItem(2, "Compressed")],
                    CategoryId = 2,
                    FolderForCategory = _ => Path.GetTempPath(),
                    Queues = [new ChoiceItem(1, "Main download queue")],
                }),
                "QueuePick" => new QueuePickDialog([new ChoiceItem(1, "Main"), new ChoiceItem(2, "Night")]),
                "Duplicate" => new DuplicateDialog("a.zip"),
                "Complete" => new CompleteDialog(SampleDownload(), new NovaGet.Core.Settings.SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bak"))),
                "Properties" => new PropertiesDialog(SampleDownload(), new NullDownloads()),
                "MoveRename" => new MoveRenameDialog(SampleDownload(), new NullDownloads()),
                "PowerCountdown" => new PowerCountdownDialog(NovaGet.Core.Models.PowerAction.Sleep),
                "ContextMenuItems" => new ContextMenuItemsDialog(new ContextMenuSettings()),
                "WebPlayerPanel" => new WebPlayerPanelDialog(new WebPlayerPanelSettings { ExcludedSites = ["*.example.com"] }),
                "ServerException" => new ServerExceptionDialog("files.example.com", 4),
                "SiteLogin" => new SiteLoginDialog("*.example.com", "alice", "secret"),
                "Batch" => new BatchDialog("http://example.com/img*.jpg"),
                "StreamQuality" => new StreamQualityDialog(new NovaGet.App.ViewModels.StreamQualityViewModel(StreamTests.SampleHls(), "Sample video")),
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

    private static NovaGet.Core.Models.Download SampleDownload() => new()
    {
        Id = 1,
        Url = "https://example.com/a.zip",
        OriginalUrl = "https://example.com/a.zip",
        FileName = "a.zip",
        SavePath = Path.GetTempPath(),
        Size = 2048,
        Downloaded = 1024,
        Status = NovaGet.Core.Models.DownloadStatus.Paused,
    };

    /// <summary>Just enough of a download list for dialogs that only read.</summary>
    private sealed class NullDownloads : NovaGet.Core.Services.IDownloadService
    {
        public event EventHandler<NovaGet.Core.Services.DownloadListChangedEventArgs>? Changed { add { } remove { } }

        public event EventHandler<NovaGet.Core.Engine.DownloadStateChangedEventArgs>? StateChanged { add { } remove { } }

        public IReadOnlyList<NovaGet.Core.Models.Download> GetAll() => [];

        public NovaGet.Core.Models.Download? Find(long id) => SampleDownload();

        public NovaGet.Core.Models.Download Add(NovaGet.Core.Services.DownloadRequest request) => throw new NotSupportedException();

        public NovaGet.Core.Models.Download? FindByUrl(string url) => null;

        public Task<string?> MoveOrRenameAsync(long id, string folder, string fileName) => Task.FromResult<string?>(null);

        public bool Start(long id, bool startedByQueue = false) => false;

        public Task StopAsync(long id) => Task.CompletedTask;

        public Task StopAllAsync() => Task.CompletedTask;

        public Task RemoveAsync(IReadOnlyCollection<long> ids, bool deleteFiles) => Task.CompletedTask;

        public int RemoveCompleted() => 0;

        public Task RedownloadAsync(long id, bool start = true) => Task.CompletedTask;

        public void SetQueue(IReadOnlyCollection<long> ids, long? queueId)
        {
        }

        public void MoveInQueue(long id, int delta)
        {
        }

        public void SetCategory(IReadOnlyCollection<long> ids, long categoryId)
        {
        }

        public void Save(NovaGet.Core.Models.Download download)
        {
        }

        public void Reload(long id)
        {
        }

        public NovaGet.Core.Services.DownloadStatistics GetStatistics() => new(0, 0, 0);
    }
}
