using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using OpenIPTV.ViewModels;

namespace OpenIPTV.App;

public partial class App : Application
{
    /// <summary>Keep the splash up at least this long so it does not just flash on fast machines.</summary>
    private static readonly TimeSpan MinimumSplash = TimeSpan.FromMilliseconds(1200);

    private AppHost? _host;
    private VlcPlayer? _player;
    private MainViewModel? _vm;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _host?.Store.AppendLog("Unhandled (background): " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _host?.Store.AppendLog("Unobserved task exception: " + args.Exception);
            args.SetObserved();
        };

        // The splash is the first window; don't let closing it end the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var clock = Stopwatch.StartNew();
        var splash = new SplashWindow(AppHost.Version);
        splash.Show();

        try
        {
            // Heavy work runs off the UI thread so the splash keeps animating.
            splash.Report("Opening data folder and loading channels …", 15);
            _host = await Task.Run(() => new AppHost());

            var ui = new WpfUiServices(Dispatcher);
            ui.ApplyTheme(_host.Settings.Theme);
            ui.FollowWindowsTheme();

            var totals = _host.Catalog.Totals();
            splash.Report(totals.Total > 0
                ? $"Loaded {totals.Total:N0} channels ({totals.Active:N0} active)"
                : "First start: channels will be downloaded in the background", 45);

            splash.Report("Starting the VLC playback engine …", 55);
            var log = _host.Store.AppendLog;
            _player = await Task.Run(() => new VlcPlayer(log));
            if (_player.InitError is not null)
                splash.Report("VLC engine unavailable — channels can still be browsed", 70);

            splash.Report("Preparing the window …", 80);
            await Dispatcher.Yield(DispatcherPriority.Background);   // let the splash repaint before the heavy XAML load
            _vm = new MainViewModel(_host, ui, _player);
            var window = new MainWindow(_vm, _player);

            splash.Report("Ready", 100);
            var remaining = MinimumSplash - clock.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            window.Activate();
            await splash.FadeOutAndCloseAsync();
            _host.Start();
        }
        catch (Exception ex)
        {
            _host?.Store.AppendLog("Start-up failed: " + ex);
            splash.Close();
            MessageBox.Show($"OpenIPTV could not start:\n\n{ex.Message}", "OpenIPTV",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _vm?.Dispose(); } catch { /* shutting down */ }
        try { _player?.Dispose(); } catch { /* shutting down */ }
        try { _host?.Dispose(); } catch { /* shutting down */ }
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Store.AppendLog("Unhandled: " + e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails are in openiptv.log in the data folder.",
            "OpenIPTV", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
