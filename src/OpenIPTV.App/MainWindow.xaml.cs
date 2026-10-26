using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenIPTV.Core.Services;
using OpenIPTV.ViewModels;

namespace OpenIPTV.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;
    private GridLength _savedListWidth;
    private Thickness _savedMargin;

    public MainWindow(MainViewModel vm, VlcPlayer player)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // The overlay lives in a separate transparent window above the video; give it the data context explicitly.
        VideoOverlay.DataContext = vm;

        Loaded += (_, _) =>
        {
            if (player.MediaPlayer is not null) Video.MediaPlayer = player.MediaPlayer;
            SearchBox.Focus();
            UpdateSortGlyphs();
        };
        vm.PropertyChanged += OnViewModelChanged;
        ((INotifyCollectionChanged)vm.History.LogLines).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            // Scroll after the ListBox has processed this change: touching its Items from inside the
            // collection's own CollectionChanged event makes WPF report "inconsistent with its items source".
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (LogList.IsVisible && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            }));
        };
    }

    // ------------------------------------------------------------------ navigation
    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        // Raised once during InitializeComponent, before the pages exist.
        if (ChannelsPage is null || SourcesPage is null || SettingsPage is null || HistoryPage is null) return;
        bool channels = NavChannels.IsChecked == true;
        ChannelsPage.Visibility = channels ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlay.Visibility = channels ? Visibility.Visible : Visibility.Collapsed;
        SourcesPage.Visibility = NavSources.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = NavHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (!channels && _vm.IsFullscreen) _vm.IsFullscreen = false;
    }

    // ------------------------------------------------------------------ channel list
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows (not headers or the scroll bar) start playback.
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is ChannelRow row)
            _vm.PlayRow(row);
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ChannelGrid.SelectedItem is ChannelRow row)
        {
            _vm.PlayRow(row);
            e.Handled = true;   // DataGrid would otherwise move to the next row
        }
    }

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        // Enter jumps to the typed page (the binding otherwise updates only when the box loses focus).
        if (e.Key != Key.Enter) return;
        PageBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        PageBox.SelectAll();
        e.Handled = true;
    }

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        // Sort every matching channel (all pages) in the view model, not just the rows on this page.
        e.Handled = true;
        if (!string.IsNullOrEmpty(e.Column.SortMemberPath)) _vm.SortByColumn(e.Column.SortMemberPath);
    }

    /// <summary>Show the ▲/▼ arrow on the column the list is sorted by.</summary>
    private void UpdateSortGlyphs()
    {
        foreach (var column in ChannelGrid.Columns)
            column.SortDirection = column.SortMemberPath == _vm.Sort1
                ? (_vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
    }

    // ------------------------------------------------------------------ video / full screen
    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) _vm.IsFullscreen = !_vm.IsFullscreen;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsFullscreen)) ApplyFullscreen(_vm.IsFullscreen);
        // DataGrid clears the header arrows whenever its rows are replaced, so redraw them afterwards.
        if (e.PropertyName is nameof(MainViewModel.Sort1) or nameof(MainViewModel.SortDescending) or nameof(MainViewModel.Rows))
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateSortGlyphs));
    }

    private void ApplyFullscreen(bool on)
    {
        if (on)
        {
            if (NavChannels.IsChecked != true) NavChannels.IsChecked = true;
            _savedState = WindowState;
            _savedStyle = WindowStyle;
            _savedResize = ResizeMode;
            _savedListWidth = ListColumn.Width;
            _savedMargin = ChannelsPage.Margin;
            HeaderBar.Visibility = Visibility.Collapsed;
            DetailsPane.Visibility = Visibility.Collapsed;
            LeftPane.Visibility = Visibility.Collapsed;
            Splitter.Visibility = Visibility.Collapsed;
            ListColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            ChannelsPage.Margin = new Thickness(0);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;  // re-maximise to cover the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _savedStyle == WindowStyle.None ? WindowStyle.SingleBorderWindow : _savedStyle;
            ResizeMode = _savedResize == ResizeMode.NoResize ? ResizeMode.CanResize : _savedResize;
            WindowState = _savedState;
            HeaderBar.Visibility = Visibility.Visible;
            DetailsPane.Visibility = Visibility.Visible;
            LeftPane.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
            ListColumn.Width = _savedListWidth.Value > 0 ? _savedListWidth : new GridLength(3, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(6);
            ChannelsPage.Margin = _savedMargin == default ? new Thickness(10) : _savedMargin;
        }
    }

    // ------------------------------------------------------------------ sources page
    private void OnSaveSources(object sender, RoutedEventArgs e)
    {
        // Commit a cell that is still being edited before validating.
        SourcesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _vm.Sources.Save();
    }
}
