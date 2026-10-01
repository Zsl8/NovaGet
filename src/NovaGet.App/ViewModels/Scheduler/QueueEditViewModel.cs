using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaGet.App.Localization;
using NovaGet.App.ViewModels.Options;
using NovaGet.Core.Models;

namespace NovaGet.App.ViewModels.Scheduler;

/// <summary>A weekday check box of the "Daily" schedule.</summary>
public sealed partial class DayOption(DayOfWeek day, bool isChecked) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Name { get; } = Localizer.Culture.DateTimeFormat.GetAbbreviatedDayName(day);

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

/// <summary>One queue in the Scheduler window, edited until Apply.</summary>
public sealed partial class QueueEditViewModel : ObservableObject
{
    private bool _loading = true;

    public QueueEditViewModel(DownloadQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        Original = queue;
        var s = queue.Schedule;
        _name = queue.Name;
        _isOneTime = s.Mode == ScheduleMode.OneTime;
        _oneTimeDate = s.OneTimeDate ?? DateTime.Today;
        foreach (var day in OrderedDays())
        {
            var option = new DayOption(day, s.Days.Contains(day));
            option.PropertyChanged += OnDayChanged;
            Days.Add(option);
        }

        _startEnabled = s.StartEnabled;
        _startTime = FormatTime(s.StartTime);
        _stopEnabled = s.StopEnabled;
        _stopTime = FormatTime(s.StopTime);
        _startOnAppStartup = s.StartOnAppStartup;
        _retriesPerFile = s.RetriesPerFile;
        _openFileWhenDone = s.OpenFileWhenDone;
        _openFilePath = s.OpenFilePath ?? string.Empty;
        _hangUpWhenDone = s.HangUpWhenDone;
        _exitWhenDone = s.ExitWhenDone;
        _turnOffWhenDone = s.TurnOffWhenDone;
        _powerAction = s.PowerAction == PowerAction.None ? PowerAction.ShutDown : s.PowerAction;
        _forceProcesses = s.ForceProcessesToTerminate;
        _wakeComputer = s.WakeComputer;
        _simultaneousCount = queue.SimultaneousCount;
        _syncIntervalMinutes = queue.SyncIntervalMinutes;
        _syncAtStartup = s.SyncAtStartup;
        _syncKeepBackup = s.SyncKeepBackup;
        _loading = false;
    }

    public DownloadQueue Original { get; private set; }

    public long Id => Original.Id;

    public bool IsBuiltIn => Original.IsBuiltIn;

    public bool IsSyncQueue => Original.IsSyncQueue;

    public bool CanRename => !IsBuiltIn;

    public string Title => IsBuiltIn ? MainViewModel.QueueTitle(Original) : Name;

    public System.Windows.Media.ImageSource Icon => Services.AppImages.Get(IsSyncQueue ? "sync-queue" : "queue", 32);

    public static IReadOnlyList<Choice<PowerAction>> PowerActions { get; } =
        [.. new[] { PowerAction.ShutDown, PowerAction.Sleep, PowerAction.Hibernate, PowerAction.LogOff }
            .Select(a => new Choice<PowerAction>(a, Localizer.Get("Power_" + a)))];

    public ObservableCollection<DayOption> Days { get; } = [];

    /// <summary>Unsaved changes.</summary>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDaily))]
    private bool _isOneTime;

    public bool IsDaily
    {
        get => !IsOneTime;
        set => IsOneTime = !value;
    }

    [ObservableProperty]
    private DateTime? _oneTimeDate;

    /// <summary>"Every day": all seven boxes.</summary>
    public bool EveryDay
    {
        get => Days.All(d => d.IsChecked);
        set
        {
            foreach (var day in Days)
            {
                day.IsChecked = value;
            }
        }
    }

    [ObservableProperty]
    private bool _startEnabled;

    [ObservableProperty]
    private string _startTime;

    [ObservableProperty]
    private bool _stopEnabled;

    [ObservableProperty]
    private string _stopTime;

    [ObservableProperty]
    private bool _startOnAppStartup;

    [ObservableProperty]
    private int _retriesPerFile;

    [ObservableProperty]
    private bool _openFileWhenDone;

    [ObservableProperty]
    private string _openFilePath;

    [ObservableProperty]
    private bool _hangUpWhenDone;

    [ObservableProperty]
    private bool _exitWhenDone;

    [ObservableProperty]
    private bool _turnOffWhenDone;

    [ObservableProperty]
    private PowerAction _powerAction;

    [ObservableProperty]
    private bool _forceProcesses;

    [ObservableProperty]
    private bool _wakeComputer;

    [ObservableProperty]
    private int _simultaneousCount;

