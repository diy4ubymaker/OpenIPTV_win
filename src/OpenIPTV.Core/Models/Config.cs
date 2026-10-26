namespace OpenIPTV.Core.Models;

/// <summary>Kinds of channel sources the app can harvest.</summary>
public static class SourceKinds
{
    public const string IptvApi = "iptv_api";
    public const string DatabaseCsv = "database_csv";
    public const string M3u = "m3u";
    public const string PlaylistsMd = "playlists_md";
    public const string GithubRepo = "github_repo";

    public static readonly string[] All = [IptvApi, DatabaseCsv, M3u, PlaylistsMd, GithubRepo];

    /// <summary>Priority for scalar fields when sources disagree (higher wins).</summary>
    public static int Priority(string kind) => kind switch
    {
        IptvApi => 40,
        DatabaseCsv => 30,
        GithubRepo or M3u => 20,
        PlaylistsMd => 10,
        _ => 0,
    };

    /// <summary>Harvest order: reference data first so playlists can map category names.</summary>
    public static int Order(string kind) => Array.IndexOf(new[] { IptvApi, DatabaseCsv, GithubRepo, M3u, PlaylistsMd }, kind) switch
    {
        -1 => 99,
        var i => i,
    };
}

/// <summary>A configurable channel source.</summary>
public sealed class SourceConfig
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = SourceKinds.M3u;
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Kind-specific options, e.g. follow=categories,languages,countries (playlists_md) or branch/path (github_repo).</summary>
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime? LastRun { get; set; }
    public string? LastStatus { get; set; }

    public string Option(string key, string fallback) =>
        Options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    public SourceConfig Clone() => new()
    {
        Name = Name, Kind = Kind, Url = Url, Enabled = Enabled,
        Options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase),
        LastRun = LastRun, LastStatus = LastStatus,
    };

    public static List<SourceConfig> Defaults() =>
    [
        new() { Name = "iptv-org API", Kind = SourceKinds.IptvApi, Url = "https://iptv-org.github.io/api" },
        new() { Name = "iptv-org database (CSV)", Kind = SourceKinds.DatabaseCsv,
                Url = "https://github.com/iptv-org/database/tree/master/data" },
        new() { Name = "Main playlist index.m3u", Kind = SourceKinds.M3u, Url = "https://iptv-org.github.io/iptv/index.m3u" },
        new() { Name = "PLAYLISTS.md grouped playlists", Kind = SourceKinds.PlaylistsMd,
                Url = "https://github.com/iptv-org/iptv/blob/master/PLAYLISTS.md",
                Options = new(StringComparer.OrdinalIgnoreCase) { ["follow"] = "categories,languages,countries" } },
        new() { Name = "iptv-org/iptv repository streams/", Kind = SourceKinds.GithubRepo,
                Url = "https://github.com/iptv-org/iptv",
                Options = new(StringComparer.OrdinalIgnoreCase) { ["branch"] = "master", ["path"] = "streams" } },
    ];
}

/// <summary>User settings; persisted as settings.json.</summary>
public sealed class AppSettings
{
    public int SyncIntervalMinutes { get; set; } = 60;
    public bool SyncOnStartup { get; set; } = true;
    public bool CheckStreamsOnSync { get; set; } = true;
    public int CheckConcurrency { get; set; } = 40;
    public int CheckTimeoutSeconds { get; set; } = 10;
    public int MaxChecksPerCycle { get; set; } = 0;          // 0 = all
    public bool VerifyTls { get; set; } = true;
    public bool IncludeNsfw { get; set; }
    public bool KeepChannelsWithoutStreams { get; set; }
    public int RecheckCooldownSeconds { get; set; } = 30;
    public int PlayerNetworkCachingMs { get; set; } = 1500;

    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public string? GithubToken { get; set; }

    // Last-used UI state (restored on start).
    public string StatusFilter { get; set; } = "Active";
    public string Sort1 { get; set; } = "Name";
    public string Sort2 { get; set; } = "";
    public bool SortDescending { get; set; }
    public int Volume { get; set; } = 80;
    public int PageSize { get; set; } = 100;
    public string SearchText { get; set; } = "";
    public List<string> CategoryFilter { get; set; } = [];
    public List<string> LanguageFilter { get; set; } = [];
    public List<string> CountryFilter { get; set; } = [];

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.CategoryFilter = [.. CategoryFilter ?? []];
        c.LanguageFilter = [.. LanguageFilter ?? []];
        c.CountryFilter = [.. CountryFilter ?? []];
        return c;
    }
}

/// <summary>One sync cycle, shown in the history tab; persisted as history.json.</summary>
public sealed class SyncRun
{
    public string Trigger { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime? Finished { get; set; }
    public string Status { get; set; } = "running";
    public int Channels { get; set; }
    public int Streams { get; set; }
    public int Checked { get; set; }
    public int ActiveStreams { get; set; }
    public int Removed { get; set; }
    public bool Pruned { get; set; }
    public Dictionary<string, string> SourceErrors { get; set; } = [];
    public Dictionary<string, string> SourceStats { get; set; } = [];
    public string? Error { get; set; }
}
