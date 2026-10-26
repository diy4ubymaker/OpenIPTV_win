using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using OpenIPTV.Core.Models;

namespace OpenIPTV.Core.Checking;

public readonly record struct CheckResult(bool Ok, string Status);

/// <summary>
/// Tests whether a stream can be played.
/// HTTP(S): GET with the stream's Referer/User-Agent; HLS master → first variant → first segment must return data;
/// other content must return bytes and not be an HTML page.
/// RTMP/RTSP/MMS: TCP connect to the server (reachable = active). UDP/RTP multicast cannot be tested remotely.
/// </summary>
public sealed class StreamChecker : IDisposable
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private const int MaxRead = 512 * 1024;
    private const int SegmentRead = 64 * 1024;
    private const int PerHostLimit = 4;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);

    public StreamChecker(int timeoutSeconds = 10, bool verifyTls = true)
    {
        _timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 120));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = _timeout,
            MaxConnectionsPerServer = PerHostLimit * 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
        };
        if (!verifyTls) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<CheckResult> CheckAsync(StreamInfo s, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(s.Url, UriKind.Absolute, out var uri)) return new(false, "invalid_url");
        var gate = _hostGates.GetOrAdd(uri.Host, _ => new SemaphoreSlim(PerHostLimit));
        await gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout * 2.5);
            var scheme = uri.Scheme.ToLowerInvariant();
            return scheme switch
            {
                "http" or "https" => await CheckHttpAsync(uri, s.Referrer, s.UserAgent, 0, cts.Token),
                "rtmp" or "rtmps" or "rtmpe" or "rtmpt" or "rtsp" or "rtsps" or "mms" or "mmsh" or "srt"
                    => await CheckTcpAsync(uri, scheme, cts.Token),
                _ => new(false, "untestable_protocol"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "timeout"); }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        { return new(false, "tls_error"); }
        catch (HttpRequestException ex) { return new(false, ex.StatusCode is { } sc ? $"http_{(int)sc}" : "connect_error"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, "error:" + ex.GetType().Name); }
        finally { gate.Release(); }
    }

    private HttpRequestMessage Request(Uri uri, string? referrer, string? userAgent, bool segment)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        req.Headers.Accept.ParseAdd("*/*");
        if (!string.IsNullOrWhiteSpace(referrer))
        {
            req.Headers.TryAddWithoutValidation("Referer", referrer);
            if (Uri.TryCreate(referrer, UriKind.Absolute, out var r))
                req.Headers.TryAddWithoutValidation("Origin", $"{r.Scheme}://{r.Authority}");
        }
        if (segment) req.Headers.Range = new RangeHeaderValue(0, SegmentRead - 1);
        return req;
    }

    private async Task<(HttpStatusCode Code, Uri FinalUri, string ContentType, byte[] Body)> ReadHeadAsync(
        Uri uri, string? referrer, string? ua, bool segment, CancellationToken ct)
    {
        using var req = Request(uri, referrer, ua, segment);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var final = resp.RequestMessage?.RequestUri ?? uri;
        var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
        if ((int)resp.StatusCode >= 400) return (resp.StatusCode, final, ctype, []);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var limit = segment ? SegmentRead : MaxRead;
        var buffer = new byte[limit];
        int total = 0;
        while (total < limit)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, limit - total), ct);
            if (n == 0) break;
            total += n;
        }
        return (resp.StatusCode, final, ctype, buffer[..total]);
    }

    public static bool LooksLikePlaylist(byte[] body, string contentType, Uri uri)
    {
        var start = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 64)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        return start.StartsWith("#EXTM3U", StringComparison.Ordinal)
               || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Variant playlist URIs and media segment URIs of an HLS playlist.</summary>
    public static (List<Uri> Variants, List<Uri> Segments) PlaylistUris(string text, Uri baseUri)
    {
        var variants = new List<Uri>();
        var segments = new List<Uri>();
        bool expectVariant = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) { expectVariant = true; continue; }
            if (line.StartsWith('#')) continue;
            if (Uri.TryCreate(baseUri, line, out var u)) (expectVariant ? variants : segments).Add(u);
            expectVariant = false;
        }
        return (variants, segments);
    }

    private async Task<CheckResult> CheckHttpAsync(Uri uri, string? referrer, string? ua, int depth, CancellationToken ct)
    {
        var (code, final, ctype, body) = await ReadHeadAsync(uri, referrer, ua, false, ct);
        if ((int)code >= 400) return new(false, $"http_{(int)code}");
        if (LooksLikePlaylist(body, ctype, final))
        {
            var text = Encoding.UTF8.GetString(body);
            if (!text.AsSpan(0, Math.Min(text.Length, 1024)).Contains("#EXTM3U".AsSpan(), StringComparison.Ordinal))
                return new(false, "not_a_playlist");
            var (variants, segments) = PlaylistUris(text, final);
            if (variants.Count > 0)
                return depth >= 3 ? new(false, "playlist_too_deep") : await CheckHttpAsync(variants[0], referrer, ua, depth + 1, ct);
            if (segments.Count == 0) return new(false, "empty_playlist");
            var seg = await ReadHeadAsync(segments[0], referrer, ua, true, ct);
            if ((int)seg.Code >= 400) return new(false, $"segment_http_{(int)seg.Code}");
            return seg.Body.Length == 0 ? new(false, "segment_empty") : new(true, "ok_hls");
        }
        if (ctype.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return new(false, "html_page");
        return body.Length == 0 ? new(false, "no_data") : new(true, "ok_progressive");
    }

    private static async Task<CheckResult> CheckTcpAsync(Uri uri, string scheme, CancellationToken ct)
    {
        int port = uri.Port > 0 ? uri.Port : scheme switch
        {
            "rtmp" or "rtmpe" => 1935,
            "rtmps" => 443,
            "rtmpt" => 80,
            "rtsp" => 554,
            "rtsps" => 322,
            "mms" => 1755,
            _ => 80,
        };
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, port, ct);
        return new(true, "ok_tcp_reachable");
    }

    /// <summary>
    /// Checks many streams concurrently; <paramref name="onResult"/> is called for every result.
    /// One failing stream (or a failing callback) never stops the others: the error is reported through
    /// <paramref name="onError"/>. Cancellation (app closing) stops quietly and then throws once to the caller.
    /// </summary>
    public async Task CheckManyAsync(IReadOnlyList<StreamInfo> items, int concurrency,
        Action<StreamInfo, CheckResult> onResult, IProgress<(int Done, int Total)>? progress, CancellationToken ct,
        Action<string>? onError = null)
    {
        int done = 0;
        using var slots = new SemaphoreSlim(Math.Clamp(concurrency, 1, 500));

        async Task One(StreamInfo s)
        {
            try { await slots.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                CheckResult r;
                try { r = await CheckAsync(s, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { r = new(false, "error:" + ex.GetType().Name); }

                try { onResult(s, r); }
                catch (Exception ex) { onError?.Invoke($"Saving check result for {s.Url} failed: {ex.Message}"); }

                var d = Interlocked.Increment(ref done);
                if (d % 25 == 0 || d == items.Count) progress?.Report((d, items.Count));
            }
            finally { slots.Release(); }
        }

        await Task.WhenAll(items.Select(One));
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose() => _http.Dispose();
}
