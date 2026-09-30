namespace NovaGet.Core.Settings;

public interface ISettingsService
{
    /// <summary>The live settings object. Mutate it only through <see cref="Update"/>.</summary>
    AppSettings Current { get; }

    /// <summary>Raised after settings were changed and saved.</summary>
    event EventHandler? Changed;

    /// <summary>Applies a change, persists it atomically and raises <see cref="Changed"/>.</summary>
    void Update(Action<AppSettings> mutate);

    /// <summary>Persists the current settings.</summary>
    void Save();

    /// <summary>Replaces every setting (import, reset to defaults), persists and raises <see cref="Changed"/>.</summary>
    void Replace(AppSettings settings);
}
