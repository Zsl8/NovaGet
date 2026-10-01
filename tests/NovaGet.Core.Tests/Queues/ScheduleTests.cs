using System.Xml.Linq;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Services.Queues;

namespace NovaGet.Core.Tests.Queues;

public sealed class ScheduleTests
{
    private static readonly DateTime Monday = new(2026, 10, 5);

    [Theory]
    [InlineData("01:59:00", "02:00:00", true)]
    [InlineData("01:59:00", "02:04:59", true)]
    [InlineData("01:59:00", "02:06:00", false)] // more than 5 minutes late
    [InlineData("02:00:00", "02:00:15", false)] // already fired at 02:00:00
    [InlineData("01:00:00", "01:59:59", false)]
    public void Crossed_detects_the_start_time(string from, string to, bool expected)
    {
        var schedule = new QueueSchedule();

        Assert.Equal(expected, ScheduleMath.Crossed(schedule, new TimeSpan(2, 0, 0), Monday + TimeSpan.Parse(from), Monday + TimeSpan.Parse(to)));
    }

    [Fact]
    public void Crossed_handles_midnight_and_days()
    {
        var schedule = new QueueSchedule { Days = [DayOfWeek.Tuesday] };
        var lateMonday = Monday + new TimeSpan(23, 59, 50);

        Assert.True(ScheduleMath.Crossed(schedule, TimeSpan.Zero, lateMonday, lateMonday.AddSeconds(15)));
        Assert.False(ScheduleMath.Crossed(schedule, new TimeSpan(23, 59, 55), lateMonday, lateMonday.AddSeconds(15)));
    }

    [Fact]
    public void One_time_schedules_run_on_their_date_only()
    {
        var schedule = new QueueSchedule { Mode = ScheduleMode.OneTime, OneTimeDate = Monday.AddDays(2) };

        Assert.False(ScheduleMath.RunsOn(schedule, Monday));
        Assert.True(ScheduleMath.RunsOn(schedule, Monday.AddDays(2)));
        Assert.Equal(Monday.AddDays(2) + new TimeSpan(2, 0, 0), ScheduleMath.Next(schedule, new TimeSpan(2, 0, 0), Monday));
        Assert.Null(ScheduleMath.Next(schedule, new TimeSpan(2, 0, 0), Monday.AddDays(3)));
    }

    [Fact]
    public void Next_finds_the_next_scheduled_day()
    {
        var schedule = new QueueSchedule { Days = [DayOfWeek.Friday] };

        Assert.Equal(new DateTime(2026, 10, 9, 2, 0, 0), ScheduleMath.Next(schedule, new TimeSpan(2, 0, 0), Monday + new TimeSpan(12, 0, 0)));
        Assert.Null(ScheduleMath.Next(new QueueSchedule { Days = [] }, TimeSpan.Zero, Monday));
    }

