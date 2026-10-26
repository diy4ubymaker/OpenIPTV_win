using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenIPTV.ViewModels;

namespace OpenIPTV.App;

/// <summary>IUiServices for WPF.</summary>
public sealed class WpfUiServices(Dispatcher dispatcher) : IUiServices
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public void CopyText(string text)
    {
        // The clipboard can be briefly locked by another app; retry a few times.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
        }
    }

    public string? AskSaveFile(string title, string defaultFileName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = filter + "|All files|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dlg.ShowDialog(Application.Current.MainWindow) == true ? dlg.FileName : null;
    }

    public void Open(string pathOrUrl)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError($"Could not open {pathOrUrl}: {ex.Message}"); }
    }

    public void ShowInfo(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "OpenIPTV", MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "OpenIPTV", MessageBoxButton.OK, MessageBoxImage.Warning);

    private string _theme = "System";

    /// <summary>
    /// Switch WPF's Fluent theme (ThemeMode) and the app's own panel colour. "System" follows Windows,
    /// including when the user changes the Windows setting while the app is running.
    /// </summary>
    public void ApplyTheme(string theme)
    {
        _theme = theme is "Light" or "Dark" ? theme : "System";
        var app = Application.Current;
        app.ThemeMode = _theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        bool dark = _theme == "Dark" || (_theme == "System" && WindowsUsesDarkTheme());
        app.Resources["PanelBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
    }

    /// <summary>Re-apply when Windows switches light/dark while "System" is selected.</summary>
    public void FollowWindowsTheme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _theme == "System")
                dispatcher.BeginInvoke(new Action(() => ApplyTheme("System")));
        };
    }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool Confirm(string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, "OpenIPTV", MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;
}
