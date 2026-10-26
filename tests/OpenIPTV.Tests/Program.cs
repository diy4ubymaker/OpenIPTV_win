// Minimal test runner (no NuGet test framework needed).
//   dotnet run --project tests/OpenIPTV.Tests            -> offline unit tests
//   dotnet run --project tests/OpenIPTV.Tests -- --live  -> also syncs a few real iptv-org sources
using System.Net;
using System.Reflection;
using System.Text;
using OpenIPTV.Core.Checking;
using OpenIPTV.Core.Models;
using OpenIPTV.Core.Services;
using OpenIPTV.Core.Sources;
using OpenIPTV.Core.Storage;
using OpenIPTV.Tests;

var live = args.Contains("--live");
var tests = typeof(CoreTests).GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Concat(typeof(ViewModelTests).GetMethods(BindingFlags.Public | BindingFlags.Static))
    .Concat(typeof(ViewModelRefreshTests).GetMethods(BindingFlags.Public | BindingFlags.Static))
    .Concat(typeof(CheckerRobustnessTests).GetMethods(BindingFlags.Public | BindingFlags.Static))
    .Concat(typeof(ThemeTests).GetMethods(BindingFlags.Public | BindingFlags.Static))
    .Concat(typeof(PagingTests).GetMethods(BindingFlags.Public | BindingFlags.Static))
    .Where(m => m.Name.StartsWith("Test", StringComparison.Ordinal))
    .Where(m => live || !m.Name.StartsWith("TestLive", StringComparison.Ordinal))
    .ToList();

int passed = 0, failed = 0;
foreach (var t in tests)
{
    try
    {
        var r = t.Invoke(null, null);
        if (r is Task task) await task;
        Console.WriteLine($"  PASS  {t.DeclaringType!.Name}.{t.Name}");
        passed++;
    }
    catch (Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
        Console.WriteLine($"  FAIL  {t.DeclaringType!.Name}.{t.Name}: {inner.GetType().Name}: {inner.Message}");
        failed++;
    }
}
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

namespace OpenIPTV.Tests
{
    public sealed class AssertException(string message) : Exception(message);

