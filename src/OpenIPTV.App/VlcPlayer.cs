using System.IO;
using LibVLCSharp.Shared;
using OpenIPTV.ViewModels;

namespace OpenIPTV.App;

/// <summary>
/// IPlayer implemented with LibVLC. All MediaPlayer calls run on one background queue:
/// LibVLC must never be called from its own event callbacks, and Stop() can block on a hung stream.
/// </summary>
public sealed class VlcPlayer : IPlayer, IDisposable
{
    private readonly Action<string> _log;
    private readonly object _queueLock = new();
    private Task _queue = Task.CompletedTask;
    private Media? _media;
    private int _volume = 80;
    private bool _mute;

    public LibVLC? LibVlc { get; }
    public MediaPlayer? MediaPlayer { get; }
    public string? InitError { get; }

    public event Action<PlayerState, string?>? StateChanged;

    public VlcPlayer(Action<string> log)
    {
        _log = log;
        try
        {
            // Fully qualified: inside namespace OpenIPTV.App, "Core" would mean the OpenIPTV.Core project.
            LibVLCSharp.Shared.Core.Initialize(FindLibVlcFolder());
            LibVlc = new LibVLC("--no-video-title-show", "--no-osd", "--quiet");
            MediaPlayer = new MediaPlayer(LibVlc) { EnableHardwareDecoding = true };
            MediaPlayer.Opening += (_, _) => Raise(PlayerState.Opening);
            MediaPlayer.Buffering += (_, e) => Raise(e.Cache >= 100f ? PlayerState.Playing : PlayerState.Buffering,
                e.Cache >= 100f ? null : $"Buffering {e.Cache:0}% …");
            MediaPlayer.Playing += (_, _) => Raise(PlayerState.Playing);
            MediaPlayer.Paused += (_, _) => Raise(PlayerState.Paused);
            MediaPlayer.Stopped += (_, _) => Raise(PlayerState.Stopped);
            MediaPlayer.EndReached += (_, _) => Raise(PlayerState.Ended);
            MediaPlayer.EncounteredError += (_, _) => Raise(PlayerState.Error);
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            _log("VLC engine failed to start: " + ex);
        }
    }

    /// <summary>libvlc\win-x64 next to the app (or inside the single-file extraction folder).</summary>
    private static string? FindLibVlcFolder()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "libvlc", "win-x64"),
        };
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "libvlc.dll")));
    }

    private void Raise(PlayerState state, string? message = null) => StateChanged?.Invoke(state, message);

    private void Enqueue(Action action)
    {
        lock (_queueLock)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { _log("Player: " + ex.Message); Raise(PlayerState.Error, ex.Message); }
            }, TaskScheduler.Default);
        }
    }

    public void Play(string url, string? referrer, string? userAgent, int networkCachingMs)
    {
        if (MediaPlayer is null || LibVlc is null)
        {
            Raise(PlayerState.Error, "The VLC engine is not available: " + InitError);
            return;
        }
        Raise(PlayerState.Opening);
        Enqueue(() =>
        {
            var options = new List<string> { $":network-caching={Math.Max(0, networkCachingMs)}" };
            if (!string.IsNullOrWhiteSpace(referrer)) options.Add($":http-referrer={referrer}");
            if (!string.IsNullOrWhiteSpace(userAgent)) options.Add($":http-user-agent={userAgent}");
            var media = new Media(LibVlc, url, FromType.FromLocation, options.ToArray());
            var old = _media;
            _media = media;
            MediaPlayer.Play(media);
            MediaPlayer.Volume = _mute ? 0 : _volume;
            old?.Dispose();
        });
    }

    public void Stop()
    {
        if (MediaPlayer is null) return;
        Enqueue(() => MediaPlayer.Stop());
    }

    public void TogglePause()
    {
        if (MediaPlayer is null) return;
        Enqueue(() => { if (MediaPlayer.CanPause) MediaPlayer.Pause(); });
    }

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            if (MediaPlayer is { } mp && !_mute) Enqueue(() => mp.Volume = _volume);
        }
    }

    public bool Mute
    {
        get => _mute;
        set
        {
            _mute = value;
            if (MediaPlayer is { } mp) Enqueue(() => mp.Volume = _mute ? 0 : _volume);
        }
    }

    public void Dispose()
    {
        try
        {
            MediaPlayer?.Stop();
            _queue.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* shutting down */ }
        MediaPlayer?.Dispose();
        _media?.Dispose();
        LibVlc?.Dispose();
    }
}
