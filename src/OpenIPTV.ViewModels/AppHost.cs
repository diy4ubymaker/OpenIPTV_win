using System.Reflection;
using OpenIPTV.Core.Models;
using OpenIPTV.Core.Services;
using OpenIPTV.Core.Storage;

namespace OpenIPTV.ViewModels;

/// <summary>Composition root: storage, catalog, settings, sources and the sync agent.</summary>
public sealed class AppHost : IDisposable
{
    private readonly object _lock = new();
    private AppSettings _settings;
    private List<SourceConfig> _sources;

    public JsonStore Store { get; }
    public CatalogService Catalog { get; }
    public SyncAgent Agent { get; }

    public static string Version =>
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public AppHost(string? dataDir = null)
    {
        Store = new JsonStore(dataDir ?? JsonStore.DefaultDataDir());
        _settings = Store.LoadSettings();
        _sources = Store.LoadSources();
        Catalog = new CatalogService(Store);
        Catalog.Load();
        Agent = new SyncAgent(Store, Catalog, () => Settings, () => Sources, SaveSources);
        Store.AppendLog($"OpenIPTV {Version} started, data folder {Store.DataDir}");
    }

    /// <summary>Copy of the current settings (callers may not mutate shared state).</summary>
    public AppSettings Settings { get { lock (_lock) return _settings.Clone(); } }

    public void SaveSettings(AppSettings s)
    {
        lock (_lock) _settings = s.Clone();
        Store.SaveSettings(s);
    }

    /// <summary>Copy of the source list.</summary>
    public List<SourceConfig> Sources { get { lock (_lock) return _sources.Select(x => x.Clone()).ToList(); } }

    public void SaveSources(List<SourceConfig> sources)
    {
        lock (_lock) _sources = sources.Select(x => x.Clone()).ToList();
        Store.SaveSources(sources);
        SourcesChanged?.Invoke();
    }

    public event Action? SourcesChanged;

    public void Start() => Agent.Start();

    public void Dispose()
    {
        Agent.Dispose();
        Catalog.Save();
    }
}
