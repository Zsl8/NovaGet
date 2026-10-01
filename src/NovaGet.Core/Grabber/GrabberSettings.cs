using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovaGet.Core.Grabber;

/// <summary>The wizard's project templates (section 14, step 1).</summary>
public enum GrabberTemplate
{
    Images,
    Video,
    Audio,
    FileTypes,
    OfflineSite,
    Custom,
}

/// <summary>A site grabber project's settings (GrabberProject.settingsJson). Secrets are stored DPAPI-protected.</summary>
public sealed record GrabberSettings
{
    public const string ImageExtensions = "jpg jpeg png gif webp bmp svg avif tif tiff ico";
    public const string VideoExtensions = "avi mpg mpe mpeg asf wmv mov qt rm mp4 mkv flv m4v webm 3gp ts";
    public const string AudioExtensions = "mp3 wav wma mpa ram ra aac aif m4a flac ogg opus";
    public const string DocumentAndArchiveExtensions = "zip rar 7z gz tar bz2 xz exe msi iso pdf doc docx xls xlsx ppt pptx txt epub";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    // ---- step 1: template, start page, login

    public GrabberTemplate Template { get; init; } = GrabberTemplate.Images;

    public string StartUrl { get; init; } = string.Empty;

    public bool UseAuthorization { get; init; }

    public string? UserName { get; init; }

    /// <summary>DPAPI-protected in the database; plain in memory (see <see cref="Protect"/>).</summary>
    public string? Password { get; init; }

    /// <summary>A Cookie header for the start site (from "Log in via browser…"); protected like the password.</summary>
    public string? Cookies { get; init; }

    // ---- step 2: where files go

    public string SaveFolder { get; init; } = string.Empty;

    /// <summary>Files go into a subfolder named after their category (Compressed, Video, …) under the save folder.</summary>
    public bool SaveByCategory { get; init; } = true;

    /// <summary>Pages are saved too, with links rewritten to the local copies (offline browsing).</summary>
    public bool ConvertLinks { get; init; }

    // ---- step 3: exploration

    /// <summary>How many links away from the start page pages are explored (0 = the start page only).</summary>
    public int Depth { get; init; } = 2;

    public bool StayOnSite { get; init; } = true;

    public bool ExploreSubdomains { get; init; }

    public bool FollowExternalLinks { get; init; }

    /// <summary>How far to follow links once they leave the site.</summary>
    public int ExternalDepth { get; init; } = 1;

    /// <summary>0 = unlimited (a safety limit of <see cref="SiteCrawler.MaxPagesHardLimit"/> still applies).</summary>
    public int MaxPages { get; init; }

    /// <summary>Wildcard address filters; when any is given, explored pages must match one.</summary>
    public IReadOnlyList<string> IncludeFilters { get; init; } = [];

    /// <summary>Wildcard address filters for pages and files that must be left out.</summary>
    public IReadOnlyList<string> ExcludeFilters { get; init; } = [];

    public bool ObeyRobots { get; init; } = true;

    public int DelayMs { get; init; }

    public int MaxParallel { get; init; } = 4;

    // ---- step 4: files

    /// <summary>Extension patterns (space separated, wildcards allowed) of the files to download.</summary>
    public string FileTypes { get; init; } = ImageExtensions;

    /// <summary>Size limits in KB (0 = none); files of unknown size pass.</summary>
    public long MinSizeKB { get; init; }

    public long MaxSizeKB { get; init; }

    /// <summary>List only files of the chosen types (otherwise every file found is listed, the matching ones checked).</summary>
    public bool OnlyMatching { get; init; } = true;

    /// <summary>The settings a template starts from (the wizard can still change everything).</summary>
    public static GrabberSettings ForTemplate(GrabberTemplate template, GrabberSettings? current = null)
    {
        var settings = (current ?? new GrabberSettings()) with { Template = template };
        return template switch
        {
            GrabberTemplate.Images => settings with { FileTypes = ImageExtensions, ConvertLinks = false, OnlyMatching = true, Depth = 2 },
            GrabberTemplate.Video => settings with { FileTypes = VideoExtensions, ConvertLinks = false, OnlyMatching = true, Depth = 2 },
            GrabberTemplate.Audio => settings with { FileTypes = AudioExtensions, ConvertLinks = false, OnlyMatching = true, Depth = 2 },
            GrabberTemplate.FileTypes => settings with { FileTypes = DocumentAndArchiveExtensions, ConvertLinks = false, OnlyMatching = true, Depth = 2 },
            GrabberTemplate.OfflineSite => settings with { FileTypes = "*", ConvertLinks = true, SaveByCategory = false, OnlyMatching = true, Depth = 3 },
            _ => settings,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public static GrabberSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GrabberSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<GrabberSettings>(json, s_json) ?? new GrabberSettings();
        }
        catch (JsonException)
        {
            return new GrabberSettings();
        }
    }

    /// <summary>The settings as stored: password and cookies protected (DPAPI on Windows).</summary>
    public GrabberSettings Protect(Func<string?, string?> protect)
    {
        ArgumentNullException.ThrowIfNull(protect);
        return this with { Password = protect(Password), Cookies = protect(Cookies) };
    }

    public GrabberSettings Unprotect(Func<string?, string?> unprotect)
    {
        ArgumentNullException.ThrowIfNull(unprotect);
        return this with { Password = unprotect(Password), Cookies = unprotect(Cookies) };
    }

    /// <summary>Why the settings can't be run, or null.</summary>
    public string? Validate()
    {
        if (!Uri.TryCreate(StartUrl.Trim(), UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
        {
            return "StartUrl";
        }

        if (string.IsNullOrWhiteSpace(SaveFolder) || !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(SaveFolder.Trim())))
        {
            return "SaveFolder";
        }

        if (Depth is < 0 or > 20 || ExternalDepth is < 0 or > 20 || MaxPages < 0 || DelayMs is < 0 or > 600_000 || MaxParallel is < 1 or > 16)
        {
            return "Exploration";
        }

        if (MinSizeKB < 0 || MaxSizeKB < 0 || (MaxSizeKB > 0 && MinSizeKB > MaxSizeKB))
        {
            return "Size";
        }

        return string.IsNullOrWhiteSpace(FileTypes) ? "FileTypes" : null;
    }
}
