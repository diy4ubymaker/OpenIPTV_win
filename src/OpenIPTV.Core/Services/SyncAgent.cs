using System.Net;
using OpenIPTV.Core.Checking;
using OpenIPTV.Core.Models;
using OpenIPTV.Core.Sources;
using OpenIPTV.Core.Storage;

namespace OpenIPTV.Core.Services;

public sealed record SyncStatus(bool Running, string Phase, string Progress, DateTime? NextRun, string? LastResult);

/// <summary>
/// Background agent: harvests all enabled sources, merges them into the catalog, tests streams,
/// and repeats every N minutes (N re-read each loop, so changes apply without restart).
/// </summary>
public sealed class SyncAgent : IDisposable
{
    private readonly JsonStore _store;
    private readonly CatalogService _catalog;
    private readonly Func<AppSettings> _settings;
    private readonly Func<List<SourceConfig>> _sources;
    private readonly Action<List<SourceConfig>> _saveSources;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DateTime> _recheckTimes = new(StringComparer.Ordinal);
    private Task? _loop;
    private string _pendingTrigger = "schedule";
    private SyncStatus _status = new(false, "", "", null, null);

    public List<SyncRun> History { get; }

    public event Action<SyncStatus>? StatusChanged;
    public event Action<SyncRun>? RunFinished;
    public event Action<string>? Log;

    public SyncAgent(JsonStore store, CatalogService catalog, Func<AppSettings> settings,
        Func<List<SourceConfig>> sources, Action<List<SourceConfig>> saveSources)
    {
        _store = store;
        _catalog = catalog;
        _settings = settings;
        _sources = sources;
        _saveSources = saveSources;
        History = store.LoadHistory();
    }

    public SyncStatus Status => _status;
    public bool IsRunning => _cycleLock.CurrentCount == 0;

    private void SetStatus(bool running, string phase, string progress, string? result = null)
    {
        _status = _status with { Running = running, Phase = phase, Progress = progress, LastResult = result ?? _status.LastResult };
        StatusChanged?.Invoke(_status);
    }

    private void Write(string line)
    {
        _store.AppendLog(line);
        Log?.Invoke(line);
    }

    public static HttpClient CreateHttpClient(bool verifyTls)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (!verifyTls) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenIPTV-Windows/1.0 (+https://github.com/iptv-org)");
        return http;
    }

