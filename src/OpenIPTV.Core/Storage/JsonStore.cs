using System.Text.Json;
using System.Text.Json.Serialization;
using OpenIPTV.Core.Models;

namespace OpenIPTV.Core.Storage;

/// <summary>
/// Local persistence as JSON files in the data folder:
/// catalog.json (channels + streams + status), sources.json, settings.json, history.json, openiptv.log.
/// Writes are atomic (temp file + replace) so a crash never leaves a half-written file.
/// </summary>
public sealed class JsonStore
{
    public string DataDir { get; }
    private readonly object _ioLock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };

    public JsonStore(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        Directory.CreateDirectory(DataDir);
    }

    /// <summary>
    /// OPENIPTV_DATA_DIR if set; else a "data" folder next to the executable when one exists (portable mode);
    /// else %LOCALAPPDATA%\OpenIPTV.
    /// </summary>
    public static string DefaultDataDir()
    {
        var env = Environment.GetEnvironmentVariable("OPENIPTV_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        var portable = Path.Combine(exeDir, "data");
        if (Directory.Exists(portable)) return portable;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenIPTV");
    }

    private string P(string name) => Path.Combine(DataDir, name);

    private T Load<T>(string name, Func<T> fallback)
    {
        lock (_ioLock)
        {
            var path = P(name);
            if (!File.Exists(path)) return fallback();
            try
            {
                using var fs = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(fs, Options) ?? fallback();
            }
            catch (Exception ex)
            {
                // Keep the unreadable file for inspection and start clean.
                try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* best effort */ }
                AppendLog($"Could not read {name}: {ex.Message} — starting with defaults.");
                return fallback();
            }
        }
    }

    private void Save<T>(string name, T value, bool pretty = false)
    {
        lock (_ioLock)
        {
            var path = P(name);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, value, pretty ? Pretty : Options);
            File.Move(tmp, path, overwrite: true);
        }
    }

    public Catalog LoadCatalog()
    {
        var c = Load(Files.Catalog, () => new Catalog());
        // Re-create dictionaries with the intended comparers (JSON deserialisation uses the default one).
        return new Catalog
        {
            Channels = new Dictionary<string, Channel>(c.Channels, StringComparer.Ordinal),
            Countries = new Dictionary<string, string>(c.Countries, StringComparer.OrdinalIgnoreCase),
            Languages = new Dictionary<string, string>(c.Languages, StringComparer.OrdinalIgnoreCase),
            Categories = new Dictionary<string, string>(c.Categories, StringComparer.OrdinalIgnoreCase),
            LastSync = c.LastSync,
        };
    }

    public void SaveCatalog(Catalog catalog) => Save(Files.Catalog, catalog);

    public List<SourceConfig> LoadSources()
    {
        var list = Load(Files.Sources, SourceConfig.Defaults);
        foreach (var s in list) s.Options = new Dictionary<string, string>(s.Options, StringComparer.OrdinalIgnoreCase);
        return list.Count > 0 ? list : SourceConfig.Defaults();
    }

    public void SaveSources(IEnumerable<SourceConfig> sources) => Save(Files.Sources, sources.ToList(), pretty: true);

    public AppSettings LoadSettings() => Load(Files.Settings, () => new AppSettings());

    public void SaveSettings(AppSettings settings) => Save(Files.Settings, settings, pretty: true);

    public List<SyncRun> LoadHistory() => Load(Files.History, () => new List<SyncRun>());

    public void SaveHistory(IEnumerable<SyncRun> runs) => Save(Files.History, runs.Take(100).ToList(), pretty: true);

    public string LogPath => P(Files.Log);

    public void AppendLog(string line)
    {
        lock (_ioLock)
        {
            try
            {
                var path = P(Files.Log);
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { /* logging must never break the app */ }
        }
    }

    /// <summary>Empty the log file and delete the rotated copy (openiptv.log.1).</summary>
    public void ClearLog()
    {
        lock (_ioLock)
        {
            var path = P(Files.Log);
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Log cleared{Environment.NewLine}");
            if (File.Exists(path + ".1")) File.Delete(path + ".1");
        }
    }

    public static class Files
    {
        public const string Catalog = "catalog.json";
        public const string Sources = "sources.json";
        public const string Settings = "settings.json";
        public const string History = "history.json";
        public const string Log = "openiptv.log";
    }
}