    [ObservableProperty]
    private int _syncIntervalMinutes;

    [ObservableProperty]
    private bool _syncAtStartup;

    [ObservableProperty]
    private bool _syncKeepBackup;

    /// <summary>Problems that keep Apply from saving, or null.</summary>
    public string? Validate()
    {
        if (StartEnabled && ParseTime(StartTime) is null)
        {
            return Localizer.Get("Scheduler_ErrorStartTime");
        }

        if (StopEnabled && ParseTime(StopTime) is null)
        {
            return Localizer.Get("Scheduler_ErrorStopTime");
        }

        if (StartEnabled && IsOneTime && OneTimeDate is null)
        {
            return Localizer.Get("Scheduler_ErrorDate");
        }

        if (StartEnabled && !IsOneTime && !Days.Any(d => d.IsChecked))
        {
            return Localizer.Get("Scheduler_ErrorDays");
        }

        if (RetriesPerFile is < 0 or > 1000)
        {
            return Localizer.Format("Scheduler_ErrorRange", Localizer.Plain("Scheduler_Retries"), 0, 1000);
        }

        if (SimultaneousCount is < 1 or > 32)
        {
            return Localizer.Format("Scheduler_ErrorRange", Localizer.Plain("Scheduler_SameTimePrefix"), 1, 32);
        }

        if (IsSyncQueue && SyncIntervalMinutes is < 1 or > 7 * 24 * 60)
        {
            return Localizer.Format("Scheduler_ErrorRange", Localizer.Plain("Scheduler_SyncEvery"), 1, 7 * 24 * 60);
        }

        if (OpenFileWhenDone && string.IsNullOrWhiteSpace(OpenFilePath))
        {
            return Localizer.Get("Scheduler_ErrorOpenFile");
        }

        return null;
    }

    /// <summary>The queue as it would be saved.</summary>
    public DownloadQueue ToModel() => new()
    {
        Id = Original.Id,
        Name = IsBuiltIn ? Original.Name : Name.Trim(),
        IsBuiltIn = Original.IsBuiltIn,
        IsSyncQueue = Original.IsSyncQueue,
        SimultaneousCount = SimultaneousCount,
        SyncIntervalMinutes = SyncIntervalMinutes,
        Schedule = new QueueSchedule
        {
            Mode = IsOneTime ? ScheduleMode.OneTime : ScheduleMode.Daily,
            OneTimeDate = OneTimeDate?.Date,
            Days = [.. Days.Where(d => d.IsChecked).Select(d => d.Day)],
            StartEnabled = StartEnabled,
            StartTime = ParseTime(StartTime) ?? Original.Schedule.StartTime,
            StopEnabled = StopEnabled,
            StopTime = ParseTime(StopTime) ?? Original.Schedule.StopTime,
            StartOnAppStartup = StartOnAppStartup,
            RetriesPerFile = RetriesPerFile,
            OpenFileWhenDone = OpenFileWhenDone,
            OpenFilePath = string.IsNullOrWhiteSpace(OpenFilePath) ? null : OpenFilePath.Trim(),
            HangUpWhenDone = HangUpWhenDone,
            ExitWhenDone = ExitWhenDone,
            TurnOffWhenDone = TurnOffWhenDone,
            PowerAction = PowerAction,
            ForceProcessesToTerminate = ForceProcesses,
            WakeComputer = WakeComputer,
            SyncAtStartup = SyncAtStartup,
            SyncKeepBackup = SyncKeepBackup,
        },
    };

    /// <summary>After Apply: the saved queue becomes the new baseline.</summary>
    public void MarkSaved(DownloadQueue saved)
    {
        Original = saved;
        IsDirty = false;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Accepts 2:00, 02:00, 14:30 and 2:00 PM style times.</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var culture in new[] { Localizer.Culture, CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParseExact(text, ["H:mm", "HH:mm", "H:mm:ss", "h:mm tt", "hh:mm tt", "t", "T"], culture, DateTimeStyles.NoCurrentDateDefault, out var parsed))
            {
                return parsed.TimeOfDay;
            }
        }

        return null;
    }

    public static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(IsDirty) or nameof(IsRunning) or nameof(Title) or nameof(IsDaily) or nameof(EveryDay)))
        {
            IsDirty = true;
        }
    }

    /// <summary>Sunday or Monday first, as the culture orders the week.</summary>
    private static IEnumerable<DayOfWeek> OrderedDays()
    {
        var first = Localizer.Culture.DateTimeFormat.FirstDayOfWeek;
        return Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)first + i) % 7));
    }

    private void OnDayChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(EveryDay));
        if (!_loading)
        {
            IsDirty = true;
        }
    }
}
