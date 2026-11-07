using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Duplicates;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>One transaction in the possible-duplicates grid; the check box is the pending choice, saved in bulk.</summary>
public sealed partial class DuplicateRowItem : ObservableObject
{
    public DuplicateRowItem(DuplicateGroup group, DuplicateCandidateRow row, Action changed)
    {
        _changed = changed;
        Id = row.Id;
        GroupNumber = group.Number;
        GroupText = $"#{group.Number}";
        IsBanded = group.Number % 2 == 0;
        PostedDate = row.PostedAtUtc.Date;
        Account = row.AccountName;
        Institution = row.InstitutionName;
        Description = row.Description;
        Payee = row.Payee;
        Amount = row.Amount;
        Category = row.CategoryPath ?? "";
        Source = row.ExternalId is null ? "Imported" : "Synced";
        Added = row.CreatedAtUtc.ToLocalTime();
        IsSuggestedKeep = row.Id == group.SuggestedKeepId;
        WasMarked = row.IsDuplicate;
        _isMarked = row.IsDuplicate;
    }

    private readonly Action _changed;
    public long Id { get; }
    public int GroupNumber { get; }
    public string GroupText { get; }
    /// <summary>Every other group is tinted, so where one group ends and the next begins is visible.</summary>
    public bool IsBanded { get; }
    public DateTime PostedDate { get; }
    public string Account { get; }
    public string Institution { get; }
    public string Description { get; }
    public string? Payee { get; }
    public decimal Amount { get; }
    public string Category { get; }
    /// <summary>"Synced" (has a provider id) or "Imported" (from a file).</summary>
    public string Source { get; }
    public DateTime Added { get; }
    /// <summary>The row Hoard would keep: "Select suggested" marks the others.</summary>
    public bool IsSuggestedKeep { get; }
    public string KeepHint => IsSuggestedKeep ? "keep" : "";
    /// <summary>The stored flag, so Save writes only what changed.</summary>
    public bool WasMarked { get; private set; }
    public bool IsChanged => IsMarked != WasMarked;

    [ObservableProperty] private bool _isMarked;
    partial void OnIsMarkedChanged(bool value) { OnPropertyChanged(nameof(IsChanged)); _changed(); }

    public void Committed() { WasMarked = IsMarked; OnPropertyChanged(nameof(IsChanged)); }
}

