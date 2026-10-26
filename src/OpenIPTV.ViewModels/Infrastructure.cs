using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OpenIPTV.ViewModels;

/// <summary>Base class with property-change notification.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Synchronous command.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Async command that disables itself while running and reports errors through a callback.</summary>
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => _running;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        catch (Exception ex) { onError?.Invoke(ex); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Runs an action once after calls stop arriving for <paramref name="delay"/>; always on the UI thread.</summary>
public sealed class Debouncer(IUiServices ui, TimeSpan delay, Action action) : IDisposable
{
    private Timer? _timer;
    private readonly object _lock = new();

    public void Trigger()
    {
        if (delay <= TimeSpan.Zero) { ui.Post(action); return; }
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => ui.Post(action), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock) _timer?.Dispose();
    }
}
