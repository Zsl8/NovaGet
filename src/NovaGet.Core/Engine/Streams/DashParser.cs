using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NovaGet.Core.Engine.Streams;

/// <summary>
/// MPEG-DASH manifests (static, first period): representations with SegmentTemplate (number or time based, with a
/// SegmentTimeline), SegmentList, or a single file (SegmentBase / BaseURL). Any ContentProtection is DRM.
/// </summary>
public sealed partial class DashManifest
{
    private static readonly Dictionary<string, string> s_drmNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"] = "Widevine",
        ["urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95"] = "PlayReady",
        ["urn:uuid:94ce86fb-07ff-4f43-adb8-93d2fa968ca2"] = "FairPlay",
        ["urn:uuid:e2719d58-a985-b3c9-781a-b030af78d30e"] = "ClearKey",
        ["urn:mpeg:dash:mp4protection:2011"] = "CENC",
    };

    private readonly List<Representation> _representations = [];

    private DashManifest(StreamInfo info, List<Representation> representations)
    {
        Info = info;
        _representations = representations;
    }

    public StreamInfo Info { get; }

    public static bool LooksLikeDash(string text) =>
        text.Contains("<MPD", StringComparison.Ordinal) && text.Contains("urn:mpeg:dash:schema:mpd", StringComparison.Ordinal);

    public static DashManifest Parse(string xml, Uri manifestUrl)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 32 * 1024 * 1024,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The DASH manifest is not valid XML: " + ex.Message, ex);
        }

        var mpd = document.Root ?? throw new FormatException("The DASH manifest is empty.");
        var ns = mpd.Name.Namespace;
        var isLive = (string?)mpd.Attribute("type") == "dynamic";
        var period = mpd.Elements(ns + "Period").FirstOrDefault() ?? throw new FormatException("The DASH manifest has no period.");
        var periodDuration = Duration((string?)period.Attribute("duration")) ?? Duration((string?)mpd.Attribute("mediaPresentationDuration")) ?? 0;
        var baseUrl = Base(Base(manifestUrl, mpd, ns), period, ns);

        string? protection = null;
        var representations = new List<Representation>();
        foreach (var set in period.Elements(ns + "AdaptationSet"))
        {
            var setBase = Base(baseUrl, set, ns);
            var contentType = (string?)set.Attribute("contentType") ?? MainType((string?)set.Attribute("mimeType"));
            protection ??= Protection(set, ns);
            foreach (var rep in set.Elements(ns + "Representation"))
            {
                protection ??= Protection(rep, ns);
                var type = (string?)rep.Attribute("contentType") ?? contentType ?? MainType((string?)rep.Attribute("mimeType"));
                if (type is not ("video" or "audio"))
                {
                    continue; // subtitles, images (thumbnails), ...
                }

                representations.Add(new Representation(
                    (string?)rep.Attribute("id") ?? representations.Count.ToString(CultureInfo.InvariantCulture),
                    type == "video" ? TrackKind.Video : TrackKind.Audio,
                    (long?)Number(rep.Attribute("bandwidth")) ?? 0,
                    (int?)Number(rep.Attribute("width")) ?? (int?)Number(set.Attribute("width")) ?? 0,
                    (int?)Number(rep.Attribute("height")) ?? (int?)Number(set.Attribute("height")) ?? 0,
                    (string?)rep.Attribute("codecs") ?? (string?)set.Attribute("codecs"),
                    (string?)set.Attribute("lang"),
                    Base(setBase, rep, ns),
                    set,
                    rep,
                    periodDuration));
            }
        }

        var videos = representations.Where(r => r.Kind == TrackKind.Video).ToList();
        var audios = representations.Where(r => r.Kind == TrackKind.Audio).ToList();
        var bestAudio = audios.OrderByDescending(a => a.Bandwidth).FirstOrDefault();
        var variants = videos.Count > 0
            ? videos.Select(v => new StreamVariant
            {
                Id = v.Id,
                Width = v.Width,
                Height = v.Height,
                Bandwidth = v.Bandwidth + (bestAudio?.Bandwidth ?? 0),
                Codecs = v.Codecs,
                AudioId = bestAudio?.Id,
            }).ToList()
            : audios.Select(a => new StreamVariant { Id = a.Id, Bandwidth = a.Bandwidth, Codecs = a.Codecs, AudioOnly = true }).ToList();

        var info = new StreamInfo
        {
            Kind = StreamKind.Dash,
            ManifestUrl = manifestUrl,
            Variants = variants,
            Audio = [.. audios.Select(a => new StreamAudio(a.Id, "audio", a.Language, a.Language, null, a.Bandwidth, a == bestAudio))],
            Protection = protection,
            IsLive = isLive,
            Duration = periodDuration,
        };
        return new DashManifest(info, representations);
    }

    /// <summary>The tracks to download for a chosen video representation (plus its audio, when separate).</summary>
    public IReadOnlyList<MediaTrack> Tracks(string? variantId, string? audioId)
    {
        var chosen = _representations.FirstOrDefault(r => r.Id == variantId)
            ?? _representations.Where(r => r.Kind == TrackKind.Video).OrderByDescending(r => r.Height).ThenByDescending(r => r.Bandwidth).FirstOrDefault()
            ?? _representations.OrderByDescending(r => r.Bandwidth).FirstOrDefault()
            ?? throw new FormatException("The DASH manifest has no playable representation.");
        var tracks = new List<MediaTrack> { chosen.Track() };
        if (chosen.Kind == TrackKind.Video && _representations.FirstOrDefault(r => r.Kind == TrackKind.Audio && (audioId is null || r.Id == audioId)) is { } audio)
        {
            tracks.Add(audio.Track());
        }

        return tracks;
    }

    private static string? MainType(string? mime) => mime?.Split('/')[0];

    private static string? Protection(XElement element, XNamespace ns)
    {
        var protection = element.Elements(ns + "ContentProtection").FirstOrDefault();
        if (protection is null)
        {
            return null;
        }

        var scheme = (string?)protection.Attribute("schemeIdUri") ?? "ContentProtection";
        var named = element.Elements(ns + "ContentProtection")
            .Select(p => (string?)p.Attribute("schemeIdUri"))
            .Select(s => s is not null && s_drmNames.TryGetValue(s, out var name) && name != "CENC" ? name : null)
            .FirstOrDefault(n => n is not null);
        return named ?? (s_drmNames.TryGetValue(scheme, out var known) ? known : scheme);
    }

    private static Uri Base(Uri parent, XElement element, XNamespace ns)
    {
        var text = (string?)element.Element(ns + "BaseURL");
        return string.IsNullOrWhiteSpace(text) ? parent : HlsParser.Resolve(parent, text.Trim());
    }

    private static double? Number(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>ISO 8601 durations such as <c>PT1H2M3.5S</c> (days allowed).</summary>
    internal static double? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = DurationRegex().Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }

        double Part(int group) => m.Groups[group].Success ? double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        return (Part(1) * 86400) + (Part(2) * 3600) + (Part(3) * 60) + Part(4);
    }

    /// <summary>Fills <c>$RepresentationID$</c>, <c>$Number%05d$</c>, <c>$Time$</c>, <c>$Bandwidth$</c> and <c>$$</c>.</summary>
    internal static string Fill(string template, string id, long number, long time, long bandwidth) =>
        TemplateRegex().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            long? value = name switch
            {
                "Number" => number,
                "Time" => time,
                "Bandwidth" => bandwidth,
                _ => null,
            };
            return name switch
            {
                "" => "$",
                "RepresentationID" => id,
                _ when value is { } v && format is not null => v.ToString("D" + int.Parse(format[2..^1], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                _ when value is { } v => v.ToString(CultureInfo.InvariantCulture),
                _ => m.Value,
            };
        });

    [GeneratedRegex(@"^P(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"\$(RepresentationID|Number|Time|Bandwidth|)(%0\d+d)?\$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateRegex();

    private sealed record Representation(
        string Id,
        TrackKind Kind,
        long Bandwidth,
        int Width,
        int Height,
        string? Codecs,
        string? Language,
        Uri BaseUrl,
        XElement Set,
        XElement Element,
        double PeriodDuration)
    {
        public MediaTrack Track()
        {
            var ns = Element.Name.Namespace;
            var template = Merge(Set.Element(ns + "SegmentTemplate"), Element.Element(ns + "SegmentTemplate"));
            if (template is not null)
            {
                return FromTemplate(template, ns);
            }

            var list = Element.Element(ns + "SegmentList") ?? Set.Element(ns + "SegmentList");
            if (list is not null)
            {
                return FromList(list, ns);
            }

            // SegmentBase or a bare BaseURL: the representation is one file.
            return new MediaTrack(Kind, null, [new MediaSegment(BaseUrl, null, null, 0, PeriodDuration)], "mp4");
        }

        private MediaTrack FromTemplate(XElement template, XNamespace ns)
        {
            var timescale = (long?)Number(template.Attribute("timescale")) ?? 1;
            var startNumber = (long?)Number(template.Attribute("startNumber")) ?? 1;
            var media = (string?)template.Attribute("media") ?? throw new FormatException($"Representation {Id} has no media template.");
            var initTemplate = (string?)template.Attribute("initialization");
            var init = initTemplate is null ? null : new MediaSegment(HlsParser.Resolve(BaseUrl, Fill(initTemplate, Id, 0, 0, Bandwidth)), null, null, -1, 0);
            var segments = new List<MediaSegment>();
            var timeline = template.Element(ns + "SegmentTimeline");
            if (timeline is not null)
            {
                long time = 0;
                var number = startNumber;
                var entries = timeline.Elements(ns + "S").ToList();
                for (var i = 0; i < entries.Count; i++)
                {
                    var s = entries[i];
                    time = (long?)Number(s.Attribute("t")) ?? time;
                    var d = (long?)Number(s.Attribute("d")) ?? throw new FormatException("SegmentTimeline entry without a duration.");
                    var repeat = (long?)Number(s.Attribute("r")) ?? 0;
                    if (repeat < 0)
                    {
                        // Repeat until the next entry's start, or the end of the period.
                        var end = i + 1 < entries.Count && Number(entries[i + 1].Attribute("t")) is { } next
                            ? (long)next
                            : (long)Math.Ceiling(PeriodDuration * timescale);
                        repeat = Math.Max(0, ((end - time) / d) - 1);
                    }

                    for (var r = 0; r <= repeat && segments.Count < 100_000; r++)
                    {
                        segments.Add(new MediaSegment(HlsParser.Resolve(BaseUrl, Fill(media, Id, number, time, Bandwidth)), null, null, number, d / (double)timescale));
                        time += d;
                        number++;
                    }
                }
            }
            else
            {
                var duration = (long?)Number(template.Attribute("duration")) ?? throw new FormatException($"Representation {Id} has neither a timeline nor a segment duration.");
                var count = (long)Math.Ceiling(PeriodDuration * timescale / duration);
                for (long i = 0; i < Math.Min(count, 100_000); i++)
                {
                    var number = startNumber + i;
                    segments.Add(new MediaSegment(HlsParser.Resolve(BaseUrl, Fill(media, Id, number, i * duration, Bandwidth)), null, null, number, duration / (double)timescale));
                }
            }

            return new MediaTrack(Kind, init, segments, "mp4");
        }

        private MediaTrack FromList(XElement list, XNamespace ns)
        {
            var timescale = (long?)Number(list.Attribute("timescale")) ?? 1;
            var duration = (long?)Number(list.Attribute("duration")) ?? 0;
            var initElement = list.Element(ns + "Initialization");
            MediaSegment? init = initElement?.Attribute("sourceURL") is { } source
                ? new MediaSegment(HlsParser.Resolve(BaseUrl, source.Value), Range(initElement.Attribute("range")), null, -1, 0)
                : null;
            var segments = list.Elements(ns + "SegmentURL")
                .Select((s, i) => new MediaSegment(
                    s.Attribute("media") is { } m ? HlsParser.Resolve(BaseUrl, m.Value) : BaseUrl,
                    Range(s.Attribute("mediaRange")),
                    null,
                    i,
                    duration / (double)timescale))
                .ToList();
            return new MediaTrack(Kind, init, segments, "mp4");
        }

        private static ByteRange? Range(XAttribute? attribute)
        {
            if (attribute is null)
            {
                return null;
            }

            var parts = attribute.Value.Split('-');
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end) && end >= start
                ? new ByteRange(start, end - start + 1)
                : null;
        }

        /// <summary>A representation's SegmentTemplate overrides the adaptation set's attribute by attribute.</summary>
        private static XElement? Merge(XElement? parent, XElement? child)
        {
            if (parent is null)
            {
                return child;
            }

            if (child is null)
            {
                return parent;
            }

            var merged = new XElement(child);
            foreach (var attribute in parent.Attributes())
            {
                if (merged.Attribute(attribute.Name) is null)
                {
                    merged.SetAttributeValue(attribute.Name, attribute.Value);
                }
            }

            if (!merged.Elements().Any())
            {
                merged.Add(parent.Elements());
            }

            return merged;
        }
    }
}

/// <summary>Reads manifest text (UTF-8 with or without BOM).</summary>
internal static class ManifestText
{
    public const int MaxBytes = 32 * 1024 * 1024;

    public static string Decode(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
}
