using System.Windows;
using NovaGet.App.Services;
using NovaGet.App.ViewModels.Scheduler;
using NovaGet.App.Views;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Services;
using NovaGet.Core.Services.Queues;
using NovaGet.Core.Settings;

namespace NovaGet.App.Tests;

[Collection(WpfCollection.Name)]
public sealed class SchedulerTests(WpfFixture wpf)
{
    private static SchedulerViewModel CreateViewModel(AppHost app, RecordingDialogs dialogs) => new(
        app.Get<IQueueRepository>(),
        app.Get<IDownloadService>(),
        app.Get<IDownloadEngine>(),
        app.Get<IQueueManager>(),
        app.Get<IAppController>(),
        dialogs,
        queue =>
        {
            app.Get<IQueueRepository>().Update(queue);
            return null; // no real wake tasks from tests
        });

    [Fact]
    public void Window_loads_and_shows_the_sync_tab_for_the_sync_queue()
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var window = new SchedulerWindow(CreateViewModel(app, new RecordingDialogs()), app.Get<ISettingsService>())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
            };
            window.Show();
            window.UpdateLayout();
            Assert.Equal(window.ScheduleTab, window.Tabs.SelectedItem);
            Assert.True(window.ActualWidth > 0);

            window.Select(DownloadQueue.SyncQueueId);
            window.UpdateLayout();
            Assert.Equal(window.SyncTab, window.Tabs.SelectedItem);
            Assert.Equal(Visibility.Collapsed, window.ScheduleTab.Visibility);

            window.Tabs.SelectedItem = window.FilesTab;
            window.UpdateLayout();
            window.Close();
        });
    }

    [Fact]
    public void Apply_saves_the_schedule()
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var vm = CreateViewModel(app, new RecordingDialogs());
            var main = vm.Queues.Single(q => q.Id == DownloadQueue.MainQueueId);
            Assert.False(vm.HasChanges);

            main.StartEnabled = true;
            main.StartTime = "23:15";
            main.EveryDay = false;
            main.Days.Single(d => d.Day == DayOfWeek.Saturday).IsChecked = true;
            main.SimultaneousCount = 3;
            main.TurnOffWhenDone = true;
            main.PowerAction = PowerAction.Hibernate;
            Assert.True(vm.HasChanges);

            Assert.True(vm.ApplyAll());

            Assert.False(vm.HasChanges);
            var saved = app.Get<IQueueRepository>().Get(DownloadQueue.MainQueueId)!;
            Assert.True(saved.Schedule.StartEnabled);
            Assert.Equal(new TimeSpan(23, 15, 0), saved.Schedule.StartTime);
            Assert.Equal(new[] { DayOfWeek.Saturday }, saved.Schedule.Days);
            Assert.Equal(3, saved.SimultaneousCount);
            Assert.Equal(PowerAction.Hibernate, saved.Schedule.PowerAction);
            vm.Dispose();
        });
    }

    [Fact]
    public void Invalid_values_are_reported_and_not_saved()
    {
        using var app = new AppHost();
        wpf.Run(() =>
        {
            var dialogs = new RecordingDialogs();
            var vm = CreateViewModel(app, dialogs);
            var main = vm.Queues.Single(q => q.Id == DownloadQueue.MainQueueId);
            main.StartEnabled = true;
            main.StartTime = "25 o'clock";

            Assert.False(vm.ApplyAll());
            Assert.Single(dialogs.Errors);
            Assert.False(app.Get<IQueueRepository>().Get(DownloadQueue.MainQueueId)!.Schedule.StartEnabled);
            vm.Dispose();
        });
    }

    [Fact]
    public void User_queues_can_be_renamed_but_not_to_a_taken_or_quoted_name()
    {
        using var app = new AppHost();
        var repo = app.Get<IQueueRepository>();
        repo.Insert(new DownloadQueue { Name = "Night" });
        wpf.Run(() =>
        {
            var dialogs = new RecordingDialogs();
            var vm = CreateViewModel(app, dialogs);
            var night = vm.Queues.Single(q => q.Name == "Night");

            night.Name = "Bad \"name\"";
            Assert.False(vm.ApplyAll());
            night.Name = "main download queue";
            Assert.False(vm.ApplyAll());
            Assert.Equal(2, dialogs.Errors.Count);

            night.Name = "Weekend";
            Assert.True(vm.ApplyAll());
            Assert.NotNull(repo.GetByName("Weekend"));
            vm.Dispose();
        });
    }

    [Fact]
    public void Files_can_be_reordered()
    {
        using var app = new AppHost();
        var downloads = app.Get<IDownloadService>();
        var ids = Enumerable.Range(1, 3).Select(i => downloads.Add(new DownloadRequest
        {
            Url = $"https://example.com/file{i}.zip",
            FileName = $"file{i}.zip",
            QueueId = DownloadQueue.MainQueueId,
        }).Id).ToList();
        wpf.Run(() =>
        {
            var vm = CreateViewModel(app, new RecordingDialogs());
            vm.Select(DownloadQueue.MainQueueId);
            Assert.Equal(ids, vm.Files.Select(f => f.Id));

            vm.MoveFile(vm.Files[2], 0);
            Assert.Equal(new[] { ids[2], ids[0], ids[1] }, vm.Files.Select(f => f.Id));

            vm.SelectedFile = vm.Files[0];
            vm.MoveDownCommand.Execute(null);
            Assert.Equal(new[] { ids[0], ids[2], ids[1] }, vm.Files.Select(f => f.Id));

            vm.SelectedFile = vm.Files.Single(f => f.Id == ids[1]);
            vm.DeleteFromQueueCommand.Execute(null);
            Assert.Null(downloads.Find(ids[1])!.QueueId);
            vm.Dispose();
        });
    }

    [Theory]
    [InlineData("2:00", 2, 0)]
    [InlineData("02:30", 2, 30)]
    [InlineData("23:59", 23, 59)]
    [InlineData("7:05 PM", 19, 5)]
    public void Times_are_parsed(string text, int hours, int minutes)
    {
        Assert.Equal(new TimeSpan(hours, minutes, 0), QueueEditViewModel.ParseTime(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("noon")]
    public void Bad_times_are_rejected(string text)
    {
        Assert.Null(QueueEditViewModel.ParseTime(text));
    }

    /// <summary>Records messages instead of showing them.</summary>
    private sealed class RecordingDialogs : IDialogService
    {
        public List<string> Errors { get; } = [];

        public Window? ActiveWindow => null;

        public void Info(string message, string? title = null)
        {
        }

        public void Error(string message, string? title = null) => Errors.Add(message);

        public bool Confirm(string message, string? title = null) => true;

        public (bool Yes, bool Checked) ConfirmWithCheck(string message, string checkText, bool checkDefault, string? title = null) => (true, checkDefault);

        public bool? ShowModal(Window dialog) => false;

        public string? PickFolder(string? initialFolder, string? title = null) => null;

        public string? PickSaveFile(string? initialPath, string filter, string? title = null) => null;

        public string? PickOpenFile(string filter, string? title = null) => null;
    }
}
