using System.Globalization;
using System.Text;
using System.Xml;
using NovaGet.Core.Models;

namespace NovaGet.Core.Services.Queues;

/// <summary>
/// "Wake the computer to start the queue": a Windows Task Scheduler task with <c>WakeToRun</c> that launches
/// <c>NovaGet.exe /startqueue "&lt;name&gt;"</c> at the queue's start time. Registered with
/// <c>schtasks /Create /XML</c> (the only schtasks route to WakeToRun) under the <c>\NovaGet\</c> folder.
/// </summary>
public static class WakeTask
{
    public const string Folder = @"\NovaGet\";

    public static string TaskName(long queueId) =>
        string.Create(CultureInfo.InvariantCulture, $@"{Folder}Queue {queueId}");

    /// <summary>A task is needed when waking is on and the schedule can still start the queue.</summary>
    public static bool IsNeeded(DownloadQueue queue, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var s = queue.Schedule;
        return s.WakeComputer && s.StartEnabled && ScheduleMath.Next(s, s.StartTime, nowLocal) is not null;
    }

    /// <summary>The task definition (Task Scheduler schema 1.2), as UTF-16 XML text.</summary>
    public static string BuildXml(DownloadQueue queue, string executable, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var s = queue.Schedule;
        const string ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var text = new StringBuilder();
        using (var xml = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, Encoding = Encoding.Unicode }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("Task", ns);
            xml.WriteAttributeString("version", "1.2");

            xml.WriteStartElement("RegistrationInfo", ns);
            xml.WriteElementString("Description", ns, $"Wakes the computer and starts the NovaGet queue \"{queue.Name}\".");
            xml.WriteElementString("URI", ns, TaskName(queue.Id));
            xml.WriteEndElement();

            xml.WriteStartElement("Triggers", ns);
            if (s.Mode == ScheduleMode.OneTime)
            {
                xml.WriteStartElement("TimeTrigger", ns);
                xml.WriteElementString("StartBoundary", ns, Boundary((s.OneTimeDate ?? nowLocal).Date + s.StartTime));
                xml.WriteElementString("Enabled", ns, "true");
                xml.WriteEndElement();
            }
            else
            {
                xml.WriteStartElement("CalendarTrigger", ns);
                xml.WriteElementString("StartBoundary", ns, Boundary(nowLocal.Date + s.StartTime));
                xml.WriteElementString("Enabled", ns, "true");
                if (s.Days.Distinct().Count() == 7)
                {
                    xml.WriteStartElement("ScheduleByDay", ns);
                    xml.WriteElementString("DaysInterval", ns, "1");
                    xml.WriteEndElement();
                }
                else
                {
                    xml.WriteStartElement("ScheduleByWeek", ns);
                    xml.WriteStartElement("DaysOfWeek", ns);
                    foreach (var day in s.Days.Distinct().Order())
                    {
                        xml.WriteElementString(day.ToString(), ns, string.Empty);
                    }

                    xml.WriteEndElement();
                    xml.WriteElementString("WeeksInterval", ns, "1");
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement(); // Triggers

            xml.WriteStartElement("Principals", ns);
            xml.WriteStartElement("Principal", ns);
            xml.WriteAttributeString("id", "Author");
            xml.WriteElementString("LogonType", ns, "InteractiveToken");
            xml.WriteElementString("RunLevel", ns, "LeastPrivilege");
            xml.WriteEndElement();
            xml.WriteEndElement();

            xml.WriteStartElement("Settings", ns);
            xml.WriteElementString("MultipleInstancesPolicy", ns, "IgnoreNew");
            xml.WriteElementString("DisallowStartIfOnBatteries", ns, "false");
            xml.WriteElementString("StopIfGoingOnBatteries", ns, "false");
            xml.WriteElementString("AllowHardTerminate", ns, "false");
            xml.WriteElementString("StartWhenAvailable", ns, "false");
            xml.WriteElementString("RunOnlyIfNetworkAvailable", ns, "false");
            xml.WriteStartElement("IdleSettings", ns);
            xml.WriteElementString("StopOnIdleEnd", ns, "false");
            xml.WriteElementString("RestartOnIdle", ns, "false");
            xml.WriteEndElement();
            xml.WriteElementString("AllowStartOnDemand", ns, "true");
            xml.WriteElementString("Enabled", ns, "true");
            xml.WriteElementString("Hidden", ns, "false");
            xml.WriteElementString("RunOnlyIfIdle", ns, "false");
            xml.WriteElementString("WakeToRun", ns, "true");
            xml.WriteElementString("ExecutionTimeLimit", ns, "PT0S");
            xml.WriteElementString("Priority", ns, "7");
            xml.WriteEndElement();

            xml.WriteStartElement("Actions", ns);
            xml.WriteAttributeString("Context", "Author");
            xml.WriteStartElement("Exec", ns);
            xml.WriteElementString("Command", ns, executable);
            xml.WriteElementString("Arguments", ns, StartArguments(queue.Name));
            xml.WriteEndElement();
            xml.WriteEndElement();

            xml.WriteEndElement(); // Task
            xml.WriteEndDocument();
        }

        return text.ToString();
    }

    /// <summary><c>/startqueue "name"</c>; queue names can't contain quotes (they are refused when named).</summary>
    public static string StartArguments(string queueName) =>
        $"/startqueue \"{queueName.Replace("\"", string.Empty, StringComparison.Ordinal)}\"";

    /// <summary>Task names in the <c>\NovaGet\</c> folder from <c>schtasks /Query /FO CSV /NH</c> output.</summary>
    public static IReadOnlyList<string> ParseTaskNames(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        var names = new List<string>();
        foreach (var line in csv.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '"')
            {
                continue;
            }

            var end = trimmed.IndexOf('"', 1);
            var name = end > 1 ? trimmed[1..end] : null;
            if (name is not null && name.StartsWith(Folder, StringComparison.OrdinalIgnoreCase) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static string Boundary(DateTime local) => local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
