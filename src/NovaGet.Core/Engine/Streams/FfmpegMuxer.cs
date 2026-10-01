using System.Diagnostics;
using System.Text;

namespace NovaGet.Core.Engine.Streams;

/// <summary>One downloaded track handed to the muxer.</summary>
public sealed record MuxInput(string Path, TrackKind Kind);

/// <summary>Joins downloaded tracks into one file without re-encoding.</summary>
public interface IStreamMuxer
{
    bool IsAvailable { get; }

    /// <summary>Copies the tracks' streams into <paramref name="output"/> (MP4). Throws <see cref="StreamMuxException"/> on failure.</summary>
    Task MuxAsync(IReadOnlyList<MuxInput> inputs, string output, CancellationToken cancellationToken);
}

public sealed class StreamMuxException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// ffmpeg (LGPL shared build, a separate process): <c>-c copy</c> into MP4. It only ever gets local files the
/// engine wrote (the protocol allowlist is <c>file</c>), never a playlist or an address, so it fetches nothing itself.
/// </summary>
public sealed class FfmpegMuxer(string? executable) : IStreamMuxer
{
    private const int MaxErrorText = 2000;

    public string? Executable { get; } = executable;

    public bool IsAvailable => Executable is not null && File.Exists(Executable);

    /// <summary>The bundled copy (<c>{app}\ffmpeg\</c>), else one on the PATH (development builds).</summary>
    public static string? Locate(string appDirectory)
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string[] bundled =
        [
            Path.Combine(appDirectory, "ffmpeg", name),
            Path.Combine(appDirectory, "ffmpeg", "bin", name),
            Path.Combine(appDirectory, name),
        ];
        var found = bundled.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }

    /// <summary>The command line (also used by tests): each input, its streams, then a stream copy into MP4.</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<MuxInput> inputs, string output)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        foreach (var input in inputs)
        {
            args.AddRange(["-protocol_whitelist", "file", "-i", input.Path]);
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            var n = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (inputs[i].Kind != TrackKind.Audio)
            {
                args.AddRange(["-map", n + ":v?"]);
            }

            if (inputs[i].Kind != TrackKind.Video)
            {
                args.AddRange(["-map", n + ":a?"]);
            }
        }

        args.AddRange(["-c", "copy", "-movflags", "+faststart", "-f", "mp4", output]);
        return args;
    }

    public async Task MuxAsync(IReadOnlyList<MuxInput> inputs, string output, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new StreamMuxException("ffmpeg was not found.");
        }

        var start = new ProcessStartInfo(Executable!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in Arguments(inputs, output))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (errors)
            {
                if (errors.Length < MaxErrorText)
                {
                    errors.AppendLine(e.Data);
                }
            }
        };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new StreamMuxException("ffmpeg could not be started: " + ex.Message, ex);
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            string text;
            lock (errors)
            {
                text = errors.ToString().Trim();
            }

            throw new StreamMuxException(string.IsNullOrEmpty(text) ? $"ffmpeg failed (exit code {process.ExitCode})." : "ffmpeg failed: " + text);
        }
    }
}
