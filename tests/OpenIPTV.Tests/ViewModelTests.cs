using OpenIPTV.Core.Models;
using OpenIPTV.Core.Services;
using OpenIPTV.Core.Sources;
using OpenIPTV.ViewModels;

namespace OpenIPTV.Tests;

public sealed class FakeUi : IUiServices
{
    public List<string> Infos { get; } = [];
    public List<string> Errors { get; } = [];
    public string? Copied { get; private set; }
    public string? SavePath { get; set; }
    public bool ConfirmAnswer { get; set; } = true;
    public void Post(Action action) => action();
    public void CopyText(string text) => Copied = text;
    public string? AskSaveFile(string title, string defaultFileName, string filter) => SavePath;
    public void Open(string pathOrUrl) { }
    public void ShowInfo(string message) => Infos.Add(message);
    public void ShowError(string message) => Errors.Add(message);
    public bool Confirm(string message) => ConfirmAnswer;
    public string? Theme { get; private set; }
    public void ApplyTheme(string theme) => Theme = theme;
}

public sealed class FakePlayer : IPlayer
{
    public (string Url, string? Referrer, string? UserAgent)? Last { get; private set; }
    public int Volume { get; set; }
    public bool Mute { get; set; }
    public event Action<PlayerState, string?>? StateChanged;
    public void Play(string url, string? referrer, string? userAgent, int networkCachingMs)
    {
        Last = (url, referrer, userAgent);
        StateChanged?.Invoke(PlayerState.Opening, null);
        StateChanged?.Invoke(PlayerState.Playing, null);
    }
    public void Stop() => StateChanged?.Invoke(PlayerState.Stopped, null);
    public void TogglePause() { }
    public void Fail() => StateChanged?.Invoke(PlayerState.Error, "boom");
}

public static class ViewModelTests
{
    private static (AppHost Host, MainViewModel Vm, FakeUi Ui, FakePlayer Player) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openiptv-vm-" + Guid.NewGuid());
        var host = new AppHost(dir);
        var cat = CatalogMerger.Merge(new Catalog(), CoreTests.SampleHarvest(), new AppSettings(), true, DateTime.Now).Catalog;
        cat.Channels["BBCNews.uk"].Streams[0].Status = Availability.Active;
        host.Catalog.Replace(cat);
        var ui = new FakeUi();
        var player = new FakePlayer();
        var vm = new MainViewModel(host, ui, player, debounce: TimeSpan.Zero);
        return (host, vm, ui, player);
    }

    public static void TestDefaultsCountsAndFilters()
    {
        var (_, vm, _, _) = Create();
        Assert.Equal("Active", vm.StatusFilter, "default status");
        Assert.Equal(1, vm.Rows.Count, "active rows");
        Assert.True(vm.CountsText.StartsWith("2 channels · ✅ 1 active · ❌ 0 inactive · ⏳ 1 unchecked"), vm.CountsText);
        vm.StatusFilter = "All";
        Assert.Equal(2, vm.Rows.Count, "all rows");
        vm.CountryFilter.Select("UK");
        Assert.Equal("Country: United Kingdom (UK)", vm.CountryFilter.Summary, "summary");
        Assert.Equal(1, vm.Rows.Count, "country filter");
        vm.SearchText = "nothing-matches";
        Assert.Equal(0, vm.Rows.Count, "search");
        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(2, vm.Rows.Count, "cleared");
        vm.SortDescending = true;
        Assert.Equal("Local Channel", vm.Rows[0].Name, "sort desc");
        vm.CountryFilter.Search = "king";
        Assert.Equal(1, vm.CountryFilter.Visible.Count, "filter search box");
    }

    public static void TestSelectPlayCopyAndErrors()
    {
        var (_, vm, ui, player) = Create();
        Assert.True(!vm.PlayCommand.CanExecute(null), "nothing selected");
        vm.SelectedRow = vm.Rows[0];
        Assert.Equal("BBC News", vm.Channel!.Name, "detail");
        Assert.Equal("https://cdn.example/bbc/index.m3u8", vm.SourceUrl, "source url");
        Assert.True(vm.SourceNotes.Contains("Referer: https://ref.example/"), vm.SourceNotes);
        vm.CopyUrlCommand.Execute(null);
        Assert.Equal("https://cdn.example/bbc/index.m3u8", ui.Copied, "copied");
        vm.PlayCommand.Execute(null);
        Assert.Equal(("https://cdn.example/bbc/index.m3u8", "https://ref.example/", "TestUA"), player.Last!.Value, "player got headers");
        Assert.Equal(PlayerState.Playing, vm.PlayerState, "playing");
        Assert.Equal("▶ BBC News", vm.NowPlaying, "now playing");
        player.Fail();
        Assert.True(vm.PlayerMessage.StartsWith("Playback failed"), vm.PlayerMessage);
    }

    public static void TestExportAndUiStatePersisted()
    {
        var (host, vm, ui, _) = Create();
        var path = Path.Combine(host.Store.DataDir, "out.m3u");
        ui.SavePath = path;
        vm.ExportM3uCommand.Execute(null);
        Assert.True(File.ReadAllText(path).Contains("tvg-id=\"BBCNews.uk\""), "m3u written");
        vm.StatusFilter = "Inactive";
        vm.Sort1 = SortFields.Country;
        vm.Volume = 55;
        vm.Dispose();
        var s = new AppHost(host.Store.DataDir).Settings;
        Assert.Equal(("Inactive", "Country", 55), (s.StatusFilter, s.Sort1, s.Volume), "ui state saved");
    }

    public static void TestSourcesValidationAndSave()
    {
        var (host, vm, ui, _) = Create();
        Assert.Equal(5, vm.Sources.Items.Count, "defaults");
        vm.Sources.AddCommand.Execute(null);
        vm.Sources.Selected!.Url = "ftp://bad";
        Assert.True(!vm.Sources.Save() && ui.Errors.Last().Contains("http"), "bad url rejected");
        vm.Sources.Selected!.Url = "https://example.com/my.m3u";
        vm.Sources.Selected!.Options = "oops";
        Assert.True(!vm.Sources.Save() && ui.Errors.Last().Contains("key=value"), "bad options rejected");
        vm.Sources.Selected!.Options = "";
        Assert.True(vm.Sources.Save(), "saved");
        Assert.Equal(6, host.Sources.Count, "persisted");
        Assert.Equal("categories,languages,countries", host.Sources[3].Option("follow", ""), "options round trip");
    }

    public static void TestSettingsValidation()
    {
        var (host, vm, ui, _) = Create();
        vm.SettingsPage.SyncIntervalMinutes = 0;
        Assert.True(!vm.SettingsPage.Save() && ui.Errors.Last().Contains("between 1"), "range");
        vm.SettingsPage.SyncIntervalMinutes = 15;
        Assert.True(vm.SettingsPage.Save(), "saved");
        Assert.Equal(15, host.Settings.SyncIntervalMinutes, "persisted");
    }
}