    // ------------------------------------------------------------------ scheduling
    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Request an immediate cycle. Returns false if one is already running.</summary>
    public bool RunNow(string trigger = "manual")
    {
        if (IsRunning) return false;
        if (_loop is null)
        {
            _ = Task.Run(() => RunCycleAsync(trigger, _cts.Token));
            return true;
        }
        _pendingTrigger = trigger;
        if (_wake.CurrentCount == 0) _wake.Release();
        return true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var last = History.FirstOrDefault(r => r.Finished is not null);
        DateTime? lastStart = last?.Started;
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            var s = _settings();
            var interval = TimeSpan.FromMinutes(Math.Max(1, s.SyncIntervalMinutes));
            // First pass: sync shortly after start when there is no data yet or "sync on start-up" is on.
            DateTime next = lastStart is null || (first && s.SyncOnStartup)
                ? (first ? DateTime.Now.AddSeconds(2) : DateTime.Now)
                : lastStart.Value + interval;
            _status = _status with { NextRun = next };
            StatusChanged?.Invoke(_status);

            bool woke = false;
            var wait = next - DateTime.Now;
            if (wait > TimeSpan.Zero)
            {
                // Wake at least every 30 s so a changed interval takes effect without restart.
                try { woke = await _wake.WaitAsync(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { break; }
                if (!woke && DateTime.Now < next) continue;
            }
            first = false;
            var trigger = woke ? _pendingTrigger : "schedule";
            _pendingTrigger = "schedule";
            lastStart = DateTime.Now;
            await RunCycleAsync(trigger, ct);
        }
    }

    // ------------------------------------------------------------------ one cycle
    public async Task<SyncRun?> RunCycleAsync(string trigger, CancellationToken ct)
    {
        if (!await _cycleLock.WaitAsync(0, ct)) return null;
        var run = new SyncRun { Trigger = trigger, Started = DateTime.Now };
        try
        {
            var settings = _settings();
            var sources = _sources().Where(s => s.Enabled).ToList();
            SetStatus(true, "Harvesting sources", $"{sources.Count} source(s)");
            Write($"Sync started ({trigger}), {sources.Count} enabled source(s)");

            Harvest harvest;
            using (var http = CreateHttpClient(settings.VerifyTls))
            {
                var harvester = new Harvester(http, settings.GithubToken);
                harvester.Log += Write;
                harvest = await harvester.HarvestAllAsync(sources, ct);
            }
            var all = _sources();
            foreach (var src in all.Where(s => s.Enabled))
            {
                src.LastRun = DateTime.Now;
                src.LastStatus = harvest.Errors.TryGetValue(src.Name, out var e) ? "error: " + e
                    : harvest.Stats.TryGetValue(src.Name, out var st) ? "ok " + st : "ok";
            }
            _saveSources(all);
            run.SourceErrors = new(harvest.Errors);
            run.SourceStats = new(harvest.Stats);

            SetStatus(true, "Merging", $"{harvest.Channels.Count:N0} channels, {harvest.Streams.Count:N0} streams");
            bool prune = sources.Count > 0 && harvest.Errors.Count == 0;
            var merged = CatalogMerger.Merge(_catalog.Snapshot(), harvest, settings, prune, DateTime.Now);
            _catalog.Replace(merged.Catalog);
            _catalog.Save();
            run.Channels = merged.Channels;
            run.Streams = merged.Streams;
            run.Removed = merged.Removed;
            run.Pruned = prune;
            Write($"Merged: {merged.Channels:N0} channels, {merged.Streams:N0} streams, removed {merged.Removed}, " +
                  $"blocklisted {merged.SkippedBlocked}, NSFW skipped {merged.SkippedNsfw}");

            if (settings.CheckStreamsOnSync)
            {
                var (checkedCount, active) = await CheckStreamsAsync(settings, ct);
                run.Checked = checkedCount;
                run.ActiveStreams = active;
            }
            run.Status = harvest.Errors.Count > 0 ? "partial" : "ok";
        }
        catch (OperationCanceledException)
        {
            run.Status = "cancelled";
        }
        catch (Exception ex)
        {
            run.Status = "error";
            run.Error = $"{ex.GetType().Name}: {ex.Message}";
            Write("Sync failed: " + ex);
        }
        finally
        {
            run.Finished = DateTime.Now;
            _catalog.Save();
            lock (History)
            {
                History.Insert(0, run);
                if (History.Count > 100) History.RemoveRange(100, History.Count - 100);
                _store.SaveHistory(History);
            }
            Write($"Sync finished: {run.Status}");
            SetStatus(false, "", "", $"last sync {run.Finished:HH:mm}: {run.Status}");
            _cycleLock.Release();
            _catalog.NotifyChanged();
            RunFinished?.Invoke(run);
        }
        return run;
    }

    /// <summary>Copy of the sync history, newest first (safe to call from any thread).</summary>
    public List<SyncRun> HistorySnapshot()
    {
        lock (History) return [.. History];
    }

    /// <summary>Delete all sync history entries (history.json). The next-sync schedule is not affected.</summary>
    public void ClearHistory()
    {
        lock (History)
        {
            History.Clear();
            _store.SaveHistory(History);
        }
        Write("Sync history cleared");
    }

    private static void Apply(StreamInfo s, CheckResult r)
    {
        s.LastChecked = DateTime.Now;
        s.LastStatus = r.Status;
        if (r.Status == "untestable_protocol") { s.Status = Availability.Unchecked; return; }
        s.Status = r.Ok ? Availability.Active : Availability.Inactive;
        s.FailCount = r.Ok ? 0 : s.FailCount + 1;
    }

    private async Task<(int Checked, int Active)> CheckStreamsAsync(AppSettings settings, CancellationToken ct)
    {
        List<StreamInfo> items = [];
        _catalog.Update(c => items = c.Channels.Values.SelectMany(ch => ch.Streams)
            .OrderBy(s => s.LastChecked is null ? 0 : 1).ThenBy(s => s.LastChecked)
            .ToList());
        if (settings.MaxChecksPerCycle > 0) items = items.Take(settings.MaxChecksPerCycle).ToList();

        // Check against copies so the UI lock is never held during network I/O.
        var work = items.Select(s => (Live: s, Probe: new StreamInfo { Url = s.Url, Referrer = s.Referrer, UserAgent = s.UserAgent }))
            .ToList();
        int active = 0, done = 0;
        var started = DateTime.Now;
        SetStatus(true, "Checking streams", $"0/{work.Count:N0}");
        using var checker = new StreamChecker(settings.CheckTimeoutSeconds, settings.VerifyTls);
        var byProbe = work.ToDictionary(w => w.Probe, w => w.Live);  // StreamInfo uses reference equality
        var lastNotify = DateTime.Now;
        await checker.CheckManyAsync(work.Select(w => w.Probe).ToList(), settings.CheckConcurrency,
            (probe, r) =>
            {
                var live = byProbe[probe];
                _catalog.Update(_ => Apply(live, r));
                if (r.Ok) Interlocked.Increment(ref active);
                Interlocked.Increment(ref done);
            },
            new Progress<(int Done, int Total)>(p =>
            {
                try
                {
                    SetStatus(true, "Checking streams", $"{p.Done:N0}/{p.Total:N0}");
                    if ((DateTime.Now - lastNotify).TotalSeconds > 5) { lastNotify = DateTime.Now; _catalog.NotifyChanged(); }
                }
                catch (Exception ex) { Write("Progress update failed: " + ex.Message); }
            }),
            ct,
            onError: Write);
        Write($"Checked {done:N0} stream(s) in {(DateTime.Now - started).TotalSeconds:N0}s: {active:N0} active");
        return (done, active);
    }

    // ------------------------------------------------------------------ on-demand re-check
    /// <summary>Re-test every stream of one channel now (rate-limited per channel).</summary>
    public async Task<(bool Done, string Message)> RecheckChannelAsync(string channelId, CancellationToken ct = default)
    {
        var settings = _settings();
        lock (_recheckTimes)
        {
            if (_recheckTimes.TryGetValue(channelId, out var last))
            {
                var wait = TimeSpan.FromSeconds(settings.RecheckCooldownSeconds) - (DateTime.Now - last);
                if (wait > TimeSpan.Zero) return (false, $"Checked moments ago — try again in {Math.Ceiling(wait.TotalSeconds)} s.");
            }
            _recheckTimes[channelId] = DateTime.Now;
        }
        List<StreamInfo> streams = [];
        _catalog.Update(c => { if (c.Channels.TryGetValue(channelId, out var ch)) streams = ch.Streams.ToList(); });
        if (streams.Count == 0) return (false, "This channel has no streams.");

        using var checker = new StreamChecker(settings.CheckTimeoutSeconds, settings.VerifyTls);
        var results = await Task.WhenAll(streams.Select(async s =>
            (Stream: s, Result: await checker.CheckAsync(new StreamInfo { Url = s.Url, Referrer = s.Referrer, UserAgent = s.UserAgent }, ct))));
        _catalog.Update(_ => { foreach (var (s, r) in results) Apply(s, r); });
        _catalog.Save();
        _catalog.NotifyChanged();
        int ok = results.Count(r => r.Result.Ok);
        Write($"Re-checked {channelId}: {ok}/{results.Length} active");
        return (true, $"Checked {results.Length} source(s): {ok} active.");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