    public static class Assert
    {
        public static void True(bool cond, string what = "condition") { if (!cond) throw new AssertException($"expected true: {what}"); }
        public static void Equal<T>(T expected, T actual, string what = "value")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertException($"{what}: expected <{expected}> but was <{actual}>");
        }
    }

    /// <summary>Tiny local HTTP server for checker tests (HLS master/variant/segment, 404, HTML).</summary>
    public sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Base { get; }

        public TestServer()
        {
            var port = System.Net.Sockets.TcpListener.Create(0) is var l ? Port(l) : 0;
            Base = $"http://localhost:{port}/";   // "localhost" needs no URL reservation on Windows
            _listener.Prefixes.Add(Base);
            _listener.Start();
            _ = Task.Run(Serve);
        }

        private static int Port(System.Net.Sockets.TcpListener l) { l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                (int code, string type, byte[] body) = path switch
                {
                    "/master.m3u8" => (200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nlow/index.m3u8\n")),
                    "/low/index.m3u8" => (200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:2,\nseg0.ts\n")),
                    "/low/seg0.ts" => (200, "video/mp2t", new byte[] { 0x47, 1, 2, 3 }),
                    "/needs-referer.m3u8" => ctx.Request.Headers["Referer"] == "https://ok.example/"
                        ? (200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:2,\nlow/seg0.ts\n"))
                        : (403, "text/plain", Encoding.UTF8.GetBytes("no")),
                    "/page" => (200, "text/html", Encoding.UTF8.GetBytes("<html></html>")),
                    "/broken.m3u8" => (200, "application/vnd.apple.mpegurl", Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:2,\nmissing.ts\n")),
                    _ => (404, "text/plain", Encoding.UTF8.GetBytes("not found")),
                };
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = type;
                try { await ctx.Response.OutputStream.WriteAsync(body); ctx.Response.Close(); } catch { }
            }
        }

        public void Dispose() { _listener.Stop(); _listener.Close(); }
    }

    public static class CoreTests
    {
        private const string M3u = """
            #EXTM3U
            #EXTINF:-1 tvg-id="BBCNews.uk@SD" tvg-logo="https://x/logo.png" group-title="News",BBC News, World (720p) [Geo-blocked]
            #EXTVLCOPT:http-referrer=https://ref.example/
            #EXTVLCOPT:http-user-agent=TestUA
            https://cdn.example/bbc/index.m3u8
            #EXTINF:-1,Local Channel
            relative/stream.m3u8
            """;

        public static void TestM3uParser()
        {
            var e = M3uParser.Parse(M3u, "https://host/list/pl.m3u");
            Assert.Equal(2, e.Count, "entries");
            Assert.Equal("BBC News, World", e[0].Name, "name");
            Assert.Equal("720p", e[0].Quality, "quality");
            Assert.True(e[0].Labels.Contains("Geo-blocked"), "label");
            Assert.Equal("https://ref.example/", e[0].Referrer, "referrer");
            Assert.Equal("TestUA", e[0].UserAgent, "ua");
            Assert.Equal("https://host/list/relative/stream.m3u8", e[1].Url, "relative url");
            Assert.Equal("BBCNews.uk", Ingest.ChannelIdOf(e[0]), "id without feed");
            Assert.Equal("custom:local-channel", Ingest.ChannelIdOf(e[1]), "custom id");
        }

        public static void TestUrlsAndPlaylistsMd()
        {
            Assert.Equal("https://raw.githubusercontent.com/iptv-org/database/master/data",
                UrlUtil.Normalize("https://github.com/iptv-org/database/tree/master/data"), "tree");
            Assert.Equal("https://raw.githubusercontent.com/iptv-org/iptv/master/PLAYLISTS.md",
                UrlUtil.Normalize("https://github.com/iptv-org/iptv/blob/master/PLAYLISTS.md"), "blob");
            Assert.Equal(("iptv-org", "iptv"), UrlUtil.ParseGithubRepo("https://github.com/iptv-org/iptv.git"), "repo");
            var md = "<code>https://iptv-org.github.io/iptv/categories/news.m3u</code>\n" +
                     "- 🇦🇫 Afghanistan <code>https://iptv-org.github.io/iptv/countries/af.m3u</code>\n" +
                     "  - Fier <code>https://iptv-org.github.io/iptv/subdivisions/al-04.m3u</code>\n";
            var links = PlaylistsMdParser.Parse(md);
            Assert.Equal(2, links.Count, "links");
            Assert.Equal(("countries", "af"), (links[1].Group, links[1].Key), "country link");
        }

        public static void TestCsv()
        {
            var rows = Csv.ReadRecords("id,name,alt\r\na,\"Hello, \"\"World\"\"\",\nb,\"multi\nline\",x\n");
            Assert.Equal(2, rows.Count, "rows");
            Assert.Equal("Hello, \"World\"", rows[0]["name"], "quoted");
            Assert.Equal(null, rows[0]["alt"], "empty → null");
            Assert.Equal("multi\nline", rows[1]["name"], "newline in quotes");
        }

        public static Harvest SampleHarvest()
        {
            var h = new Harvest();
            Ingest.Reference(h, "csv", 30, new()
            {
                ["countries"] = [new() { ["code"] = "UK", ["name"] = "United Kingdom" }, new() { ["code"] = "SG", ["name"] = "Singapore" }],
                ["languages"] = [new() { ["code"] = "eng", ["name"] = "English" }],
                ["categories"] = [new() { ["id"] = "news", ["name"] = "News" }],
                ["channels"] =
                [
                    new() { ["id"] = "BBCNews.uk", ["name"] = "BBC News", ["country"] = "UK", ["categories"] = "news", ["is_nsfw"] = "FALSE" },
                    new() { ["id"] = "Blocked.us", ["name"] = "Blocked", ["country"] = "US" },
                    new() { ["id"] = "Adult.us", ["name"] = "Adult", ["country"] = "US", ["is_nsfw"] = "TRUE" },
                ],
                ["feeds"] = [new() { ["channel"] = "BBCNews.uk", ["id"] = "SD", ["is_main"] = "TRUE", ["languages"] = "eng" }],
                ["logos"] = [new() { ["channel"] = "BBCNews.uk", ["feed"] = null, ["in_use"] = "TRUE", ["width"] = "500", ["url"] = "https://l/a.png" }],
                ["blocklist"] = [new() { ["channel"] = "Blocked.us" }],
            });
            Ingest.M3u(h, M3uParser.Parse(M3u, "https://host/pl.m3u"), "m3u", 20);
            Ingest.M3u(h, M3uParser.Parse("#EXTM3U\n#EXTINF:-1 tvg-id=\"Blocked.us\",B\nhttps://b/x.m3u8\n#EXTINF:-1 tvg-id=\"Adult.us\",A\nhttps://b/a.m3u8\n"), "m3u", 20);
            h.FinalizeData();
            return h;
        }

        public static void TestMergePriorityBlocklistNsfw()
        {
            var h = SampleHarvest();
            var ch = h.Channels["BBCNews.uk"];
            Assert.Equal("BBC News", ch.Name.Value, "CSV name beats M3U name");
            Assert.Equal("https://l/a.png", ch.Logo.Value, "reference logo beats tvg-logo");
            Assert.True(ch.Languages.SetEquals(["eng"]), "languages from main feed");
            var m = CatalogMerger.Merge(new Catalog(), h, new AppSettings(), prune: true, DateTime.Now);
            Assert.Equal(1, m.SkippedBlocked, "blocked");
            Assert.Equal(1, m.SkippedNsfw, "nsfw");
            Assert.True(m.Catalog.Channels.ContainsKey("BBCNews.uk") && m.Catalog.Channels.ContainsKey("custom:local-channel"), "kept");
            Assert.Equal(2, m.Catalog.Channels.Count, "channel count");
            var s = m.Catalog.Channels["BBCNews.uk"].Streams.Single();
            Assert.Equal("https://ref.example/", s.Referrer, "referrer kept");
        }

        public static void TestMergeKeepsStatusAndPrunes()
        {
            var first = CatalogMerger.Merge(new Catalog(), SampleHarvest(), new AppSettings(), true, DateTime.Now).Catalog;
            first.Channels["BBCNews.uk"].Streams[0].Status = Availability.Active;
            // Second harvest without the custom channel.
            var h2 = SampleHarvest();
            foreach (var k in h2.Streams.Keys.Where(k => k.ChannelId.StartsWith("custom:")).ToList()) h2.Streams.Remove(k);
            var partial = CatalogMerger.Merge(first, h2, new AppSettings(), prune: false, DateTime.Now).Catalog;
            Assert.True(partial.Channels.ContainsKey("custom:local-channel"), "kept when a source failed");
            var full = CatalogMerger.Merge(first, h2, new AppSettings(), prune: true, DateTime.Now).Catalog;
            Assert.True(!full.Channels.ContainsKey("custom:local-channel"), "removed when all sources succeeded");
            Assert.Equal(Availability.Active, full.Channels["BBCNews.uk"].Status, "status preserved");
        }

        private static CatalogService ServiceWith(Catalog c)
        {
            var svc = new CatalogService(new JsonStore(Path.Combine(Path.GetTempPath(), "openiptv-test-" + Guid.NewGuid())));
            svc.Replace(c);
            return svc;
        }

        public static void TestSearchCountsSortAndExport()
        {
            var cat = CatalogMerger.Merge(new Catalog(), SampleHarvest(), new AppSettings(), true, DateTime.Now).Catalog;
            cat.Channels["BBCNews.uk"].Streams[0].Status = Availability.Active;
            var svc = ServiceWith(cat);
            var (rows, counts) = svc.Search(new ChannelQuery { Status = StatusFilter.Active });
            Assert.Equal(1, rows.Count, "active rows");
            Assert.Equal(new StatusCounts(2, 1, 0, 1), counts, "counts ignore status filter");
            var (byCountry, _) = svc.Search(new ChannelQuery { Status = StatusFilter.All, Countries = ["uk"], Languages = ["ENG"] });
            Assert.Equal("United Kingdom", byCountry.Single().Country, "country filter case-insensitive");
            var (all, _) = svc.Search(new ChannelQuery { Status = StatusFilter.All, Sort = [SortFields.Name], Descending = true });
            Assert.Equal("Local Channel", all[0].Name, "descending name");
            var (byStatus, _) = svc.Search(new ChannelQuery { Status = StatusFilter.All, Text = "bbc" });
            Assert.Equal("BBC News", byStatus.Single().Name, "text search");
            Assert.True(svc.Facet(SortFields.Country).Any(f => f.Value == "UK" && f.Count == 1), "facet");
            var m3u = svc.ExportM3u(["BBCNews.uk"], allStreams: false, activeOnly: true);
            Assert.True(m3u.Contains("tvg-id=\"BBCNews.uk\"") && m3u.Contains("#EXTVLCOPT:http-referrer=https://ref.example/"), "m3u");
            Assert.True(svc.ExportCsv(["BBCNews.uk"]).Contains("United Kingdom"), "csv");
        }

        public static void TestStoreRoundTrip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "openiptv-test-" + Guid.NewGuid());
            var store = new JsonStore(dir);
            var cat = CatalogMerger.Merge(new Catalog(), SampleHarvest(), new AppSettings(), true, DateTime.Now).Catalog;
            cat.Channels["BBCNews.uk"].Streams[0].Status = Availability.Inactive;
            store.SaveCatalog(cat);
            var back = store.LoadCatalog();
            Assert.Equal(Availability.Inactive, back.Channels["BBCNews.uk"].Streams[0].Status, "status round trip");
            Assert.Equal("United Kingdom", back.CountryName("uk"), "case-insensitive lookups after load");
            store.SaveSources(SourceConfig.Defaults());
            Assert.Equal("categories,languages,countries", store.LoadSources()[3].Option("FOLLOW", ""), "options");
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{broken");
            Assert.Equal(60, store.LoadSettings().SyncIntervalMinutes, "corrupt settings → defaults");
        }

        public static async Task TestCheckerAgainstLocalServer()
        {
            using var srv = new TestServer();
            using var checker = new StreamChecker(5);
            async Task<CheckResult> C(string path, string? referrer = null) =>
                await checker.CheckAsync(new StreamInfo { Url = srv.Base + path, Referrer = referrer });
            Assert.Equal(new CheckResult(true, "ok_hls"), await C("master.m3u8"), "master → variant → segment");
            Assert.Equal(new CheckResult(false, "http_404"), await C("nope.m3u8"), "404");
            Assert.Equal(new CheckResult(false, "html_page"), await C("page"), "html");
            Assert.Equal(new CheckResult(false, "segment_http_404"), await C("broken.m3u8"), "missing segment");
            Assert.Equal(new CheckResult(false, "http_403"), await C("needs-referer.m3u8"), "no referer");
            Assert.Equal(new CheckResult(true, "ok_hls"), await C("needs-referer.m3u8", "https://ok.example/"), "with referer");
            Assert.Equal("connect_error", (await checker.CheckAsync(new StreamInfo { Url = "http://127.0.0.1:9/x.m3u8" })).Status, "refused");
            Assert.Equal("untestable_protocol", (await checker.CheckAsync(new StreamInfo { Url = "udp://@239.0.0.1:1234" })).Status, "udp");
        }

        public static async Task TestAgentCycleWithLocalSource()
        {
            using var srv = new TestServer();
            var dir = Path.Combine(Path.GetTempPath(), "openiptv-test-" + Guid.NewGuid());
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(dir).FullName, "pl.m3u"), "");
            var store = new JsonStore(dir);
            var catalog = new CatalogService(store);
            // Serve the playlist from a data: like local file through a custom source → use the test server paths.
            var m3u = $"#EXTM3U\n#EXTINF:-1 tvg-id=\"Good.sg\" group-title=\"News\",Good (720p)\n{srv.Base}master.m3u8\n" +
                      $"#EXTINF:-1 tvg-id=\"Bad.sg\",Bad\n{srv.Base}nope.m3u8\n";
            using var playlistServer = new PlaylistServer(m3u);
            var settings = new AppSettings { CheckConcurrency = 4, CheckTimeoutSeconds = 5 };
            var sources = new List<SourceConfig> { new() { Name = "local", Kind = SourceKinds.M3u, Url = playlistServer.Url } };
            using var agent = new SyncAgent(store, catalog, () => settings, () => sources, s => store.SaveSources(s));
            var run = await agent.RunCycleAsync("test", CancellationToken.None);
            Assert.Equal("ok", run!.Status, "run status");
            Assert.Equal(2, run.Channels, "channels");
            Assert.Equal(1, run.ActiveStreams, "active");
            var (rows, counts) = catalog.Search(new ChannelQuery());
            Assert.Equal("Good", rows.Single().Name, "default Active filter");
            Assert.Equal("SG", rows.Single().CountryCode, "country from id suffix");
            Assert.Equal(new StatusCounts(2, 1, 1, 0), counts, "counts");
            var (ok, msg) = await agent.RecheckChannelAsync("Good.sg");
            Assert.True(ok, msg);
            var (ok2, msg2) = await agent.RecheckChannelAsync("Good.sg");
            Assert.True(!ok2 && msg2.Contains("try again"), "cooldown");
            Assert.True(File.Exists(Path.Combine(dir, "catalog.json")) && File.Exists(Path.Combine(dir, "history.json")), "persisted");
            Assert.True(sources[0].LastStatus!.StartsWith("ok"), "source status recorded");
        }

        public static async Task TestLiveSync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "openiptv-live");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var store = new JsonStore(dir);
            var catalog = new CatalogService(store);
            var settings = new AppSettings { CheckStreamsOnSync = true, MaxChecksPerCycle = 60 };
            var sources = new List<SourceConfig>
            {
                new() { Name = "csv", Kind = SourceKinds.DatabaseCsv, Url = "https://github.com/iptv-org/database/tree/master/data" },
                new() { Name = "sg", Kind = SourceKinds.M3u, Url = "https://github.com/iptv-org/iptv/blob/master/streams/sg.m3u" },
                new() { Name = "uk", Kind = SourceKinds.M3u, Url = "https://raw.githubusercontent.com/iptv-org/iptv/master/streams/uk.m3u" },
            };
            using var agent = new SyncAgent(store, catalog, () => settings, () => sources, s => store.SaveSources(s));
            agent.Log += l => Console.WriteLine("        " + l);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var run = await agent.RunCycleAsync("live-test", CancellationToken.None);
            Console.WriteLine($"        cycle {sw.Elapsed.TotalSeconds:N1}s status={run!.Status} channels={run.Channels} streams={run.Streams} checked={run.Checked} active={run.ActiveStreams}");
            Assert.True(run.Channels > 100, "channels harvested");
            var (rows, counts) = catalog.Search(new ChannelQuery { Status = StatusFilter.All, Countries = ["SG"] });
            Console.WriteLine($"        SG: {counts} e.g. {string.Join(" | ", rows.Take(4).Select(r => $"{r.Name} [{r.Categories}] [{r.Languages}] {r.Country}"))}");
            Assert.True(rows.Count > 0 && rows.All(r => r.Country == "Singapore"), "SG channels with country name");
            Assert.True(rows.Any(r => r.LogoUrl is not null), "logos");
            sw.Restart();
            var reloaded = new JsonStore(dir).LoadCatalog();
            Console.WriteLine($"        reload catalog.json ({new FileInfo(Path.Combine(dir, "catalog.json")).Length / 1024} KB) in {sw.ElapsedMilliseconds} ms");
            Assert.Equal(catalog.Snapshot().Channels.Count, reloaded.Channels.Count, "reload");
        }
    }

    /// <summary>Serves one playlist text at /pl.m3u.</summary>
    public sealed class PlaylistServer : IDisposable
    {
        private readonly HttpListener _l = new();
        public string Url { get; }
        public PlaylistServer(string body)
        {
            var t = System.Net.Sockets.TcpListener.Create(0); t.Start(); var port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            Url = $"http://localhost:{port}/pl.m3u";
            _l.Prefixes.Add($"http://localhost:{port}/");
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (_l.IsListening)
                {
                    try
                    {
                        var ctx = await _l.GetContextAsync();
                        var b = Encoding.UTF8.GetBytes(body);
                        await ctx.Response.OutputStream.WriteAsync(b);
                        ctx.Response.Close();
                    }
                    catch { return; }
                }
            });
        }
        public void Dispose() { _l.Stop(); _l.Close(); }
    }
}
