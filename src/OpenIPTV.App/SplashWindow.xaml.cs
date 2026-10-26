using System.Windows;
using System.Windows.Media.Animation;

namespace OpenIPTV.App;

/// <summary>Start-up splash: shows what is loading and fades out when the main window is ready.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"Version {version}";
    }

    /// <summary>Update the status line and move the progress bar smoothly to <paramref name="percent"/>.</summary>
    public void Report(string status, double percent)
    {
        StatusText.Text = status;
        var anim = new DoubleAnimation(Math.Clamp(percent, 0, 100), TimeSpan.FromMilliseconds(250));
        Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }

    /// <summary>Fade out, then close.</summary>
    public Task FadeOutAndCloseAsync()
    {
        var tcs = new TaskCompletionSource();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            Close();
            tcs.TrySetResult();
        };
        BeginAnimation(OpacityProperty, fade);
        return tcs.Task;
    }
}
