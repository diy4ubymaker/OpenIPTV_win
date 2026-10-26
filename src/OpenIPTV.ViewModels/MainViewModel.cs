using System.Collections.ObjectModel;
using OpenIPTV.Core.Models;
using OpenIPTV.Core.Services;

namespace OpenIPTV.ViewModels;

/// <summary>State and behaviour of the main window (Channels tab + header), plus the other tabs as children.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly IUiServices _ui;
    private readonly IPlayer _player;
    private readonly Debouncer _searchDebounce;
    private readonly Debouncer _catalogDebounce;
    private readonly Debouncer _saveUiState;

    private string _searchText = "";
    private string _statusFilter;
    private string _sort1;
    private string _sort2;
    private bool _sortDescending;
    private IReadOnlyList<ChannelRow> _rows = [];
    private IReadOnlyList<ChannelRow> _allRows = [];
    private int _pageSize;
    private int _currentPage = 1;
    private ChannelRow? _selectedRow;
    private ChannelDetail? _channel;
    private StreamView? _selectedStream;
    private string _countsText = "";
    private string _totalsText = "";
    private string _syncText = "";
    private string _nextRunText = "";
    private bool _isSyncing;
    private string _playerMessage = "Select a channel and press Play (or double-click it).";
    private string _channelMessage = "";
    private PlayerState _playerState = PlayerState.Idle;
    private string? _playingChannelName;
    private int _volume;
    private bool _isFullscreen;
    private bool _refreshing;
    private string _syncPhase = "";

    public MainViewModel(AppHost host, IUiServices ui, IPlayer player, TimeSpan? debounce = null)
    {
        _host = host;
        _ui = ui;
        _player = player;
        var s = host.Settings;
        _statusFilter = StatusFilters.Contains(s.StatusFilter) ? s.StatusFilter : "Active";
        _sort1 = SortFields.All.Contains(s.Sort1) ? s.Sort1 : SortFields.Name;
        _sort2 = SortOptions2.Contains(s.Sort2) ? s.Sort2 : "";
        _sortDescending = s.SortDescending;
        _volume = Math.Clamp(s.Volume, 0, 100);
        _pageSize = PageSizes.Contains(s.PageSize) ? s.PageSize : 100;
        _searchText = s.SearchText ?? "";
        _player.Volume = _volume;

        var d = debounce ?? TimeSpan.FromMilliseconds(250);
        _searchDebounce = new Debouncer(ui, d, Refresh);
        _catalogDebounce = new Debouncer(ui, debounce ?? TimeSpan.FromMilliseconds(800), OnCatalogChanged);
        _saveUiState = new Debouncer(ui, debounce ?? TimeSpan.FromSeconds(1), SaveUiState);

        CategoryFilter = new FilterViewModel("Category", SortFields.Category);
        LanguageFilter = new FilterViewModel("Language", SortFields.Language);
        CountryFilter = new FilterViewModel("Country", SortFields.Country);
        // Restore the last Category/Language/Country selections (applied when the options load below).
        CategoryFilter.Restore(s.CategoryFilter);
        LanguageFilter.Restore(s.LanguageFilter);
        CountryFilter.Restore(s.CountryFilter);
        foreach (var f in Filters) f.Changed += () => { Refresh(); _saveUiState.Trigger(); };

        Sources = new SourcesViewModel(host, ui);
        SettingsPage = new SettingsViewModel(host, ui);
        History = new HistoryViewModel(host, ui);

        PlayCommand = new RelayCommand(Play, () => SelectedStream is not null);
        StopCommand = new RelayCommand(() => _player.Stop());
        PauseCommand = new RelayCommand(() => _player.TogglePause());
        RecheckCommand = new AsyncCommand(RecheckAsync, () => Channel is not null, ShowError);
        CopyUrlCommand = new RelayCommand(() => { if (SelectedStream is { } st) { _ui.CopyText(st.Url); ChannelMessage = "Source URL copied."; } },
            () => SelectedStream is not null);
        OpenWebsiteCommand = new RelayCommand(() => { if (Channel?.Website is { } w) _ui.Open(w); }, () => Channel?.Website is not null);
        SyncNowCommand = new RelayCommand(SyncNow, () => !IsSyncing);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ExportM3uCommand = new RelayCommand(ExportM3u);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        OpenDataFolderCommand = new RelayCommand(() => _ui.Open(_host.Store.DataDir));
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);
        ExitFullscreenCommand = new RelayCommand(() => IsFullscreen = false);
        FirstPageCommand = new RelayCommand(() => CurrentPage = 1, () => CurrentPage > 1);
        PrevPageCommand = new RelayCommand(() => CurrentPage--, () => CurrentPage > 1);
        NextPageCommand = new RelayCommand(() => CurrentPage++, () => CurrentPage < PageCount);
        LastPageCommand = new RelayCommand(() => CurrentPage = PageCount, () => CurrentPage < PageCount);
        ToggleMuteCommand = new RelayCommand(() => { _player.Mute = !_player.Mute; OnPropertyChanged(nameof(IsMuted)); OnPropertyChanged(nameof(MuteToolTip)); });

        _player.StateChanged += (state, msg) => _ui.Post(() => OnPlayerState(state, msg));
        _host.Catalog.Changed += () => _catalogDebounce.Trigger();
        _host.Agent.StatusChanged += st => _ui.Post(() => OnSyncStatus(st));
        _host.Agent.RunFinished += _ => _ui.Post(() => History.Reload());

        RefreshFacets();
        Refresh();
        UpdateTotals();
        OnSyncStatus(_host.Agent.Status);
    }

    // ------------------------------------------------------------------ static choices
    public static IReadOnlyList<string> StatusFilters { get; } = ["All", "Active", "Inactive", "Unchecked"];
    public static IReadOnlyList<string> SortOptions { get; } = SortFields.All;
    public static IReadOnlyList<string> SortOptions2 { get; } = ["", .. SortFields.All];
    public static IReadOnlyList<int> PageSizes { get; } = [25, 50, 100, 200, 500];

    public string Title => $"OpenIPTV {AppHost.Version}";
    public string Version => AppHost.Version;
    public string DataFolder => _host.Store.DataDir;

    // ------------------------------------------------------------------ child pages
    public FilterViewModel CategoryFilter { get; }
    public FilterViewModel LanguageFilter { get; }
    public FilterViewModel CountryFilter { get; }
    public IEnumerable<FilterViewModel> Filters => [CategoryFilter, LanguageFilter, CountryFilter];
    public SourcesViewModel Sources { get; }
    public SettingsViewModel SettingsPage { get; }
    public HistoryViewModel History { get; }

    // ------------------------------------------------------------------ commands
    public RelayCommand PlayCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand PauseCommand { get; }
    public AsyncCommand RecheckCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand OpenWebsiteCommand { get; }
    public RelayCommand SyncNowCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand ExportM3uCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand ToggleFullscreenCommand { get; }
    public RelayCommand ExitFullscreenCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }
    public RelayCommand FirstPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand LastPageCommand { get; }

    // ------------------------------------------------------------------ filters and list
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) { _searchDebounce.Trigger(); _saveUiState.Trigger(); } }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set { if (Set(ref _statusFilter, value)) { Refresh(); _saveUiState.Trigger(); } }
    }

    public string Sort1
    {
        get => _sort1;
        set { if (Set(ref _sort1, value ?? SortFields.Name)) { Refresh(); _saveUiState.Trigger(); } }
    }

    public string Sort2
    {
        get => _sort2;
        set { if (Set(ref _sort2, value ?? "")) { Refresh(); _saveUiState.Trigger(); } }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set { if (Set(ref _sortDescending, value)) { Refresh(); _saveUiState.Trigger(); } }
    }

    /// <summary>
    /// Column-header click: the current sort column flips direction; another column becomes the primary
    /// sort, ascending. Sorts every matching channel (all pages), then shows page 1.
    /// </summary>
    public void SortByColumn(string field)
    {
        if (!SortFields.All.Contains(field)) return;
        if (_sort1 == field)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sort1 = field;
            _sortDescending = false;
            OnPropertyChanged(nameof(Sort1));
        }
        OnPropertyChanged(nameof(SortDescending));
        if (_sort2 == field)
        {
            _sort2 = "";
            OnPropertyChanged(nameof(Sort2));
        }
        Refresh();
        _saveUiState.Trigger();
    }

    /// <summary>The channels on the current page.</summary>
    public IReadOnlyList<ChannelRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    /// <summary>All channels matching the filters (every page); used for paging and export.</summary>
    public int MatchCount => _allRows.Count;

    /// <summary>Channels per page (25–500), remembered between sessions.</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            var size = PageSizes.Contains(value) ? value : 100;
            if (size == _pageSize) return;
            int firstIndex = (_currentPage - 1) * _pageSize;      // keep the first visible channel on screen
            _pageSize = size;
            OnPropertyChanged();
            _currentPage = firstIndex / size + 1;
            ShowPage();
            _saveUiState.Trigger();
        }
    }

    public int PageCount => Math.Max(1, (_allRows.Count + _pageSize - 1) / _pageSize);

    /// <summary>1-based page number; out-of-range values are clamped.</summary>
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            var page = Math.Clamp(value, 1, PageCount);
            if (page == _currentPage) { OnPropertyChanged(); return; }   // re-sync a clamped text box
            _currentPage = page;
            ShowPage();
        }
    }

    public string PageText => $"of {PageCount:N0}";

    /// <summary>"12,345 channels · ✅ 8,610 active · ❌ 3,110 inactive · ⏳ 0 unchecked — showing 8,610 (Active)".</summary>
    public string CountsText { get => _countsText; private set => Set(ref _countsText, value); }

    public ChannelRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            // While the list is being replaced the grid briefly reports "nothing selected"; keep the details pane.
            if (_refreshing) { _selectedRow = value; return; }
            if (Set(ref _selectedRow, value) && value is not null) LoadChannel(value.Id);
        }
    }

    public ChannelDetail? Channel
    {
        get => _channel;
        private set
        {
            if (!Set(ref _channel, value)) return;
            OnPropertyChanged(nameof(HasChannel));
            OnPropertyChanged(nameof(ChannelStatusText));
            OnPropertyChanged(nameof(ChannelLastChecked));
            RecheckCommand.RaiseCanExecuteChanged();
            OpenWebsiteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasChannel => Channel is not null;

    public string ChannelStatusText => Channel?.Status switch
    {
        Availability.Active => "✅ Active",
        Availability.Inactive => "❌ Inactive",
        null => "",
        _ => "⏳ Unchecked",
    };

    public string ChannelLastChecked
    {
        get
        {
            var last = Channel?.Streams.Where(s => s.LastChecked is not null).Select(s => s.LastChecked!.Value).DefaultIfEmpty().Max();
            return last is { } l && l != default ? $"last checked {l:yyyy-MM-dd HH:mm}" : "not checked yet";
        }
    }

    public ObservableCollection<StreamView> Streams { get; } = [];

    public StreamView? SelectedStream
    {
        get => _selectedStream;
        set
        {
            if (!Set(ref _selectedStream, value)) return;
            OnPropertyChanged(nameof(SourceUrl));
            OnPropertyChanged(nameof(SourceNotes));
            PlayCommand.RaiseCanExecuteChanged();
            CopyUrlCommand.RaiseCanExecuteChanged();
        }
    }

    public string SourceUrl => SelectedStream?.Url ?? "";

    public string SourceNotes
    {
        get
        {
            if (SelectedStream is not { } s) return "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(s.Referrer)) parts.Add($"Referer: {s.Referrer}");
            if (!string.IsNullOrEmpty(s.UserAgent)) parts.Add($"User-Agent: {s.UserAgent}");
            if (s.LastChecked is { } lc) parts.Add($"last check: {s.LastStatus ?? "—"} at {lc:yyyy-MM-dd HH:mm}");
            if (!s.IsHttp) parts.Add("non-HTTP source (played by VLC; RTMP/RTSP only tested for reachability)");
            return string.Join("  ·  ", parts);
        }
    }

    public string ChannelMessage { get => _channelMessage; set => Set(ref _channelMessage, value); }

    // ------------------------------------------------------------------ header / sync
    public string TotalsText { get => _totalsText; private set => Set(ref _totalsText, value); }
    public string SyncText { get => _syncText; private set => Set(ref _syncText, value); }
    public string NextRunText { get => _nextRunText; private set => Set(ref _nextRunText, value); }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set { if (Set(ref _isSyncing, value)) SyncNowCommand.RaiseCanExecuteChanged(); }
    }

    // ------------------------------------------------------------------ player
    public string PlayerMessage { get => _playerMessage; private set => Set(ref _playerMessage, value); }
    public PlayerState PlayerState { get => _playerState; private set { if (Set(ref _playerState, value)) OnPropertyChanged(nameof(IsPlaying)); } }
    public bool IsPlaying => PlayerState is PlayerState.Playing or PlayerState.Buffering or PlayerState.Opening or PlayerState.Paused;
    public string NowPlaying => _playingChannelName is null ? "" : $"▶ {_playingChannelName}";

    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 100))) return;
            _player.Volume = _volume;
            _saveUiState.Trigger();
        }
    }

    public bool IsMuted => _player.Mute;
    public string MuteToolTip => _player.Mute ? "Sound is muted. Click to unmute" : "Mute";

    public bool IsFullscreen { get => _isFullscreen; set => Set(ref _isFullscreen, value); }

    // ------------------------------------------------------------------ behaviour
    private ChannelQuery BuildQuery() => new()
    {
        Text = SearchText,
        Categories = CategoryFilter.SelectedValues,
        Languages = LanguageFilter.SelectedValues,
        Countries = CountryFilter.SelectedValues,
        Status = Enum.TryParse<StatusFilter>(StatusFilter, out var st) ? st : Core.Services.StatusFilter.Active,
        Sort = [Sort1, Sort2],
        Descending = SortDescending,
    };

    /// <summary>Re-run the query after a filter, search or sort change (UI thread): back to page 1.</summary>
    public void Refresh() => Refresh(countsOnly: false, resetPage: true);

    /// <param name="countsOnly">Only update the counts line; leave the list alone.</param>
    /// <param name="resetPage">Go to page 1 (new filters) instead of staying on the current page (data refresh).</param>
    private void Refresh(bool countsOnly, bool resetPage)
    {
        var (rows, c) = _host.Catalog.Search(BuildQuery());
        _lastCounts = c;
        if (!countsOnly)
        {
            _allRows = rows;
            if (resetPage) _currentPage = 1;
            ShowPage();
        }
        else UpdateCountsText();
    }

    private StatusCounts _lastCounts = new(0, 0, 0, 0);

    /// <summary>Show the current page of <see cref="_allRows"/>, keeping the selected channel selected if it is on it.</summary>
    private void ShowPage()
    {
        _currentPage = Math.Clamp(_currentPage, 1, PageCount);
        var keepId = SelectedRow?.Id ?? Channel?.Id;
        var page = _allRows.Skip((_currentPage - 1) * _pageSize).Take(_pageSize).ToList();
        _refreshing = true;
        try { Rows = page; }
        finally { _refreshing = false; }
        if (keepId is not null)
        {
            _selectedRow = page.FirstOrDefault(r => r.Id == keepId);   // re-select without reloading the details pane
            OnPropertyChanged(nameof(SelectedRow));
        }
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(MatchCount));
        FirstPageCommand.RaiseCanExecuteChanged();
        PrevPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        LastPageCommand.RaiseCanExecuteChanged();
        UpdateCountsText();
    }

    private void UpdateCountsText()
    {
        var c = _lastCounts;
        int from = _allRows.Count == 0 ? 0 : (_currentPage - 1) * _pageSize + 1;
        int to = Math.Min(_currentPage * _pageSize, _allRows.Count);
        CountsText = $"{c.Total:N0} channels · ✅ {c.Active:N0} active · ❌ {c.Inactive:N0} inactive · ⏳ {c.Unchecked:N0} unchecked" +
                     $" — showing {from:N0}–{to:N0} of {_allRows.Count:N0} ({StatusFilter})";
    }

    private void RefreshFacets()
    {
        CategoryFilter.SetOptions(_host.Catalog.Facet(SortFields.Category));
        LanguageFilter.SetOptions(_host.Catalog.Facet(SortFields.Language));
        CountryFilter.SetOptions(_host.Catalog.Facet(SortFields.Country));
    }

    private void UpdateTotals()
    {
        var t = _host.Catalog.Totals();
        var (streams, active) = _host.Catalog.StreamTotals();
        TotalsText = $"{t.Total:N0} channels (✅ {t.Active:N0} · ❌ {t.Inactive:N0} · ⏳ {t.Unchecked:N0}) · {streams:N0} streams ({active:N0} active)";
    }

    private void OnCatalogChanged()
    {
        // While streams are being tested the catalog changes every few seconds: update the counts only,
        // so the list does not jump back to the top while the user browses (unless it is still empty).
        bool checking = IsSyncing && _syncPhase.StartsWith("Checking", StringComparison.Ordinal) && Rows.Count > 0;
        if (!checking) RefreshFacets();
        Refresh(countsOnly: checking, resetPage: false);
        UpdateTotals();
        if (Channel is { } ch) LoadChannel(ch.Id, keepStream: true);
    }

    private void LoadChannel(string? id, bool keepStream = false)
    {
        var keepUrl = keepStream ? SelectedStream?.Url : null;
        var detail = id is null ? null : _host.Catalog.Get(id);
        Channel = detail;
        Streams.Clear();
        if (detail is null) { SelectedStream = null; return; }
        foreach (var s in detail.Streams) Streams.Add(s);
        SelectedStream = Streams.FirstOrDefault(s => s.Url == keepUrl) ?? Streams.FirstOrDefault();
        OnPropertyChanged(nameof(ChannelLastChecked));
        OnPropertyChanged(nameof(ChannelStatusText));
        if (!keepStream) ChannelMessage = "";
    }

    public void Play()
    {
        if (SelectedStream is not { } s || Channel is null) return;
        _playingChannelName = Channel.Name;
        OnPropertyChanged(nameof(NowPlaying));
        PlayerMessage = $"Opening {Channel.Name} …";
        _player.Play(s.Url, s.Referrer, s.UserAgent, _host.Settings.PlayerNetworkCachingMs);
    }

    /// <summary>Play a channel straight from the list (double-click / Enter).</summary>
    public void PlayRow(ChannelRow? row)
    {
        if (row is null) return;
        if (SelectedRow?.Id != row.Id) SelectedRow = row;
        Play();
    }

    private void OnPlayerState(PlayerState state, string? message)
    {
        PlayerState = state;
        PlayerMessage = state switch
        {
            PlayerState.Opening => $"Opening {_playingChannelName} …",
            PlayerState.Buffering => message ?? "Buffering …",
            PlayerState.Playing => "",
            PlayerState.Paused => "Paused",
            PlayerState.Stopped or PlayerState.Idle => "Stopped",
            PlayerState.Ended => "The stream ended. It may be offline — try another source or press Check source now.",
            PlayerState.Error => "Playback failed — the source may be offline or geo-blocked. Try another source or press Check source now.",
            _ => PlayerMessage,
        };
        if (state is PlayerState.Stopped or PlayerState.Idle) { _playingChannelName = null; OnPropertyChanged(nameof(NowPlaying)); }
    }

    private async Task RecheckAsync()
    {
        if (Channel is not { } ch) return;
        ChannelMessage = "Checking sources …";
        var (_, msg) = await _host.Agent.RecheckChannelAsync(ch.Id);
        ChannelMessage = msg;
        LoadChannel(ch.Id, keepStream: true);
        ChannelMessage = msg;
        Refresh(countsOnly: false, resetPage: false);
        UpdateTotals();
    }

    private void SyncNow()
    {
        if (!_host.Agent.RunNow("manual"))
            _ui.ShowInfo("A sync is already running.");
    }

    private void OnSyncStatus(SyncStatus st)
    {
        IsSyncing = st.Running;
        _syncPhase = st.Phase;
        SyncText = st.Running ? $"🔄 {st.Phase} {st.Progress}" : st.LastResult ?? "";
        NextRunText = st.NextRun is { } n && !st.Running ? $"next sync {n:HH:mm}" : "";
    }

    private void ClearFilters()
    {
        foreach (var f in Filters) f.Clear();
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
        Refresh();
        _saveUiState.Trigger();
    }

    private void ExportM3u()
    {
        if (_allRows.Count == 0) { _ui.ShowInfo("No channels to export with the current filters."); return; }
        var path = _ui.AskSaveFile("Export playlist", $"openiptv-{DateTime.Now:yyyyMMdd}.m3u", "M3U playlist|*.m3u");
        if (path is null) return;
        var activeOnly = StatusFilter == "Active";
        File.WriteAllText(path, _host.Catalog.ExportM3u(_allRows.Select(r => r.Id), allStreams: false, activeOnly), new System.Text.UTF8Encoding(false));
        _ui.ShowInfo($"Exported {_allRows.Count:N0} channels to {path}");
    }

    private void ExportCsv()
    {
        if (_allRows.Count == 0) { _ui.ShowInfo("No channels to export with the current filters."); return; }
        var path = _ui.AskSaveFile("Export channels", $"openiptv-channels-{DateTime.Now:yyyyMMdd-HHmm}.csv", "CSV file|*.csv");
        if (path is null) return;
        File.WriteAllText(path, _host.Catalog.ExportCsv(_allRows.Select(r => r.Id)), new System.Text.UTF8Encoding(true)); // BOM: Excel-friendly
        _ui.ShowInfo($"Exported {_allRows.Count:N0} channels to {path}");
    }

    private void SaveUiState()
    {
        var s = _host.Settings;
        s.StatusFilter = StatusFilter;
        s.Sort1 = Sort1;
        s.Sort2 = Sort2;
        s.SortDescending = SortDescending;
        s.Volume = Volume;
        s.PageSize = PageSize;
        s.SearchText = SearchText;
        s.CategoryFilter = [.. CategoryFilter.SelectedValues];
        s.LanguageFilter = [.. LanguageFilter.SelectedValues];
        s.CountryFilter = [.. CountryFilter.SelectedValues];
        _host.SaveSettings(s);
    }

    private void ShowError(Exception ex) => _ui.ShowError(ex.Message);

    public void Dispose()
    {
        _searchDebounce.Dispose();
        _catalogDebounce.Dispose();
        _saveUiState.Dispose();
        SaveUiState();
    }
}