    private static Download Saved(long size = 100, string? etag = "\"abc\"", DateTime? modified = null) => new()
    {
        Size = size,
        ETag = etag,
        LastModified = modified ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    private static ProbeResult Probe(long size = 100, string? etag = "\"abc\"", DateTimeOffset? modified = null) => new()
    {
        FinalUri = new Uri("https://example.com/f"),
        FileName = "f",
        Size = size,
        ETag = etag,
        LastModified = modified ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Sync_compares_size_etag_date_and_presence()
    {
        Assert.False(SyncComparer.HasChanged(Saved(), Probe(), fileExists: true));
        Assert.True(SyncComparer.HasChanged(Saved(), Probe(), fileExists: false));
        Assert.True(SyncComparer.HasChanged(Saved(), Probe(size: 101), fileExists: true));
        Assert.True(SyncComparer.HasChanged(Saved(), Probe(etag: "\"def\""), fileExists: true));
        Assert.False(SyncComparer.HasChanged(Saved(), Probe(etag: "W/\"abc\""), fileExists: true));
        Assert.True(SyncComparer.HasChanged(Saved(), Probe(modified: new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)), fileExists: true));
        Assert.False(SyncComparer.HasChanged(Saved(etag: null), Probe(etag: "\"x\"", size: -1), fileExists: true));
    }

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Wake_task_for_every_day()
    {
        var queue = new DownloadQueue { Id = 3, Name = "Night", Schedule = new QueueSchedule { StartEnabled = true, WakeComputer = true } };

        var xml = XDocument.Parse(WakeTask.BuildXml(queue, @"C:\Program Files\NovaGet\NovaGet.exe", Monday + new TimeSpan(15, 0, 0)));

        var trigger = xml.Descendants(Ns + "CalendarTrigger").Single();
        Assert.Equal("2026-10-05T02:00:00", trigger.Element(Ns + "StartBoundary")!.Value);
        Assert.NotNull(trigger.Element(Ns + "ScheduleByDay"));
        Assert.Equal("true", xml.Descendants(Ns + "WakeToRun").Single().Value);
        Assert.Equal(@"C:\Program Files\NovaGet\NovaGet.exe", xml.Descendants(Ns + "Command").Single().Value);
        Assert.Equal("/startqueue \"Night\"", xml.Descendants(Ns + "Arguments").Single().Value);
        Assert.Equal(@"\NovaGet\Queue 3", xml.Descendants(Ns + "URI").Single().Value);
        Assert.Equal("InteractiveToken", xml.Descendants(Ns + "LogonType").Single().Value);
    }

    [Fact]
    public void Wake_task_for_some_days_and_one_time()
    {
        var weekly = new DownloadQueue
        {
            Id = 4, Name = "Weekend",
            Schedule = new QueueSchedule { StartEnabled = true, WakeComputer = true, Days = [DayOfWeek.Sunday, DayOfWeek.Saturday], StartTime = new TimeSpan(6, 30, 0) },
        };
        var days = XDocument.Parse(WakeTask.BuildXml(weekly, "NovaGet.exe", Monday)).Descendants(Ns + "DaysOfWeek").Single();
        Assert.Equal(new[] { "Sunday", "Saturday" }, days.Elements().Select(e => e.Name.LocalName));

        var once = new DownloadQueue
        {
            Id = 5, Name = "Once",
            Schedule = new QueueSchedule { Mode = ScheduleMode.OneTime, OneTimeDate = Monday.AddDays(1), StartEnabled = true, WakeComputer = true },
        };
        var trigger = XDocument.Parse(WakeTask.BuildXml(once, "NovaGet.exe", Monday)).Descendants(Ns + "TimeTrigger").Single();
        Assert.Equal("2026-10-06T02:00:00", trigger.Element(Ns + "StartBoundary")!.Value);
    }

    [Fact]
    public void Wake_task_is_needed_only_when_it_can_fire()
    {
        var queue = new DownloadQueue { Name = "Q", Schedule = new QueueSchedule { StartEnabled = true, WakeComputer = true } };
        Assert.True(WakeTask.IsNeeded(queue, Monday));

        queue.Schedule.WakeComputer = false;
        Assert.False(WakeTask.IsNeeded(queue, Monday));

        queue.Schedule = new QueueSchedule { Mode = ScheduleMode.OneTime, OneTimeDate = Monday.AddDays(-1), StartEnabled = true, WakeComputer = true };
        Assert.False(WakeTask.IsNeeded(queue, Monday));
    }

    [Fact]
    public void Task_names_are_read_from_schtasks_output()
    {
        var csv = "\"\\NovaGet\\Queue 3\",\"10/6/2026 2:00:00 AM\",\"Ready\"\r\n" +
                  "\"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\",\"N/A\",\"Ready\"\r\n" +
                  "\"\\NovaGet\\Queue 7\",\"N/A\",\"Disabled\"\r\n" +
                  "\"\\NovaGet\\Queue 3\",\"10/7/2026 2:00:00 AM\",\"Ready\"\r\n";

        Assert.Equal(new[] { @"\NovaGet\Queue 3", @"\NovaGet\Queue 7" }, WakeTask.ParseTaskNames(csv));
    }
}
