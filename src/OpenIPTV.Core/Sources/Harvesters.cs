using System.Net.Http.Headers;
using System.Text.Json;
using OpenIPTV.Core.Models;

namespace OpenIPTV.Core.Sources;

/// <summary>Downloads every enabled source and fills one <see cref="Harvest"/>.</summary>
public sealed class Harvester(HttpClient http, string? githubToken = null)
{
    private static readonly string[] ReferenceFiles =
        ["countries", "languages", "categories", "blocklist", "feeds", "logos", "channels"];

    public event Action<string>? Log;

    public async Task<Harvest> HarvestAllAsync(IEnumerable<SourceConfig> sources, CancellationToken ct)
    {
        var h = new Harvest();
        foreach (var src in sources.OrderBy(s => SourceKinds.Order(s.Kind)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Log?.Invoke($"Harvesting {src.Name} ({src.Kind})");
                var stats = src.Kind switch
                {
                    SourceKinds.IptvApi => await IptvApiAsync(src, h, ct),
                    SourceKinds.DatabaseCsv => await DatabaseCsvAsync(src, h, ct),
                    SourceKinds.M3u => await M3uAsync(src, h, ct),
                    SourceKinds.PlaylistsMd => await PlaylistsMdAsync(src, h, ct),
                    SourceKinds.GithubRepo => await GithubRepoAsync(src, h, ct),
                    _ => throw new InvalidOperationException($"unknown source kind '{src.Kind}'"),
                };
                h.Stats[src.Name] = stats;
                Log?.Invoke($"  {src.Name}: {stats}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var msg = $"{ex.GetType().Name}: {ex.Message}";
                h.Errors[src.Name] = msg.Length > 500 ? msg[..500] : msg;
                Log?.Invoke($"  {src.Name} FAILED: {msg}");
            }
        }
        h.FinalizeData();
        return h;
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct, HttpRequestMessage? custom = null)
    {
        using var req = custom ?? new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ------------------------------------------------------------ reference data (API JSON / DB CSV share one schema)
    private async Task<string> IptvApiAsync(SourceConfig src, Harvest h, CancellationToken ct)
    {
        var baseUrl = UrlUtil.Normalize(src.Url);
        var files = ReferenceFiles.Append("streams").ToArray();
        var tasks = files.ToDictionary(f => f, f => GetTextAsync($"{baseUrl}/{f}.json", ct));
        await Task.WhenAll(tasks.Values);
        var data = tasks.ToDictionary(kv => kv.Key, kv => JsonRows(kv.Value.Result));
        return Ingest.Reference(h, src.Name, SourceKinds.Priority(SourceKinds.IptvApi), data);
    }

    private async Task<string> DatabaseCsvAsync(SourceConfig src, Harvest h, CancellationToken ct)
    {
        var baseUrl = UrlUtil.Normalize(src.Url);
        var tasks = ReferenceFiles.ToDictionary(f => f, f => GetTextAsync($"{baseUrl}/{f}.csv", ct));
        await Task.WhenAll(tasks.Values);
        var data = tasks.ToDictionary(kv => kv.Key, kv => Csv.ReadRecords(kv.Value.Result));
        return Ingest.Reference(h, src.Name, SourceKinds.Priority(SourceKinds.DatabaseCsv), data);
    }

    /// <summary>JSON array of objects → rows of strings (arrays joined with ';', booleans as TRUE/FALSE), like the CSV.</summary>
    public static List<Dictionary<string, string?>> JsonRows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<Dictionary<string, string?>>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.EnumerateObject())
            {
                d[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.True => "TRUE",
                    JsonValueKind.False => "FALSE",
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.Array => string.Join(";", p.Value.EnumerateArray()
                        .Where(x => x.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
                    _ => null,
                };
            }
            rows.Add(d);
        }
        return rows;
    }

    // ------------------------------------------------------------ playlists
    private async Task<string> M3uAsync(SourceConfig src, Harvest h, CancellationToken ct)
    {
        var url = UrlUtil.Normalize(src.Url);
        var text = await GetTextAsync(url, ct);
        int n = Ingest.M3u(h, M3uParser.Parse(text, url), src.Name, SourceKinds.Priority(SourceKinds.M3u));
        return $"streams={n}";
    }

    private async Task<string> PlaylistsMdAsync(SourceConfig src, Harvest h, CancellationToken ct)
    {
        var text = await GetTextAsync(UrlUtil.Normalize(src.Url), ct);
        var follow = src.Option("follow", "categories,languages,countries")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var links = PlaylistsMdParser.Parse(text).Where(l => follow.Contains(l.Group)).ToList();
        int concurrency = int.TryParse(src.Option("concurrency", "12"), out var c) ? Math.Clamp(c, 1, 64) : 12;
        var results = await FetchManyAsync(links.Select(l => l.Url).ToList(), concurrency, ct);

        int ok = 0, failed = 0, streams = 0;
        var prio = SourceKinds.Priority(SourceKinds.PlaylistsMd);
        foreach (var (group, key, url) in links)
        {
            if (!results.TryGetValue(url, out var body) || body is null) { failed++; continue; }
            ok++;
            var hint = group switch
            {
                "categories" => new Ingest.Hint(Category: key),
                "languages" => new Ingest.Hint(Language: key),
                _ => new Ingest.Hint(Country: key.ToUpperInvariant()),
            };
            streams += Ingest.M3u(h, M3uParser.Parse(body, url), src.Name, prio, hint);
        }
        if (links.Count > 0 && ok == 0) throw new InvalidOperationException($"all {failed} linked playlists failed");
        return $"playlists={links.Count} failed={failed} streams={streams}";
    }

    private async Task<string> GithubRepoAsync(SourceConfig src, Harvest h, CancellationToken ct)
    {
        var (owner, repo) = UrlUtil.ParseGithubRepo(src.Url);
        var branch = src.Option("branch", "master");
        var path = src.Option("path", "streams").Trim('/');
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={Uri.EscapeDataString(branch)}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrWhiteSpace(githubToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken.Trim());
        var listing = await GetTextAsync(req.RequestUri!.ToString(), ct, req);

        var files = new List<(string Name, string Url)>();
        using (var doc = JsonDocument.Parse(listing))
        {
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                if (f.GetProperty("type").GetString() != "file") continue;
                var name = f.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) continue;
                var dl = f.TryGetProperty("download_url", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()!
                    : $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}/{name}";
                files.Add((name, dl));
            }
        }
        var results = await FetchManyAsync(files.Select(f => f.Url).ToList(), 12, ct);
        int failed = 0, streams = 0;
        var prio = SourceKinds.Priority(SourceKinds.GithubRepo);
        foreach (var (name, url) in files)
        {
            if (!results.TryGetValue(url, out var body) || body is null) { failed++; continue; }
            var entries = M3uParser.Parse(body, url);
            streams += Ingest.M3u(h, entries, src.Name, prio);
            var stem = Path.GetFileNameWithoutExtension(name).Split('_')[0];
            if (stem.Length == 2)                                   // file name hints the country, e.g. us_pluto.m3u
                foreach (var e in entries) h.Channel(Ingest.ChannelIdOf(e)).Country.Offer(stem.ToUpperInvariant(), 1);
        }
        if (files.Count > 0 && failed == files.Count) throw new InvalidOperationException("all repository playlists failed");
        return $"files={files.Count} failed={failed} streams={streams}";
    }

    private async Task<Dictionary<string, string?>> FetchManyAsync(List<string> urls, int concurrency, CancellationToken ct)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
        await Parallel.ForEachAsync(urls.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (url, token) =>
            {
                try { result[url] = await GetTextAsync(url, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { result[url] = null; }
            });
        return new Dictionary<string, string?>(result);
    }
}

