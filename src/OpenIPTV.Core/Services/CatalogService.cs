using System.Globalization;
using System.Text;
using OpenIPTV.Core.Models;
using OpenIPTV.Core.Storage;

namespace OpenIPTV.Core.Services;

public enum StatusFilter { All, Active, Inactive, Unchecked }

public static class SortFields
{
    public const string Name = "Name";
    public const string Category = "Category";
    public const string Language = "Language";
    public const string Country = "Country";
    public const string Code = "Code";
    public const string Sources = "Sources";
    public const string Status = "Status";
    public static readonly string[] All = [Name, Category, Language, Country, Code, Sources, Status];
}

public sealed record ChannelQuery
{
    public string? Text { get; init; }
    public IReadOnlyCollection<string> Categories { get; init; } = [];
    public IReadOnlyCollection<string> Languages { get; init; } = [];
    public IReadOnlyCollection<string> Countries { get; init; } = [];
    public StatusFilter Status { get; init; } = StatusFilter.Active;
    public IReadOnlyList<string> Sort { get; init; } = [SortFields.Name];
    public bool Descending { get; init; }
}

/// <summary>Flat, display-ready row for the channel list.</summary>
public sealed record ChannelRow(
    string Id, string Name, string Categories, string Languages, string Country, string CountryCode,
    string? LogoUrl, int StreamCount, Availability Status)
{
    public string StatusText => Status switch
    {
        Availability.Active => "✅ Active",
        Availability.Inactive => "❌ Inactive",
        _ => "⏳ Unchecked",
    };
}

public sealed record StatusCounts(int Total, int Active, int Inactive, int Unchecked);

public sealed record FacetItem(string Value, string Label, int Count)
{
    public override string ToString() => Label;
}

public sealed record StreamView(
    int Index, string Url, string? Title, string? Quality, IReadOnlyList<string> Labels, Availability Status,
    DateTime? LastChecked, string? LastStatus, string? Referrer, string? UserAgent, bool IsHttp)
{
    public string Display
    {
        get
        {
            var icon = Status switch { Availability.Active => "✅", Availability.Inactive => "❌", _ => "⏳" };
            var host = Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;
            var parts = new List<string> { icon };
            if (!string.IsNullOrEmpty(Quality)) parts.Add(Quality);
            if (!string.IsNullOrEmpty(Title)) parts.Add(Title.Length > 40 ? Title[..40] : Title);
            parts.Add("· " + host);
            if (Labels.Count > 0) parts.Add($"[{string.Join(", ", Labels)}]");
            return string.Join(" ", parts);
        }
    }
}

public sealed record ChannelDetail(
    string Id, string Name, string? LogoUrl, string? Website, string Categories, string Languages,
    string Country, string CountryCode, Availability Status, IReadOnlyList<StreamView> Streams);

/// <summary>
/// Owns the in-memory catalog. All reads and writes go through one lock; queries return copies,
/// so the UI never sees a catalog that the sync agent is changing.
/// </summary>
public sealed class CatalogService(JsonStore store)
{
    private readonly object _gate = new();
    private Catalog _catalog = new();

    public event Action? Changed;

    public void Load()
    {
        var c = store.LoadCatalog();
        lock (_gate) _catalog = c;
        Changed?.Invoke();
    }

    public void Save()
    {
        lock (_gate) store.SaveCatalog(_catalog);
    }

    public Catalog Snapshot()
    {
        lock (_gate) return _catalog;
    }

    public void Replace(Catalog c)
    {
        lock (_gate) _catalog = c;
        Changed?.Invoke();
    }

    /// <summary>Run an update under the lock (used for check results).</summary>
    public void Update(Action<Catalog> mutate)
    {
        lock (_gate) mutate(_catalog);
    }

    public void NotifyChanged() => Changed?.Invoke();

    public DateTime? LastSync { get { lock (_gate) return _catalog.LastSync; } }

