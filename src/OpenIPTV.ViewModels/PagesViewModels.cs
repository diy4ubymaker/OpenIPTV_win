using System.Collections.ObjectModel;
using OpenIPTV.Core.Models;

namespace OpenIPTV.ViewModels;

// ====================================================================== Sources tab
public sealed class SourceItem : ObservableObject
{
    private string _name;
    private string _kind;
    private string _url;
    private bool _enabled;
    private string _options;

    public SourceItem(SourceConfig s)
    {
        _name = s.Name;
        _kind = s.Kind;
        _url = s.Url;
        _enabled = s.Enabled;
        _options = string.Join("; ", s.Options.Select(kv => $"{kv.Key}={kv.Value}"));
        LastRun = s.LastRun;
        LastStatus = s.LastStatus;
    }

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Kind { get => _kind; set => Set(ref _kind, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Options as "key=value; key=value", e.g. "follow=categories,languages,countries".</summary>
    public string Options { get => _options; set => Set(ref _options, value); }

    public DateTime? LastRun { get; }
    public string? LastStatus { get; }
    public string LastRunText => LastRun is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "";

    public static Dictionary<string, string> ParseOptions(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Option \"{part}\" must look like key=value.");
            d[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return d;
    }

    public SourceConfig ToConfig() => new()
    {
        Name = Name.Trim(), Kind = Kind, Url = Url.Trim(), Enabled = Enabled, Options = ParseOptions(Options),
        LastRun = LastRun, LastStatus = LastStatus,
    };
}

public sealed class SourcesViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUiServices _ui;
    private SourceItem? _selected;
    private bool _dirty;

    public SourcesViewModel(AppHost host, IUiServices ui)
    {
        _host = host;
        _ui = ui;
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null);
        SaveCommand = new RelayCommand(() => Save());
        RevertCommand = new RelayCommand(Load);
        DefaultsCommand = new RelayCommand(RestoreDefaults);
        host.SourcesChanged += () => ui.Post(() => { if (!_dirty) Load(); });
        Load();
    }

    public static IReadOnlyList<string> Kinds { get; } = SourceKinds.All;

    public ObservableCollection<SourceItem> Items { get; } = [];

    public SourceItem? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) RemoveCommand.RaiseCanExecuteChanged(); }
    }

    public bool IsDirty { get => _dirty; private set => Set(ref _dirty, value); }

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand DefaultsCommand { get; }

    public string Help =>
        "Kinds: iptv_api (iptv-org API base URL) · database_csv (iptv-org database data folder) · m3u (any M3U/M3U8 playlist) · " +
        "playlists_md (PLAYLISTS.md; option follow=categories,languages,countries) · github_repo (repository; options branch=master; path=streams). " +
        "GitHub /blob/ and /tree/ links are converted automatically. Changes apply on the next sync.";

    private void Track(SourceItem item) => item.PropertyChanged += (_, _) => IsDirty = true;

    public void Load()
    {
        Items.Clear();
        foreach (var s in _host.Sources)
        {
            var item = new SourceItem(s);
            Track(item);
            Items.Add(item);
        }
        IsDirty = false;
    }

    private void Add()
    {
        var item = new SourceItem(new SourceConfig { Name = "New playlist", Kind = SourceKinds.M3u, Url = "https://" });
        Track(item);
        Items.Add(item);
        Selected = item;
        IsDirty = true;
    }

    private void Remove()
    {
        if (Selected is null) return;
        Items.Remove(Selected);
        Selected = null;
        IsDirty = true;
    }

    private void RestoreDefaults()
    {
        if (!_ui.Confirm("Replace the source list with the five default iptv-org sources?")) return;
        Items.Clear();
        foreach (var s in SourceConfig.Defaults())
        {
            var item = new SourceItem(s);
            Track(item);
            Items.Add(item);
        }
        IsDirty = true;
    }

    /// <summary>Validate and save. Returns false (and shows why) if something is wrong.</summary>
    public bool Save()
    {
        var list = new List<SourceConfig>();
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) { _ui.ShowError("Every source needs a name."); return false; }
            if (!SourceKinds.All.Contains(item.Kind)) { _ui.ShowError($"{item.Name}: unknown kind '{item.Kind}'."); return false; }
            if (!Uri.TryCreate(item.Url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            { _ui.ShowError($"{item.Name}: the URL must start with http:// or https://"); return false; }
            try { list.Add(item.ToConfig()); }
            catch (FormatException ex) { _ui.ShowError($"{item.Name}: {ex.Message}"); return false; }
        }
        if (list.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
        { _ui.ShowError("Source names must be unique."); return false; }
        IsDirty = false;
        _host.SaveSources(list);
        _ui.ShowInfo("Sources saved. They are used from the next sync — press Sync now to apply them immediately.");
        return true;
    }
}

