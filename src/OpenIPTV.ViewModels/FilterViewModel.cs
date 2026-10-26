using System.Collections.ObjectModel;
using OpenIPTV.Core.Services;

namespace OpenIPTV.ViewModels;

public sealed class FilterOption(string value, string label, int count) : ObservableObject
{
    private bool _isSelected;
    private int _count = count;

    public string Value { get; } = value;
    public string Label { get; } = label;
    public int Count { get => _count; set { if (Set(ref _count, value)) OnPropertyChanged(nameof(Display)); } }
    public string Display => $"{Label}  ({Count:N0})";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) SelectionChanged?.Invoke(); }
    }

    internal event Action? SelectionChanged;
}

/// <summary>A multi-select filter (Category, Language or Country) with its own search box.</summary>
public sealed class FilterViewModel : ObservableObject
{
    private readonly List<FilterOption> _all = [];
    private string _search = "";
    private bool _suppress;

    public FilterViewModel(string title, string kind)
    {
        Title = title;
        Kind = kind;
        ClearCommand = new RelayCommand(Clear);
    }

    public string Title { get; }
    public string Kind { get; }

    /// <summary>Options matching the search box; selected ones first.</summary>
    public ObservableCollection<FilterOption> Visible { get; } = [];

    public RelayCommand ClearCommand { get; }

    public event Action? Changed;

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) RebuildVisible(); }
    }

    public IReadOnlyList<string> SelectedValues => _all.Where(o => o.IsSelected).Select(o => o.Value).ToList();

    /// <summary>Button text, e.g. "Country: All", "Country: Singapore", "Country: 3 selected".</summary>
    public string Summary
    {
        get
        {
            var sel = _all.Where(o => o.IsSelected).ToList();
            return sel.Count switch
            {
                0 => $"{Title}: All",
                1 => $"{Title}: {sel[0].Label}",
                2 => $"{Title}: {sel[0].Label}, {sel[1].Label}",
                _ => $"{Title}: {sel.Count} selected",
            };
        }
    }

    public bool HasSelection => _all.Any(o => o.IsSelected);

    private List<string>? _restore;

    /// <summary>Selection saved from the last session; applied by the next <see cref="SetOptions"/>.</summary>
    public void Restore(IEnumerable<string>? values) =>
        _restore = values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

    /// <summary>Replace the options (after a sync) while keeping the user's selection.</summary>
    public void SetOptions(IEnumerable<FacetItem> items)
    {
        var selected = SelectedValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_restore is not null) { selected.UnionWith(_restore); _restore = null; }
        foreach (var o in _all) o.SelectionChanged -= OnSelection;
        _all.Clear();
        _suppress = true;
        foreach (var f in items)
        {
            var o = new FilterOption(f.Value, f.Label, f.Count) { IsSelected = selected.Contains(f.Value) };
            o.SelectionChanged += OnSelection;
            _all.Add(o);
        }
        // Keep selections whose value vanished, so a filter never silently widens.
        foreach (var missing in selected.Where(v => !_all.Any(o => o.Value.Equals(v, StringComparison.OrdinalIgnoreCase))))
        {
            var o = new FilterOption(missing, missing, 0) { IsSelected = true };
            o.SelectionChanged += OnSelection;
            _all.Add(o);
        }
        _suppress = false;
        RebuildVisible();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
    }

    public void Select(params string[] values)
    {
        _suppress = true;
        foreach (var o in _all) o.IsSelected = values.Contains(o.Value, StringComparer.OrdinalIgnoreCase);
        _suppress = false;
        OnSelection();
    }

    public void Clear()
    {
        if (!HasSelection) return;
        _suppress = true;
        foreach (var o in _all) o.IsSelected = false;
        _suppress = false;
        OnSelection();
    }

    private void OnSelection()
    {
        if (_suppress) return;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
        Changed?.Invoke();
    }

    private void RebuildVisible()
    {
        var s = _search.Trim();
        Visible.Clear();
        foreach (var o in _all.Where(o => s.Length == 0 || o.Label.Contains(s, StringComparison.CurrentCultureIgnoreCase))
                              .OrderByDescending(o => o.IsSelected))
            Visible.Add(o);
    }
}