public static class ViewModelRefreshTests
{
    public static void TestRefreshKeepsSelectionAndDetails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openiptv-vm-" + Guid.NewGuid());
        var host = new AppHost(dir);
        var cat = CatalogMerger.Merge(new Catalog(), CoreTests.SampleHarvest(), new AppSettings(), true, DateTime.Now).Catalog;
        cat.Channels["BBCNews.uk"].Streams[0].Status = Availability.Active;
        host.Catalog.Replace(cat);
        var vm = new MainViewModel(host, new FakeUi(), new FakePlayer(), debounce: TimeSpan.Zero);
        vm.StatusFilter = "All";
        vm.SelectedRow = vm.Rows.First(r => r.Id == "BBCNews.uk");
        // Simulate the grid clearing its selection while the list is replaced.
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Rows)) vm.SelectedRow = null; };
        host.Catalog.NotifyChanged();
        Assert.Equal("BBC News", vm.Channel?.Name, "details kept");
        Assert.Equal("BBCNews.uk", vm.SelectedRow?.Id, "selection restored");
    }
}

public static class CheckerRobustnessTests
{
    public static async Task TestCancellationIsQuietAndCallbackErrorsDoNotStopOthers()
    {
        using var srv = new TestServer();
        using var checker = new OpenIPTV.Core.Checking.StreamChecker(5);
        var items = Enumerable.Range(0, 20).Select(i => new StreamInfo { Url = srv.Base + "master.m3u8?i=" + i }).ToList();
        int results = 0; var errors = new List<string>();
        await checker.CheckManyAsync(items, 4, (s, r) =>
        {
            Interlocked.Increment(ref results);
            if (s.Url.EndsWith("=3")) throw new InvalidOperationException("boom");
        }, null, CancellationToken.None, e => { lock (errors) errors.Add(e); });
        Assert.Equal(20, results, "all checked despite one failing callback");
        Assert.Equal(1, errors.Count, "error reported");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool threw = false;
        try { await checker.CheckManyAsync(items, 4, (_, _) => { }, null, cts.Token); }
        catch (OperationCanceledException) { threw = true; }
        Assert.True(threw, "cancellation surfaces once to the caller");
    }
}