// ====================================================================== Settings tab
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly IUiServices _ui;
    private AppSettings _s;

    public SettingsViewModel(AppHost host, IUiServices ui)
    {
        _host = host;
        _ui = ui;
        _s = host.Settings;
        SaveCommand = new RelayCommand(() => Save());
        RevertCommand = new RelayCommand(() => { _s = _host.Settings; OnPropertyChanged(string.Empty); });
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }

    public int SyncIntervalMinutes { get => _s.SyncIntervalMinutes; set { _s.SyncIntervalMinutes = value; OnPropertyChanged(); } }
    public bool SyncOnStartup { get => _s.SyncOnStartup; set { _s.SyncOnStartup = value; OnPropertyChanged(); } }
    public bool CheckStreamsOnSync { get => _s.CheckStreamsOnSync; set { _s.CheckStreamsOnSync = value; OnPropertyChanged(); } }
    public int CheckConcurrency { get => _s.CheckConcurrency; set { _s.CheckConcurrency = value; OnPropertyChanged(); } }
    public int CheckTimeoutSeconds { get => _s.CheckTimeoutSeconds; set { _s.CheckTimeoutSeconds = value; OnPropertyChanged(); } }
    public int MaxChecksPerCycle { get => _s.MaxChecksPerCycle; set { _s.MaxChecksPerCycle = value; OnPropertyChanged(); } }
    public bool VerifyTls { get => _s.VerifyTls; set { _s.VerifyTls = value; OnPropertyChanged(); } }
    public bool IncludeNsfw { get => _s.IncludeNsfw; set { _s.IncludeNsfw = value; OnPropertyChanged(); } }
    public bool KeepChannelsWithoutStreams { get => _s.KeepChannelsWithoutStreams; set { _s.KeepChannelsWithoutStreams = value; OnPropertyChanged(); } }
    public int RecheckCooldownSeconds { get => _s.RecheckCooldownSeconds; set { _s.RecheckCooldownSeconds = value; OnPropertyChanged(); } }
    public int PlayerNetworkCachingMs { get => _s.PlayerNetworkCachingMs; set { _s.PlayerNetworkCachingMs = value; OnPropertyChanged(); } }
    public string GithubToken { get => _s.GithubToken ?? ""; set { _s.GithubToken = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); } }

    public string DataFolder => _host.Store.DataDir;

    public static IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];

    /// <summary>Applied and remembered immediately (no Save needed), so the user can preview themes.</summary>
    public string Theme
    {
        get => _s.Theme;
        set
        {
            var theme = Themes.Contains(value) ? value : "System";
            if (_s.Theme == theme) return;
            _s.Theme = theme;
            OnPropertyChanged();
            _ui.ApplyTheme(theme);
            var current = _host.Settings;
            current.Theme = theme;
            _host.SaveSettings(current);
        }
    }

    private static string? Range(string label, int v, int lo, int hi) => v < lo || v > hi ? $"{label} must be between {lo} and {hi}." : null;

    public bool Save()
    {
        var error = Range("Sync interval (minutes)", SyncIntervalMinutes, 1, 10080)
                    ?? Range("Concurrent stream checks", CheckConcurrency, 1, 500)
                    ?? Range("Check timeout (s)", CheckTimeoutSeconds, 2, 120)
                    ?? Range("Max checks per sync", MaxChecksPerCycle, 0, 1_000_000)
                    ?? Range("Re-check cooldown (s)", RecheckCooldownSeconds, 0, 3600)
                    ?? Range("Player network caching (ms)", PlayerNetworkCachingMs, 0, 60000);
        if (error is not null) { _ui.ShowError(error); return false; }
        // Keep UI state the main window saved meanwhile.
        var current = _host.Settings;
        _s.StatusFilter = current.StatusFilter;
        _s.Sort1 = current.Sort1;
        _s.Sort2 = current.Sort2;
        _s.SortDescending = current.SortDescending;
        _s.Volume = current.Volume;
        _s.PageSize = current.PageSize;
        _s.SearchText = current.SearchText;
        _s.CategoryFilter = [.. current.CategoryFilter ?? []];
        _s.LanguageFilter = [.. current.LanguageFilter ?? []];
        _s.CountryFilter = [.. current.CountryFilter ?? []];
        _host.SaveSettings(_s);
        _ui.ShowInfo("Settings saved. A new sync interval takes effect within 30 seconds.");
        return true;
    }
}

// ====================================================================== History tab
public sealed record SyncRunRow(string Started, string Finished, string Trigger, string Status, string Channels,
    string Streams, string Checked, string Errors);

public sealed class HistoryViewModel : ObservableObject
{
    private readonly AppHost _host;

    public HistoryViewModel(AppHost host, IUiServices ui)
    {
        _host = host;
        OpenLogCommand = new RelayCommand(() => ui.Open(host.Store.LogPath));
        ClearLogCommand = new RelayCommand(() =>
        {
            if (!ui.Confirm("Clear the live log and empty the log file (openiptv.log)?")) return;
            try
            {
                host.Store.ClearLog();
                LogLines.Clear();
            }
            catch (Exception ex)
            {
                ui.ShowError("Could not clear the log file: " + ex.Message);
            }
        });
        ClearHistoryCommand = new RelayCommand(() =>
        {
            if (!ui.Confirm("Delete all sync history entries?")) return;
            try
            {
                host.Agent.ClearHistory();
            }
            catch (Exception ex)
            {
                ui.ShowError("Could not clear the sync history: " + ex.Message);
            }
            Reload();
        }, () => Runs.Count > 0);
        host.Agent.Log += line => ui.Post(() =>
        {
            LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
        });
        Reload();
    }

    public ObservableCollection<SyncRunRow> Runs { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    public void Reload()
    {
        Runs.Clear();
        foreach (var r in _host.Agent.HistorySnapshot())
        {
            var errors = string.Join("; ", r.SourceErrors.Select(kv => $"{kv.Key}: {kv.Value}"));
            if (r.Error is not null) errors = string.IsNullOrEmpty(errors) ? r.Error : errors + "; " + r.Error;
            Runs.Add(new SyncRunRow(
                r.Started.ToString("yyyy-MM-dd HH:mm:ss"),
                r.Finished?.ToString("HH:mm:ss") ?? "",
                r.Trigger, r.Status, r.Channels.ToString("N0"), r.Streams.ToString("N0"),
                r.Checked > 0 ? $"{r.ActiveStreams:N0}/{r.Checked:N0} active" : "",
                errors));
        }
        ClearHistoryCommand?.RaiseCanExecuteChanged();
    }
}
