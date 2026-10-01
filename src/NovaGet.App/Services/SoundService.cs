using System.IO;
using System.Media;
using Microsoft.Extensions.Logging;
using NovaGet.Core.Engine;
using NovaGet.Core.Models;
using NovaGet.Core.Paths;
using NovaGet.Core.Services;
using NovaGet.Core.Settings;

namespace NovaGet.App.Services;

/// <summary>Options → Sounds: plays the configured .wav for download and queue events.</summary>
internal sealed class SoundService(
    IDownloadService downloads,
    ISettingsService settings,
    AppPaths paths,
    ILogger<SoundService> logger) : IDisposable
{
    private SoundPlayer? _player;

    public void Initialize()
    {
        downloads.StateChanged += OnStateChanged;
        downloads.Changed += OnListChanged;
    }

    public void Play(SoundEvent soundEvent)
    {
        var entry = settings.Current.Sounds.Get(soundEvent);
        if (entry.Enabled)
        {
            Preview(entry.Path);
        }
    }

    /// <summary>Plays a file now (the ▶ button). Returns false when it can't be played.</summary>
    public bool Preview(string path)
    {
        var file = Resolve(path);
        if (file is null)
        {
            return false;
        }

        try
        {
            _player?.Dispose();
            _player = new SoundPlayer(file);
            _player.Play(); // asynchronous; a new sound replaces one still playing
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UriFormatException)
        {
            logger.LogWarning(ex, "Could not play {Sound}", file);
            return false;
        }
    }

    /// <summary>Absolute paths as they are; relative ones are relative to the install folder.</summary>
    public string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        var full = Path.IsPathRooted(expanded) ? expanded : Path.Combine(paths.ExecutableDir, expanded);
        return File.Exists(full) ? full : null;
    }

    public void Dispose()
    {
        downloads.StateChanged -= OnStateChanged;
        downloads.Changed -= OnListChanged;
        _player?.Dispose();
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (e.Status == DownloadStatus.Completed)
        {
            Play(SoundEvent.DownloadComplete);
        }
        else if (e.Status == DownloadStatus.Error)
        {
            Play(SoundEvent.DownloadFailed);
        }
    }

    private void OnListChanged(object? sender, DownloadListChangedEventArgs e)
    {
        if (e.Change == DownloadListChange.Added)
        {
            Play(SoundEvent.DownloadAdded);
        }
    }
}
