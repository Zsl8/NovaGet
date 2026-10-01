using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Models;
using NovaGet.Core.Services.Queues;

namespace NovaGet.App.Services;

/// <summary>Registers and removes the Task Scheduler tasks that wake the PC for scheduled queues (via schtasks.exe).</summary>
public sealed class WakeTaskService(ILogger<WakeTaskService> logger)
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Creates, replaces or removes the queue's task to match its schedule. Returns an error message, or null.</summary>
    public string? Update(DownloadQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var now = DateTime.Now;
        if (!WakeTask.IsNeeded(queue, now))
        {
            Remove(queue.Id);
            return null;
        }

        var file = Path.Combine(Path.GetTempPath(), $"novaget-wake-{queue.Id}-{Guid.NewGuid():N}.xml");
        try
        {
            var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NovaGet.exe");
            File.WriteAllText(file, WakeTask.BuildXml(queue, executable, now), Encoding.Unicode);
            var (exitCode, output) = Run("/Create", "/TN", WakeTask.TaskName(queue.Id), "/XML", file, "/F");
            if (exitCode != 0)
            {
                logger.LogWarning("schtasks /Create for queue {Queue} failed ({Code}): {Output}", queue.Name, exitCode, output);
                return string.IsNullOrWhiteSpace(output) ? $"schtasks exit code {exitCode}" : output.Trim();
            }

            logger.LogInformation("Wake task registered for queue {Queue}", queue.Name);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not register the wake task for queue {Queue}", queue.Name);
            return ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Remove(long queueId)
    {
        if (OperatingSystem.IsWindows())
        {
            TryRun("/Delete", "/TN", WakeTask.TaskName(queueId), "/F");
        }
    }

    /// <summary>Uninstall: removes every task in the <c>\NovaGet\</c> folder.</summary>
    public void RemoveAll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (exitCode, output) = TryRun("/Query", "/FO", "CSV", "/NH");
        if (exitCode != 0)
        {
            return;
        }

        foreach (var name in WakeTask.ParseTaskNames(output))
        {
            TryRun("/Delete", "/TN", name, "/F");
        }
    }

    private (int ExitCode, string Output) TryRun(params string[] arguments)
    {
        try
        {
            return Run(arguments);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "schtasks {Arguments} failed", string.Join(' ', arguments));
            return (-1, string.Empty);
        }
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("schtasks did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill();
            return (-1, "schtasks did not finish in time.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