public static class ThemeTests
{
    public static void TestThemeAppliesAndPersistsImmediately()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openiptv-vm-" + Guid.NewGuid());
        var host = new AppHost(dir);
        var ui = new FakeUi();
        var vm = new MainViewModel(host, ui, new FakePlayer(), debounce: TimeSpan.Zero);
        Assert.Equal("System", vm.SettingsPage.Theme, "default");
        vm.SettingsPage.Theme = "Dark";
        Assert.Equal("Dark", ui.Theme, "applied at once");
        Assert.Equal("Dark", new AppHost(dir).Settings.Theme, "saved without pressing Save");
        vm.StatusFilter = "All";
        vm.Dispose();                               // UI-state save must not undo the theme
        Assert.Equal("Dark", new AppHost(dir).Settings.Theme, "kept after UI-state save");
        vm.SettingsPage.Theme = "Purple";
        Assert.Equal("System", vm.SettingsPage.Theme, "unknown → System");
    }
}

public static class PagingTests
{
    private static (AppHost, MainViewModel) Create(int channels)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openiptv-vm-" + Guid.NewGuid());
        var host = new AppHost(dir);
        var cat = new Catalog();
        cat.Countries["SG"] = "Singapore";
        cat.Countries["MY"] = "Malaysia";
        for (int i = 0; i < channels; i++)
        {
            var ch = new Channel { Id = $"C{i:000}.sg", Name = $"Channel {i:000}", CountryCode = i % 2 == 0 ? "SG" : "MY" };
            ch.Streams.Add(new StreamInfo { Url = $"https://x/{i}.m3u8", Status = Availability.Active });
            cat.Channels[ch.Id] = ch;
        }
        host.Catalog.Replace(cat);
        return (host, new MainViewModel(host, new FakeUi(), new FakePlayer(), debounce: TimeSpan.Zero));
    }

    public static void TestPagingNavigationAndCounts()
    {
        var (_, vm) = Create(260);
        Assert.Equal(100, vm.PageSize, "default page size");
        Assert.Equal(3, vm.PageCount, "pages");
        Assert.Equal(100, vm.Rows.Count, "first page rows");
        Assert.True(vm.CountsText.EndsWith("showing 1–100 of 260 (Active)"), vm.CountsText);
        Assert.True(!vm.PrevPageCommand.CanExecute(null) && vm.NextPageCommand.CanExecute(null), "commands on page 1");
        vm.NextPageCommand.Execute(null);
        Assert.Equal("Channel 100", vm.Rows[0].Name, "page 2 starts at 101st");
        vm.LastPageCommand.Execute(null);
        Assert.Equal(60, vm.Rows.Count, "last page rows");
        Assert.True(vm.CountsText.EndsWith("showing 201–260 of 260 (Active)"), vm.CountsText);
        Assert.True(!vm.NextPageCommand.CanExecute(null), "no next on last page");
        vm.CurrentPage = 99;
        Assert.Equal(3, vm.CurrentPage, "clamped");
        vm.CountryFilter.Select("SG");
        Assert.Equal(1, vm.CurrentPage, "filter change resets to page 1");
        Assert.Equal(130, vm.MatchCount, "matches across pages");
    }

    public static void TestPageSizeKeepsPositionPersistsAndExportUsesAllPages()
    {
        var (host, vm) = Create(260);
        vm.CurrentPage = 3;                       // first visible = item 201
        vm.PageSize = 25;
        Assert.Equal(9, vm.CurrentPage, "item 201 is on page 9 of 25-per-page");
        Assert.Equal("Channel 200", vm.Rows[0].Name, "same first channel visible");
        vm.PageSize = 33;                         // not offered → falls back to 100
        Assert.Equal(100, vm.PageSize, "invalid size → 100");
        vm.PageSize = 50;
        var ui = new FakeUi { SavePath = Path.Combine(host.Store.DataDir, "all.m3u") };
        var vm2 = new MainViewModel(host, ui, new FakePlayer(), debounce: TimeSpan.Zero);
        vm2.ExportM3uCommand.Execute(null);
        Assert.Equal(260, File.ReadAllLines(ui.SavePath).Count(l => l.StartsWith("#EXTINF")), "export = every page");
        vm.Dispose();
        Assert.Equal(50, new AppHost(host.Store.DataDir).Settings.PageSize, "page size remembered");
    }

    public static void TestColumnHeaderSortCoversAllPages()
    {
        var (host, vm) = Create(260);
        vm.NextPageCommand.Execute(null);
        vm.SortByColumn(SortFields.Name);                 // already sorted by Name → flip to descending
        Assert.True(vm.SortDescending, "same column flips direction");
        Assert.Equal(1, vm.CurrentPage, "sorting returns to page 1");
        Assert.Equal("Channel 259", vm.Rows[0].Name, "descending across all pages, not just this page");
        vm.Sort2 = SortFields.Country;
        vm.SortByColumn(SortFields.Country);              // new column → ascending; removed as second key
        Assert.Equal(SortFields.Country, vm.Sort1, "primary sort");
        Assert.True(!vm.SortDescending, "new column starts ascending");
        Assert.Equal("", vm.Sort2, "no duplicate second key");
        Assert.Equal("Malaysia", vm.Rows[0].Country, "Malaysia before Singapore");
        vm.SortByColumn(SortFields.Code);
        Assert.Equal("MY", vm.Rows[0].CountryCode, "code column sorts");
        vm.SortByColumn("Bogus");
        Assert.Equal(SortFields.Code, vm.Sort1, "unknown column ignored");
        vm.Dispose();
        var s = new AppHost(host.Store.DataDir).Settings;
        Assert.Equal(SortFields.Code, s.Sort1, "sort remembered");
    }

    public static void TestFiltersAndSearchRemembered()
    {
        var (host, vm) = Create(260);
        vm.CountryFilter.Select("SG");
        vm.SearchText = "Channel 1";
        vm.Dispose();
        // Saving the Settings page must not wipe the remembered filters.
        var settings = new SettingsViewModel(host, new FakeUi());
        settings.Save();
        var vm2 = new MainViewModel(host, new FakeUi(), new FakePlayer(), debounce: TimeSpan.Zero);
        Assert.Equal("Country: Singapore (SG)", vm2.CountryFilter.Summary, "country filter restored");
        Assert.Equal("Channel 1", vm2.SearchText, "search restored");
        Assert.True(vm2.Rows.All(r => r.CountryCode == "SG" && r.Name.StartsWith("Channel 1")), "restored filters applied");
        Assert.Equal(50, vm2.MatchCount, "SG (even) channels named Channel 1xx");
        Assert.Equal("SG", new AppHost(host.Store.DataDir).Settings.CountryFilter.Single(), "saved to settings.json");
        vm2.ClearFiltersCommand.Execute(null);
        vm2.Dispose();
        var vm3 = new MainViewModel(host, new FakeUi(), new FakePlayer(), debounce: TimeSpan.Zero);
        Assert.Equal("Country: All", vm3.CountryFilter.Summary, "cleared filters remembered too");
        Assert.Equal(260, vm3.MatchCount, "all channels after clear");
    }

    public static void TestClearLogAndHistory()
    {
        var (host, vm) = Create(3);
        host.Agent.History.Add(new SyncRun { Trigger = "manual", Status = "ok", Started = DateTime.Now, Finished = DateTime.Now });
        host.Store.SaveHistory(host.Agent.History);
        host.Store.AppendLog("old line");
        vm.History.Reload();
        vm.History.LogLines.Add("12:00:00  something");
        Assert.True(vm.History.ClearHistoryCommand.CanExecute(null), "enabled with entries");

        var ui = new FakeUi { ConfirmAnswer = false };
        var history = new HistoryViewModel(host, ui);
        history.ClearHistoryCommand.Execute(null);
        Assert.Equal(1, history.Runs.Count, "cancelled confirm keeps history");

        vm.History.ClearHistoryCommand.Execute(null);
        Assert.Equal(0, vm.History.Runs.Count, "history cleared");
        Assert.True(!vm.History.ClearHistoryCommand.CanExecute(null), "disabled when empty");
        Assert.Equal(0, host.Store.LoadHistory().Count, "history.json emptied");

        vm.History.ClearLogCommand.Execute(null);
        Assert.Equal(0, vm.History.LogLines.Count, "live log cleared");
        var log = File.ReadAllText(host.Store.LogPath);
        Assert.True(!log.Contains("old line") && log.Contains("Log cleared"), log);
    }

    public static void TestMuteState()
    {
        var (_, vm) = Create(3);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(!vm.IsMuted, "starts unmuted");
        vm.ToggleMuteCommand.Execute(null);
        Assert.True(vm.IsMuted && changed.Contains(nameof(MainViewModel.IsMuted)), "muted and notified (button turns orange)");
        Assert.Equal("Sound is muted. Click to unmute", vm.MuteToolTip, "tooltip");
        vm.ToggleMuteCommand.Execute(null);
        Assert.True(!vm.IsMuted, "unmuted again");
    }

    public static void TestDataRefreshKeepsPageAndSelection()
    {
        var (host, vm) = Create(260);
        vm.CurrentPage = 2;
        vm.SelectedRow = vm.Rows[5];
        host.Catalog.NotifyChanged();             // e.g. sync finished
        Assert.Equal(2, vm.CurrentPage, "page kept after data refresh");
        Assert.Equal("Channel 105", vm.SelectedRow?.Name, "selection kept");
    }
}
