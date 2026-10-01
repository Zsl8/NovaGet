using NovaGet.Core.Models;

namespace NovaGet.Core.Services.Queues;

/// <summary>When a queue's start and stop times fall (all times are local wall-clock times).</summary>
public static class ScheduleMath
{
    /// <summary>A start or stop time missed by more than this (the PC was asleep or off) is skipped.</summary>
    public static readonly TimeSpan MissedWindow = TimeSpan.FromMinutes(5);

    /// <summary>True when the schedule runs on <paramref name="day"/>.</summary>
    public static bool RunsOn(QueueSchedule schedule, DateTime day)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return schedule.Mode == ScheduleMode.OneTime
            ? schedule.OneTimeDate?.Date == day.Date
            : schedule.Days.Contains(day.DayOfWeek);
    }

    /// <summary>
    /// True when <paramref name="timeOfDay"/> on a scheduled day lies in (<paramref name="from"/>, <paramref name="to"/>]
    /// and is no older than <see cref="MissedWindow"/>.
    /// </summary>
    public static bool Crossed(QueueSchedule schedule, TimeSpan timeOfDay, DateTime from, DateTime to)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (to <= from)
        {
            return false;
        }

        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var at = day + timeOfDay;
            if (at > from && at <= to && to - at <= MissedWindow && RunsOn(schedule, day))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The next local time at or after <paramref name="now"/> when <paramref name="timeOfDay"/> occurs, or null.</summary>
    public static DateTime? Next(QueueSchedule schedule, TimeSpan timeOfDay, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Mode == ScheduleMode.OneTime)
        {
            var at = schedule.OneTimeDate?.Date + timeOfDay;
            return at >= now ? at : null;
        }

        for (var i = 0; i <= 7; i++)
        {
            var day = now.Date.AddDays(i);
            var at = day + timeOfDay;
            if (at >= now && RunsOn(schedule, day))
            {
                return at;
            }
        }

        return null;
    }
}
