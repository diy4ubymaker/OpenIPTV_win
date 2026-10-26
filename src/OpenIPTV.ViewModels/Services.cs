namespace OpenIPTV.ViewModels;

/// <summary>What the view models need from the UI framework (implemented by the WPF app; faked in tests).</summary>
public interface IUiServices
{
    /// <summary>Run on the UI thread (asynchronously).</summary>
    void Post(Action action);
    void CopyText(string text);
    /// <summary>Returns the chosen path, or null if cancelled. Filter uses the WPF format "M3U playlist|*.m3u".</summary>
    string? AskSaveFile(string title, string defaultFileName, string filter);
    void Open(string pathOrUrl);
    void ShowInfo(string message);
    void ShowError(string message);
    bool Confirm(string message);
    /// <summary>Switch the colour theme: "System", "Light" or "Dark".</summary>
    void ApplyTheme(string theme);
}

public enum PlayerState { Idle, Opening, Buffering, Playing, Paused, Stopped, Ended, Error }

/// <summary>Video player abstraction (LibVLC in the WPF app).</summary>
public interface IPlayer
{
    void Play(string url, string? referrer, string? userAgent, int networkCachingMs);
    void Stop();
    void TogglePause();
    int Volume { get; set; }
    bool Mute { get; set; }
    /// <summary>Raised on any thread.</summary>
    event Action<PlayerState, string?>? StateChanged;
}