/// <summary>Turns parsed rows / playlist entries into harvest records.</summary>
public static class Ingest
{
    public sealed record Hint(string? Category = null, string? Language = null, string? Country = null);

    private static List<string> Split(string? v) =>
        string.IsNullOrWhiteSpace(v) ? [] : v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool Bool(string? v) => v is not null && (v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || v == "1");

    private static long Long(string? v) => double.TryParse(v, System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)d : 0;

    private static string? Get(Dictionary<string, string?> r, string key) => r.TryGetValue(key, out var v) ? v : null;

    public static string Reference(Harvest h, string origin, int prio, Dictionary<string, List<Dictionary<string, string?>>> data)
    {
        List<Dictionary<string, string?>> Rows(string name) => data.TryGetValue(name, out var r) ? r : [];

        foreach (var r in Rows("countries"))
            if (Get(r, "code") is { } code) h.Countries.TryAdd(code.ToUpperInvariant(), Get(r, "name") ?? code);
        foreach (var r in Rows("languages"))
            if (Get(r, "code") is { } code) h.Languages.TryAdd(code, Get(r, "name") ?? code);
        foreach (var r in Rows("categories"))
            if (Get(r, "id") is { } id) h.Categories.TryAdd(id, Get(r, "name") ?? id);
        foreach (var r in Rows("blocklist"))
            if (Get(r, "channel") is { } ch) h.Blocklist.Add(ch);

        foreach (var r in Rows("feeds"))
        {
            var cid = Get(r, "channel");
            var langs = Split(Get(r, "languages"));
            if (cid is null || langs.Count == 0) continue;
            if (Get(r, "id") is { } fid) h.FeedLanguages.TryAdd((cid, fid), langs);
            if (Bool(Get(r, "is_main")) || !h.MainFeedLanguages.ContainsKey(cid)) h.MainFeedLanguages[cid] = langs;
        }
        foreach (var r in Rows("logos"))
        {
            var cid = Get(r, "channel");
            var url = Get(r, "url");
            if (cid is null || url is null) continue;
            var inUse = Get(r, "in_use") is not { } iu || Bool(iu);
            long score = ((string.IsNullOrEmpty(Get(r, "feed")) ? 4 : 0) + (inUse ? 2 : 0)) * 100_000L
                         + Math.Min(Long(Get(r, "width")), 99_999);
            if (!h.Logos.TryGetValue(cid, out var cur) || score > cur.Score) h.Logos[cid] = (score, url);
        }

        int channels = 0, streams = 0;
        foreach (var r in Rows("channels"))
        {
            if (Get(r, "id") is not { } id) continue;
            var ch = h.Channel(id);
            ch.Name.Offer(Get(r, "name"), prio);
            ch.Country.Offer(Get(r, "country")?.ToUpperInvariant(), prio);
            ch.Website.Offer(Get(r, "website"), prio);
            if (Bool(Get(r, "is_nsfw"))) ch.IsNsfw = true;
            ch.AltNames.UnionWith(Split(Get(r, "alt_names")));
            ch.Categories.UnionWith(Split(Get(r, "categories")));
            ch.Origins.Add(origin);
            channels++;
        }
        foreach (var r in Rows("streams"))
        {
            var cid = Get(r, "channel");
            var url = Get(r, "url")?.Trim();
            if (cid is null || string.IsNullOrEmpty(url)) continue;
            h.Channel(cid).Origins.Add(origin);
            var s = new StreamRec
            {
                ChannelId = cid, Url = url, Title = Get(r, "title"), Feed = Get(r, "feed"), Quality = Get(r, "quality"),
                Referrer = Get(r, "referrer"), UserAgent = Get(r, "user_agent"),
            };
            s.Labels.UnionWith(Split(Get(r, "labels")));
            s.Origins.Add(origin);
            h.AddStream(s);
            streams++;
        }
        return $"channels={channels} streams={streams}";
    }

