using CommunityToolkit.Mvvm.ComponentModel;
using NovaGet.App.Localization;
using NovaGet.Core.Engine.Streams;
using NovaGet.Core.Formatting;

namespace NovaGet.App.ViewModels;

/// <summary>A quality in the list: "MP4 1920x1080 · 4.5 Mbps" with its estimated size.</summary>
public sealed class StreamOptionViewModel(StreamVariant variant, double duration)
{
    public StreamVariant Variant { get; } = variant;

    public string Label => Variant.Label;

    /// <summary>Bit rate × duration, when both are known ("≈ 342 MB").</summary>
    public long EstimatedSize { get; } = duration > 0 && variant.Bandwidth > 0 ? (long)(variant.Bandwidth / 8.0 * duration) : -1;

    public string SizeText => EstimatedSize > 0 ? "≈ " + DisplayFormat.Size(EstimatedSize, 0) : string.Empty;
}

/// <summary>An audio language/rendition choice.</summary>
public sealed record StreamAudioOption(string Id, string Title);

/// <summary>The "Choose quality" dialog for HLS/DASH streams (section 4.8).</summary>
public sealed partial class StreamQualityViewModel : ObservableObject
{
    public StreamQualityViewModel(StreamInfo info, string title)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        Title = title;
        Options = [.. info.Variants
            .OrderBy(v => v.AudioOnly)
            .ThenByDescending(v => v.Height)
            .ThenByDescending(v => v.Bandwidth)
            .Select(v => new StreamOptionViewModel(v, info.Duration))];
        _selected = Options.FirstOrDefault(o => o.Variant == info.Best) ?? (Options.Count > 0 ? Options[0] : null);
        UpdateAudio();
    }

    public StreamInfo Info { get; }

    public string Title { get; }

    public IReadOnlyList<StreamOptionViewModel> Options { get; }

    public string DurationText => Info.Duration > 0
        ? Localizer.Format("Stream_Duration", DisplayFormat.Duration(TimeSpan.FromSeconds(Math.Round(Info.Duration))))
        : string.Empty;

    public bool IsLive => Info.IsLive;

    [ObservableProperty]
    private StreamOptionViewModel? _selected;

    [ObservableProperty]
    private IReadOnlyList<StreamAudioOption> _audioOptions = [];

    [ObservableProperty]
    private StreamAudioOption? _selectedAudio;

    public bool HasAudioChoice => AudioOptions.Count > 1;

    /// <summary>What the download remembers.</summary>
    public StreamSelection Selection => new()
    {
        Kind = Info.Kind,
        ManifestUrl = Info.ManifestUrl.AbsoluteUri,
        VariantId = Selected?.Variant.Id,
        AudioId = HasAudioChoice ? SelectedAudio?.Id : null,
    };

    /// <summary>".m4a" for audio-only choices, else ".mp4".</summary>
    public string Extension => Selected?.Variant.AudioOnly == true ? ".m4a" : ".mp4";

    partial void OnSelectedChanged(StreamOptionViewModel? value) => UpdateAudio();

    partial void OnAudioOptionsChanged(IReadOnlyList<StreamAudioOption> value) => OnPropertyChanged(nameof(HasAudioChoice));

    /// <summary>HLS: the renditions of the variant's audio group; DASH: every audio representation.</summary>
    private void UpdateAudio()
    {
        var variant = Selected?.Variant;
        var renditions = variant is null || variant.AudioOnly
            ? []
            : Info.Kind == StreamKind.Dash ? Info.Audio : Info.Audio.Where(a => a.GroupId == variant.AudioId).ToList();
        AudioOptions = [.. renditions.Select(a => new StreamAudioOption(a.Id, AudioTitle(a)))];
        var preferred = renditions.FirstOrDefault(a => a.Id == SelectedAudio?.Id)
            ?? renditions.FirstOrDefault(a => a.Id == variant?.AudioId)
            ?? renditions.FirstOrDefault(a => a.IsDefault)
            ?? (renditions.Count > 0 ? renditions[0] : null);
        SelectedAudio = AudioOptions.FirstOrDefault(o => o.Id == preferred?.Id);
    }

    private static string AudioTitle(StreamAudio audio)
    {
        var name = audio.Name ?? audio.Language ?? audio.Id;
        var rate = audio.Bandwidth > 0 ? $" · {audio.Bandwidth / 1000} kbps" : string.Empty;
        return audio.Language is { } language && audio.Name is not null && !audio.Name.Contains(language, StringComparison.OrdinalIgnoreCase)
            ? $"{name} ({language}){rate}"
            : name + rate;
    }
}
