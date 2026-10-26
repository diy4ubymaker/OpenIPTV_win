using System.Text.Json.Serialization;

namespace OpenIPTV.Core.Models;

/// <summary>Playability of a stream or channel, as determined by the last health check.</summary>
public enum Availability
{
    Unchecked = 0,
    Active = 1,
    Inactive = 2,
}

/// <summary>One playable URL of a channel.</summary>
public sealed class StreamInfo
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Feed { get; set; }
    public string? Quality { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public List<string> Labels { get; set; } = [];
    public List<string> Origins { get; set; } = [];

    public Availability Status { get; set; } = Availability.Unchecked;
    public DateTime? LastChecked { get; set; }
    public string? LastStatus { get; set; }
    public int FailCount { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>Numeric part of the quality label ("720p" → 720) for ordering.</summary>
    [JsonIgnore]
    public int QualityValue
    {
        get
        {
            if (string.IsNullOrEmpty(Quality)) return 0;
            int n = 0;
            foreach (var ch in Quality)
            {
                if (!char.IsDigit(ch)) break;
                n = n * 10 + (ch - '0');
            }
            return n;
        }
    }

    [JsonIgnore]
    public bool IsHttp => Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A TV channel with all of its streams.</summary>
public sealed class Channel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> AltNames { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public string? CountryCode { get; set; }
    public string? LogoUrl { get; set; }
    public string? Website { get; set; }
    public bool IsNsfw { get; set; }
    public List<string> Origins { get; set; } = [];
    public List<StreamInfo> Streams { get; set; } = [];
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>Active if any stream is active; Inactive if all streams were checked and none is; else Unchecked.</summary>
    [JsonIgnore]
    public Availability Status
    {
        get
        {
            if (Streams.Count == 0) return Availability.Unchecked;
            bool anyUnchecked = false;
            foreach (var s in Streams)
            {
                if (s.Status == Availability.Active) return Availability.Active;
                if (s.Status == Availability.Unchecked) anyUnchecked = true;
            }
            return anyUnchecked ? Availability.Unchecked : Availability.Inactive;
        }
    }

    /// <summary>Streams ordered best first: active, then unchecked, then inactive; higher quality first.</summary>
    public IEnumerable<StreamInfo> OrderedStreams() =>
        Streams.OrderBy(s => s.Status switch
            {
                Availability.Active => 0,
                Availability.Unchecked => 1,
                _ => 2,
            })
            .ThenByDescending(s => s.QualityValue);
}

/// <summary>Everything the app knows about channels; persisted as catalog.json.</summary>
public sealed class Catalog
{
    public Dictionary<string, Channel> Channels { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Countries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Languages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? LastSync { get; set; }

    public string CountryName(string? code) =>
        code is not null && Countries.TryGetValue(code, out var n) ? n : code ?? "";

    public string LanguageName(string code) => Languages.TryGetValue(code, out var n) ? n : code;

    public string CategoryName(string id) => Categories.TryGetValue(id, out var n) ? n : id;
}
