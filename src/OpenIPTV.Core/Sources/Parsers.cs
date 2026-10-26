using System.Text;
using System.Text.RegularExpressions;

namespace OpenIPTV.Core.Sources;

public static partial class UrlUtil
{
    [GeneratedRegex(@"^https?://github\.com/([^/]+)/([^/]+)/(blob|tree|raw)/([^/]+)/?(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex GithubWeb();

    [GeneratedRegex(@"^https?://github\.com/([^/]+)/([^/#?]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GithubRepo();

    /// <summary>github.com/o/r/blob|tree/branch/path → raw.githubusercontent.com/o/r/branch/path; trims a trailing slash.</summary>
    public static string Normalize(string url)
    {
        url = url.Trim();
        var m = GithubWeb().Match(url);
        if (m.Success)
        {
            var path = m.Groups[5].Value.TrimEnd('/');
            return $"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[4].Value}"
                   + (path.Length > 0 ? "/" + path : "");
        }
        return url.TrimEnd('/');
    }

    public static (string Owner, string Repo) ParseGithubRepo(string url)
    {
        var m = GithubRepo().Match(url.Trim());
        if (!m.Success) throw new ArgumentException($"Not a GitHub repository URL: {url}");
        var repo = m.Groups[2].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return (m.Groups[1].Value, repo);
    }

    public static string Resolve(string baseUrl, string relative)
    {
        if (Uri.TryCreate(relative, UriKind.Absolute, out var abs) && !abs.IsFile) return abs.ToString();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && Uri.TryCreate(b, relative, out var r)) return r.ToString();
        return relative;
    }

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "unnamed" : s;
    }
}

/// <summary>One #EXTINF entry of an extended M3U playlist.</summary>
public sealed class M3uEntry
{
    public Dictionary<string, string> Attrs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Title { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Quality { get; set; }
    public HashSet<string> Labels { get; } = [];
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string Url { get; set; } = "";

    public string Attr(string key) => Attrs.TryGetValue(key, out var v) ? v : "";
}

public static partial class M3uParser
{
    [GeneratedRegex(@"([\w-]+)=""([^""]*)""")]
    private static partial Regex AttrRx();

    [GeneratedRegex(@"\s*\((\d{3,4}[pi])\)")]
    private static partial Regex QualityRx();

    [GeneratedRegex(@"\s*\[([^\]]+)\]")]
    private static partial Regex TagRx();

    public static List<M3uEntry> Parse(string text, string baseUrl = "")
    {
        var entries = new List<M3uEntry>();
        M3uEntry? cur = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                cur = new M3uEntry();
                foreach (Match m in AttrRx().Matches(line)) cur.Attrs[m.Groups[1].Value] = m.Groups[2].Value;
                cur.Title = TitleAfterAttrs(line);
                var q = QualityRx().Match(cur.Title);
                if (q.Success) cur.Quality = q.Groups[1].Value;
                foreach (Match t in TagRx().Matches(cur.Title)) cur.Labels.Add(t.Groups[1].Value);
                cur.Name = TagRx().Replace(QualityRx().Replace(cur.Title, ""), "").Trim();
                cur.Referrer = NullIfEmpty(cur.Attr("http-referrer"));
                cur.UserAgent = NullIfEmpty(cur.Attr("http-user-agent"));
            }
            else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                if (cur is null) continue;
                var opt = line["#EXTVLCOPT:".Length..];
                var eq = opt.IndexOf('=');
                if (eq < 0) continue;
                var key = opt[..eq].Trim().ToLowerInvariant();
                var val = opt[(eq + 1)..].Trim();
                if (key == "http-referrer") cur.Referrer = val;
                else if (key == "http-user-agent") cur.UserAgent = val;
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }
            else
            {
                cur ??= new M3uEntry { Title = line, Name = line };
                cur.Url = baseUrl.Length > 0 ? UrlUtil.Resolve(baseUrl, line) : line;
                entries.Add(cur);
                cur = null;
            }
        }
        return entries;
    }

    /// <summary>Display name follows the first comma outside quoted attribute values.</summary>
    private static string TitleAfterAttrs(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes) return line[(i + 1)..].Trim();
        }
        return "";
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public static partial class PlaylistsMdParser
{
    [GeneratedRegex(@"(https?://[^\s<>`""')]+/(categories|languages|countries)/([A-Za-z0-9_-]+)\.m3u)")]
    private static partial Regex LinkRx();

    /// <summary>(group, key, url) for the per-category/language/country playlists linked from PLAYLISTS.md.</summary>
    public static List<(string Group, string Key, string Url)> Parse(string text)
    {
        var seen = new HashSet<string>();
        var result = new List<(string, string, string)>();
        foreach (Match m in LinkRx().Matches(text))
        {
            var url = m.Groups[1].Value;
            if (seen.Add(url)) result.Add((m.Groups[2].Value, m.Groups[3].Value, url));
        }
        return result;
    }
}

/// <summary>Minimal RFC 4180 CSV reader (quoted fields, escaped quotes, newlines inside quotes).</summary>
public static class Csv
{
    public static List<Dictionary<string, string?>> ReadRecords(string text)
    {
        var rows = ReadRows(text);
        var result = new List<Dictionary<string, string?>>();
        if (rows.Count == 0) return result;
        var header = rows[0].Select(h => h.Trim().TrimStart('﻿')).ToArray();
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 1 && row[0].Length == 0) continue;
            var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < header.Length; c++) d[header[c]] = c < row.Count && row[c].Length > 0 ? row[c] : null;
            result.Add(d);
        }
        return result;
    }

    public static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = [];
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