    /// <summary>iptv-org id from tvg-id (feed suffix "@SD" removed), or a stable custom id from the name.</summary>
    public static string ChannelIdOf(M3uEntry e)
    {
        var tvg = e.Attr("tvg-id").Trim();
        if (tvg.Length > 0) return tvg.Split('@')[0];
        return "custom:" + UrlUtil.Slug(e.Name.Length > 0 ? e.Name : e.Url);
    }

    public static int M3u(Harvest h, List<M3uEntry> entries, string origin, int prio, Hint? hint = null)
    {
        var catByName = h.Categories.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Url)) continue;
            var tvg = e.Attr("tvg-id").Trim();
            var cid = ChannelIdOf(e);
            string? feed = tvg.Contains('@') ? tvg[(tvg.IndexOf('@') + 1)..] : null;
            var ch = h.Channel(cid);
            ch.Name.Offer(e.Name, prio);
            ch.Logo.Offer(e.Attr("tvg-logo"), prio - 5);            // API/CSV logos outrank tvg-logo
            var dot = cid.LastIndexOf('.');
            if (tvg.Length > 0 && dot > 0 && cid.Length - dot - 1 == 2)
                ch.Country.Offer(cid[(dot + 1)..].ToUpperInvariant(), prio - 5);  // ids end with the country: BBCNews.uk
            foreach (var g in e.Attr("group-title").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!g.Equals("undefined", StringComparison.OrdinalIgnoreCase))
                    ch.Categories.Add(catByName.TryGetValue(g, out var id) ? id : UrlUtil.Slug(g));
            if (hint?.Category is { } hc && hc != "undefined") ch.Categories.Add(hc);
            if (hint?.Language is { } hl && hl != "undefined") ch.Languages.Add(hl);
            if (hint?.Country is { } hco) ch.Country.Offer(hco, prio);
            ch.Origins.Add(origin);
            var s = new StreamRec
            {
                ChannelId = cid, Url = e.Url.Trim(), Title = e.Title, Feed = feed, Quality = e.Quality,
                Referrer = e.Referrer, UserAgent = e.UserAgent,
            };
            s.Labels.UnionWith(e.Labels);
            s.Origins.Add(origin);
            h.AddStream(s);
            n++;
        }
        return n;
    }
}