/// <summary>
/// Reports › Possible duplicates: transactions on the same account for the same amount and vendor within a day or two of
/// each other. Tick the copies (or let "Select suggested" tick all but one per group) and save them in one go; "Not
/// duplicates" remembers a group as reviewed so it is not offered again.
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly DuplicateService _service;

    public DuplicatesViewModel(DuplicateService service)
    {
        _service = service;
        _selectedRange = RangeOptions[2];
    }

    /// <summary>Raised after a save, so the other pages reload without the flagged rows.</summary>
    public event Action? Saved;

    public ObservableCollection<DuplicateRowItem> Rows { get; } = new();

    public IReadOnlyList<RangeOption> RangeOptions { get; } =
        [new("3 months", 3), new("6 months", 6), new("1 year", 12), new("2 years", 24), new("All history", null)];
    public IReadOnlyList<int> WindowOptions { get; } = [0, 1, 2, 3, 5, 7];

    [ObservableProperty] private RangeOption _selectedRange;
    [ObservableProperty] private int _windowDays = DuplicateService.DefaultWindowDays;
    [ObservableProperty] private bool _showResolved;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _pendingChanges;
    [ObservableProperty] private DuplicateRowItem? _selectedRow;

    public bool HasPendingChanges => PendingChanges > 0;
    public string SaveText => PendingChanges == 0 ? "Save" : $"Save {PendingChanges} change(s)";
    partial void OnPendingChangesChanged(int value) { OnPropertyChanged(nameof(HasPendingChanges)); OnPropertyChanged(nameof(SaveText)); }

    private bool _loadedOnce;
    partial void OnSelectedRangeChanged(RangeOption value) { if (_loadedOnce) _ = SafeLoadAsync(); }
    partial void OnWindowDaysChanged(int value) { if (_loadedOnce) _ = SafeLoadAsync(); }
    partial void OnShowResolvedChanged(bool value) { if (_loadedOnce) _ = SafeLoadAsync(); }

    private async Task SafeLoadAsync()
    {
        try { await LoadAsync(); }
        catch (Exception ex) { Status = ex.Message; ErrorLog.Write(ex, "Possible duplicates"); }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            DateTime? from = SelectedRange.Months is { } m ? DateTime.SpecifyKind(DateTime.Today.AddMonths(-m), DateTimeKind.Utc) : null;
            var groups = await _service.FindAsync(from, WindowDays, ShowResolved);
            Rows.Clear();
            foreach (var g in groups)
                foreach (var r in g.Rows)
                    Rows.Add(new DuplicateRowItem(g, r, Recount));
            Recount();
            var open = groups.Count(g => !g.IsResolved);
            Summary = groups.Count == 0
                ? "No possible duplicates found. Widen the date range or the day window to look further."
                : $"{open} group(s) to review" + (ShowResolved ? $", {groups.Count - open} already resolved" : "") +
                  $" · {Rows.Count} transactions. Same account, same amount, same vendor, posted within {WindowDays} day(s) of each other.";
            Status = "";
            _loadedOnce = true;
        }
        finally { IsBusy = false; }
    }

    private void Recount() => PendingChanges = Rows.Count(r => r.IsChanged);

    /// <summary>Ticks every row but the suggested keeper in each group (the provider-synced copy, else the first stored).</summary>
    [RelayCommand]
    private void SelectSuggested()
    {
        foreach (var r in Rows) r.IsMarked = !r.IsSuggestedKeep;
    }

    /// <summary>Back to what is stored.</summary>
    [RelayCommand]
    private void ResetChoices()
    {
        foreach (var r in Rows) r.IsMarked = r.WasMarked;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var mark = Rows.Where(r => r.IsChanged && r.IsMarked).Select(r => r.Id).ToList();
        var unmark = Rows.Where(r => r.IsChanged && !r.IsMarked).Select(r => r.Id).ToList();
        if (mark.Count + unmark.Count == 0) return;
        // A group with every copy ticked would drop the purchase entirely; that is almost certainly a slip.
        var allTicked = Rows.GroupBy(r => r.GroupNumber).Where(g => g.All(r => r.IsMarked)).Select(g => g.Key).ToList();
        if (allTicked.Count > 0)
        {
            Status = $"Every transaction in group {string.Join(", ", allTicked)} is ticked — leave one unticked to keep the purchase, then save.";
            return;
        }
        IsBusy = true;
        try
        {
            var (marked, unmarked) = await _service.SaveAsync(mark, unmark);
            foreach (var r in Rows) r.Committed();
            Recount();
            Status = $"Saved: {marked} marked as duplicates" + (unmarked > 0 ? $", {unmarked} unmarked" : "") + ". Reports, budgets and the dashboard now leave them out.";
            Saved?.Invoke();
            await LoadAsync();
            Status = $"Saved: {marked} marked as duplicates" + (unmarked > 0 ? $", {unmarked} unmarked" : "") + ". Reports, budgets and the dashboard now leave them out.";
        }
        catch (Exception ex) { Status = ex.Message; ErrorLog.Write(ex, "Possible duplicates"); }
        finally { IsBusy = false; }
    }

    /// <summary>The selected row's group is not a duplicate: remember that and stop offering it.</summary>
    [RelayCommand]
    private async Task NotDuplicatesAsync()
    {
        if (SelectedRow is not { } row) { Status = "Select a transaction in the group first."; return; }
        var ids = Rows.Where(r => r.GroupNumber == row.GroupNumber).Select(r => r.Id).ToList();
        try
        {
            await _service.DismissAsync(ids);
            if (Rows.Any(r => r.GroupNumber == row.GroupNumber && r.WasMarked))
            {
                await _service.SetDuplicateAsync(ids, false);
                Saved?.Invoke();
            }
            Status = $"Group {row.GroupNumber} will not be offered again.";
            await LoadAsync();
            Status = $"Group {row.GroupNumber} marked as not duplicates; it will not be offered again.";
        }
        catch (Exception ex) { Status = ex.Message; ErrorLog.Write(ex, "Possible duplicates"); }
    }
}
