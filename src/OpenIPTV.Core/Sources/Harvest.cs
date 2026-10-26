using OpenIPTV.Core.Models;

namespace OpenIPTV.Core.Sources;

/// <summary>A scalar value that only a source of equal or higher priority may overwrite.</summary>
public sealed class Prioritized
{
    public string? Value { get; private set; }
    private int _priority = -1;

    public void Offer(string? value, int priority)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (priority > _priority || Value is null)
        {
            Value = value.Trim();
            _priority = Math.Max(priority, _priority);
        }
    }
}

public sealed class ChannelRec(string id)
{
    public string Id { get; } = id;
    public Prioritized Name { get; } = new();
    public Prioritized Country { get; } = new();
    public Prioritized Logo { get; } = new();
    public Prioritized Website { get; } = new();
    public HashSet<string> AltNames { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Origins { get; } = new(StringComparer.Ordinal);
    public bool IsNsfw { get; set; }
}

public sealed class StreamRec
{
    public required string ChannelId { get; init; }
    public required string Url { get; init; }
    public string? Title { get; set; }
    public string? Feed { get; set; }
    public string? Quality { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public HashSet<string> Labels { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Origins { get; } = new(StringComparer.Ordinal);
}

/// <summary>Everything harvested in one sync cycle, before it is merged into the catalog.</summary>
public sealed class Harvest
{
    public Dictionary<string, ChannelRec> Channels { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string ChannelId, string Url), StreamRec> Streams { get; } = [];
    public Dictionary<string, string> Countries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string ChannelId, string FeedId), List<string>> FeedLanguages { get; } = [];
    public Dictionary<string, List<string>> MainFeedLanguages { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (long Score, string Url)> Logos { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Blocklist { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Errors { get; } = [];
    public Dictionary<string, string> Stats { get; } = [];

    public ChannelRec Channel(string id)
    {
        if (!Channels.TryGetValue(id, out var rec)) Channels[id] = rec = new ChannelRec(id);
        return rec;
    }

    public void AddStream(StreamRec s)
    {
        var key = (s.ChannelId, s.Url);
        if (!Streams.TryGetValue(key, out var cur)) { Streams[key] = s; return; }
        cur.Origins.UnionWith(s.Origins);
        cur.Labels.UnionWith(s.Labels);
        cur.Title ??= s.Title;
        cur.Feed ??= s.Feed;
        cur.Quality ??= s.Quality;
        cur.Referrer ??= s.Referrer;
        cur.UserAgent ??= s.UserAgent;
    }

    /// <summary>Fill derived fields once every source has been ingested.</summary>
    public void FinalizeData()
    {
        foreach (var (id, ch) in Channels)
        {
            if (Logos.TryGetValue(id, out var logo)) ch.Logo.Offer(logo.Url, 100);
            if (MainFeedLanguages.TryGetValue(id, out var langs)) ch.Languages.UnionWith(langs);
        }
        foreach (var s in Streams.Values)
        {
            if (s.Feed is not null && FeedLanguages.TryGetValue((s.ChannelId, s.Feed), out var fl)
                && Channels.TryGetValue(s.ChannelId, out var ch))
                ch.Languages.UnionWith(fl);
        }
        foreach (var ch in Channels.Values)
        {
            if (ch.Country.Value is { } cc && !Countries.ContainsKey(cc)) Countries[cc] = cc;
            foreach (var c in ch.Categories)
                if (!Categories.ContainsKey(c))
                    Categories[c] = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c.Replace('-', ' '));
        }
    }
}

/// <summary>Merges a harvest into the catalog, preserving stream health across cycles.</summary>
public static class CatalogMerger
{
    public sealed record MergeResult(Catalog Catalog, int Channels, int Streams, int SkippedBlocked, int SkippedNsfw, int Removed);

    /// <param name="prune">True only when every enabled source succeeded; otherwise data not seen is kept.</param>
    public static MergeResult Merge(Catalog old, Harvest h, AppSettings settings, bool prune, DateTime now)
    {
        var cat = new Catalog { LastSync = now };
        foreach (var (k, v) in old.Countries) cat.Countries[k] = v;
        foreach (var (k, v) in old.Languages) cat.Languages[k] = v;
        foreach (var (k, v) in old.Categories) cat.Categories[k] = v;
        foreach (var (k, v) in h.Countries) cat.Countries[k] = v;
        foreach (var (k, v) in h.Languages) cat.Languages[k] = v;
        foreach (var (k, v) in h.Categories) cat.Categories[k] = v;

        var streamsByChannel = h.Streams.Values.GroupBy(s => s.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        int blocked = 0, nsfw = 0, streamCount = 0;

        foreach (var (id, rec) in h.Channels)
        {
            streamsByChannel.TryGetValue(id, out var srecs);
            if ((srecs is null || srecs.Count == 0) && !settings.KeepChannelsWithoutStreams) continue;
            if (h.Blocklist.Contains(id)) { blocked++; continue; }
            if (rec.IsNsfw && !settings.IncludeNsfw) { nsfw++; continue; }

            old.Channels.TryGetValue(id, out var prev);
            var ch = new Channel
            {
                Id = id,
                Name = rec.Name.Value ?? id,
                AltNames = rec.AltNames.Order(StringComparer.Ordinal).ToList(),
                Categories = rec.Categories.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                Languages = rec.Languages.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                CountryCode = rec.Country.Value?.ToUpperInvariant(),
                LogoUrl = rec.Logo.Value,
                Website = rec.Website.Value,
                IsNsfw = rec.IsNsfw,
                Origins = rec.Origins.Order(StringComparer.Ordinal).ToList(),
                FirstSeen = prev?.FirstSeen ?? now,
                LastSeen = now,
            };
            var prevStreams = prev?.Streams.ToDictionary(s => s.Url, StringComparer.Ordinal)
                              ?? new Dictionary<string, StreamInfo>(StringComparer.Ordinal);
            foreach (var s in srecs ?? [])
            {
                prevStreams.TryGetValue(s.Url, out var ps);
                ch.Streams.Add(new StreamInfo
                {
                    Url = s.Url, Title = s.Title, Feed = s.Feed, Quality = s.Quality,
                    Referrer = s.Referrer, UserAgent = s.UserAgent,
                    Labels = s.Labels.Order(StringComparer.Ordinal).ToList(),
                    Origins = s.Origins.Order(StringComparer.Ordinal).ToList(),
                    Status = ps?.Status ?? Availability.Unchecked,
                    LastChecked = ps?.LastChecked, LastStatus = ps?.LastStatus, FailCount = ps?.FailCount ?? 0,
                    FirstSeen = ps?.FirstSeen ?? now, LastSeen = now,
                });
                prevStreams.Remove(s.Url);
            }
            if (!prune) ch.Streams.AddRange(prevStreams.Values);   // keep streams a failed source used to provide
            streamCount += ch.Streams.Count;
            cat.Channels[id] = ch;
        }

        int removed = 0;
        foreach (var (id, prev) in old.Channels)
        {
            if (cat.Channels.ContainsKey(id)) continue;
            if (prune || h.Blocklist.Contains(id)) { removed++; continue; }
            cat.Channels[id] = prev;                               // keep until all sources agree it is gone
            streamCount += prev.Streams.Count;
        }
        if (prune)
            removed += old.Channels.Values.Where(c => cat.Channels.ContainsKey(c.Id))
                .Sum(c => c.Streams.Count(s => !cat.Channels[c.Id].Streams.Any(n => n.Url == s.Url)));

        return new MergeResult(cat, cat.Channels.Count, streamCount, blocked, nsfw, removed);
    }
}