    private static bool MatchesFilters(Channel ch, ChannelQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var t = q.Text.Trim();
            if (!ch.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                && !ch.Id.Contains(t, StringComparison.OrdinalIgnoreCase)
                && !ch.AltNames.Any(a => a.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        if (q.Categories.Count > 0 && !ch.Categories.Any(c => q.Categories.Contains(c, StringComparer.OrdinalIgnoreCase))) return false;
        if (q.Languages.Count > 0 && !ch.Languages.Any(l => q.Languages.Contains(l, StringComparer.OrdinalIgnoreCase))) return false;
        if (q.Countries.Count > 0 && (ch.CountryCode is null || !q.Countries.Contains(ch.CountryCode, StringComparer.OrdinalIgnoreCase))) return false;
        return true;
    }

    private static bool MatchesStatus(Availability a, StatusFilter f) => f switch
    {
        StatusFilter.Active => a == Availability.Active,
        StatusFilter.Inactive => a == Availability.Inactive,
        StatusFilter.Unchecked => a == Availability.Unchecked,
        _ => true,
    };

    private static ChannelRow ToRow(Catalog c, Channel ch) => new(
        ch.Id, ch.Name,
        string.Join(", ", ch.Categories.Select(c.CategoryName)),
        string.Join(", ", ch.Languages.Select(c.LanguageName)),
        c.CountryName(ch.CountryCode), ch.CountryCode ?? "",
        string.IsNullOrWhiteSpace(ch.LogoUrl) ? null : ch.LogoUrl,
        ch.Streams.Count, ch.Status);

    public (IReadOnlyList<ChannelRow> Rows, StatusCounts Counts) Search(ChannelQuery q)
    {
        List<ChannelRow> rows;
        int total = 0, active = 0, inactive = 0, unchecked_ = 0;
        lock (_gate)
        {
            rows = new List<ChannelRow>();
            foreach (var ch in _catalog.Channels.Values)
            {
                if (!MatchesFilters(ch, q)) continue;
                var st = ch.Status;
                total++;
                if (st == Availability.Active) active++;
                else if (st == Availability.Inactive) inactive++;
                else unchecked_++;
                if (MatchesStatus(st, q.Status)) rows.Add(ToRow(_catalog, ch));
            }
        }
        return (Sort(rows, q.Sort, q.Descending), new StatusCounts(total, active, inactive, unchecked_));
    }

    public static IReadOnlyList<ChannelRow> Sort(List<ChannelRow> rows, IReadOnlyList<string> sort, bool desc)
    {
        var cmp = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);
        IOrderedEnumerable<ChannelRow>? ordered = null;
        foreach (var field in sort.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().Append(SortFields.Name))
        {
            Func<ChannelRow, string> key = field switch
            {
                SortFields.Category => r => r.Categories,
                SortFields.Language => r => r.Languages,
                SortFields.Country => r => r.Country,
                SortFields.Code => r => r.CountryCode,
                SortFields.Sources => r => r.StreamCount.ToString("D6", CultureInfo.InvariantCulture),
                SortFields.Status => r => ((int)r.Status).ToString(CultureInfo.InvariantCulture),
                _ => r => r.Name,
            };
            // Empty values always last, whatever the direction.
            Func<ChannelRow, int> empty = r => key(r).Length == 0 ? 1 : 0;
            if (ordered is null)
                ordered = (desc ? rows.OrderBy(empty).ThenByDescending(key, cmp) : rows.OrderBy(empty).ThenBy(key, cmp));
            else
                ordered = desc ? ordered.ThenBy(empty).ThenByDescending(key, cmp) : ordered.ThenBy(empty).ThenBy(key, cmp);
        }
        return (ordered ?? rows.OrderBy(r => r.Name, cmp)).ToList();
    }

    /// <summary>Values used by channels, with channel counts — for the filter pickers.</summary>
    public IReadOnlyList<FacetItem> Facet(string kind)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Catalog c;
        lock (_gate)
        {
            c = _catalog;
            foreach (var ch in c.Channels.Values)
            {
                IEnumerable<string> values = kind switch
                {
                    SortFields.Category => ch.Categories,
                    SortFields.Language => ch.Languages,
                    _ => ch.CountryCode is null ? [] : [ch.CountryCode],
                };
                foreach (var v in values) counts[v] = counts.GetValueOrDefault(v) + 1;
            }
            return counts.Select(kv => new FacetItem(kv.Key, kind switch
                {
                    SortFields.Category => c.CategoryName(kv.Key),
                    SortFields.Language => c.LanguageName(kv.Key),
                    _ => $"{c.CountryName(kv.Key)} ({kv.Key})",
                }, kv.Value))
                .OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public ChannelDetail? Get(string id)
    {
        lock (_gate)
        {
            if (!_catalog.Channels.TryGetValue(id, out var ch)) return null;
            var streams = ch.OrderedStreams().Select((s, i) => new StreamView(
                i, s.Url, s.Title, s.Quality, s.Labels.ToList(), s.Status, s.LastChecked, s.LastStatus,
                s.Referrer, s.UserAgent, s.IsHttp)).ToList();
            return new ChannelDetail(ch.Id, ch.Name, ch.LogoUrl, ch.Website,
                string.Join(", ", ch.Categories.Select(_catalog.CategoryName)),
                string.Join(", ", ch.Languages.Select(_catalog.LanguageName)),
                _catalog.CountryName(ch.CountryCode), ch.CountryCode ?? "", ch.Status, streams);
        }
    }

    public StatusCounts Totals()
    {
        lock (_gate)
        {
            int a = 0, i = 0, u = 0;
            foreach (var ch in _catalog.Channels.Values)
            {
                switch (ch.Status)
                {
                    case Availability.Active: a++; break;
                    case Availability.Inactive: i++; break;
                    default: u++; break;
                }
            }
            return new StatusCounts(_catalog.Channels.Count, a, i, u);
        }
    }

    public (int Streams, int Active) StreamTotals()
    {
        lock (_gate)
        {
            int n = 0, a = 0;
            foreach (var ch in _catalog.Channels.Values)
                foreach (var s in ch.Streams) { n++; if (s.Status == Availability.Active) a++; }
            return (n, a);
        }
    }

    /// <summary>Extended M3U of the given channels (best stream each, or all), with header lines for VLC-style players.</summary>
    public string ExportM3u(IEnumerable<string> channelIds, bool allStreams, bool activeOnly)
    {
        static string Attr(string? v) => (v ?? "").Replace('"', '\'').Replace('\n', ' ').Trim();
        var sb = new StringBuilder("#EXTM3U\n");
        lock (_gate)
        {
            foreach (var id in channelIds)
            {
                if (!_catalog.Channels.TryGetValue(id, out var ch)) continue;
                var streams = ch.OrderedStreams().Where(s => !activeOnly || s.Status == Availability.Active).ToList();
                if (!allStreams) streams = streams.Take(1).ToList();
                var group = string.Join(";", ch.Categories.Select(_catalog.CategoryName));
                foreach (var s in streams)
                {
                    var title = ch.Name + (string.IsNullOrEmpty(s.Quality) ? "" : $" ({s.Quality})");
                    sb.Append($"#EXTINF:-1 tvg-id=\"{Attr(ch.Id)}\" tvg-name=\"{Attr(ch.Name)}\" tvg-logo=\"{Attr(ch.LogoUrl)}\" ")
                      .Append($"tvg-country=\"{Attr(ch.CountryCode)}\" tvg-language=\"{Attr(string.Join(";", ch.Languages.Select(_catalog.LanguageName)))}\" ")
                      .Append($"group-title=\"{Attr(group.Length == 0 ? "Undefined" : group)}\",{title}\n");
                    if (!string.IsNullOrEmpty(s.Referrer)) sb.Append($"#EXTVLCOPT:http-referrer={s.Referrer}\n");
                    if (!string.IsNullOrEmpty(s.UserAgent)) sb.Append($"#EXTVLCOPT:http-user-agent={s.UserAgent}\n");
                    sb.Append(s.Url).Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>CSV export: one row per stream.</summary>
    public string ExportCsv(IEnumerable<string> channelIds)
    {
        static string F(string? v) => v is null ? "" : v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder("channel_id,name,categories,languages,country_code,country_name,logo_url,stream_url,stream_status,last_checked,last_status\n");
        lock (_gate)
        {
            foreach (var id in channelIds)
            {
                if (!_catalog.Channels.TryGetValue(id, out var ch)) continue;
                foreach (var s in ch.OrderedStreams())
                    sb.AppendJoin(',', F(ch.Id), F(ch.Name), F(string.Join(";", ch.Categories.Select(_catalog.CategoryName))),
                            F(string.Join(";", ch.Languages.Select(_catalog.LanguageName))), F(ch.CountryCode),
                            F(_catalog.CountryName(ch.CountryCode)), F(ch.LogoUrl), F(s.Url), F(s.Status.ToString()),
                            F(s.LastChecked?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), F(s.LastStatus))
                      .Append('\n');
            }
        }
        return sb.ToString();
    }
}
