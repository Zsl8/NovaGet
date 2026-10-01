using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaGet.Core.Paths;

namespace NovaGet.Core.Settings;

/// <summary>
/// Loads and saves settings.json. Writes are atomic (temp file + replace) and keep the
/// previous version as settings.json.bak, which is used if the main file is ever corrupt.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly string _backupFile;
    private readonly ILogger _logger;
    private readonly string? _installDefaultsFile;
    private AppSettings _current;

    public SettingsService(AppPaths paths, ILogger<SettingsService>? logger = null)
        : this(paths.SettingsFile, paths.SettingsBackupFile, logger, Path.Combine(paths.ExecutableDir, InstallDefaults.FileName))
    {
    }

    /// <param name="installDefaultsFile">The installer's choices, applied when there are no settings yet.</param>
    public SettingsService(string file, string backupFile, ILogger<SettingsService>? logger = null, string? installDefaultsFile = null)
    {
        _file = file;
        _backupFile = backupFile;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _installDefaultsFile = installDefaultsFile;
        _current = Load();
    }

    /// <summary>True when no settings existed (a first run): defaults were used, with the installer's choices.</summary>
    public bool IsFirstRun { get; private set; }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            mutate(_current);
            SettingsNormalizer.Normalize(_current);
            Write(_current);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        lock (_gate)
        {
            Write(_current);
        }
    }

    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = SettingsJson.Clone(settings);
        SettingsNormalizer.Normalize(copy);
        lock (_gate)
        {
            _current = copy;
            Write(_current);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        var main = TryRead(_file);
        if (main is not null)
        {
            return main;
        }

        var mainWasCorrupt = File.Exists(_file);
        var restored = TryRead(_backupFile);
        if (mainWasCorrupt)
        {
            // Set the unreadable file aside so the next save cannot rotate it over the good backup.
            try
            {
                File.Move(_file, _file + ".corrupt", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not move corrupt settings file aside");
            }
        }

        if (restored is not null)
        {
            _logger.LogWarning("Settings file was missing or unreadable; restored from backup {Backup}", _backupFile);
            return restored;
        }

        var defaults = new AppSettings();
        if (!mainWasCorrupt)
        {
            IsFirstRun = true;
            if (_installDefaultsFile is not null && InstallDefaults.TryApply(defaults, _installDefaultsFile))
            {
                _logger.LogInformation("First run: applied the installer's choices from {File}", _installDefaultsFile);
            }
        }

        SettingsNormalizer.Normalize(defaults);
        return defaults;
    }

    private AppSettings? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var settings = SettingsJson.Deserialize(File.ReadAllText(path, Encoding.UTF8));
            SettingsNormalizer.Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "Could not read settings from {File}", path);
            return null;
        }
    }

    private void Write(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _file + ".tmp";
        var json = SettingsJson.Serialize(settings);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_file))
        {
            // Atomic swap that also keeps the previous good copy as the backup.
            File.Replace(temp, _file, _backupFile, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, _file);
        }
    }
}
