using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Budgeting;
using Hoard.Desktop.Services;

namespace Hoard.Desktop.ViewModels;

/// <summary>One bar of a row's 12-month sparkline, scaled 0..1 against the row's largest month.</summary>
public sealed record SparkBar(double Height, string Tip);

/// <summary>One category group on the Budget page; the target cell is editable.</summary>
public sealed partial class BudgetRowItem : ObservableObject
{
    public BudgetRowItem(BudgetLine line, DateOnly month)
    {
        Line = line;
        var max = line.History.DefaultIfEmpty(0).Max();
        Spark = line.History.Select((v, i) => new SparkBar(
            max > 0 ? Math.Max(0.04, (double)(v / max)) : 0.04,
            $"{month.AddMonths(-BudgetService.HistoryMonths + i):MMM yyyy}: {v:C0}")).ToList();
        _targetText = line.Target?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        CommittedText = _targetText;
    }

    public BudgetLine Line { get; }
    public long? GroupId => Line.GroupId;
    public bool CanHaveTarget => Line.GroupId is not null;
    public string Group => Line.GroupName;
    public IReadOnlyList<SparkBar> Spark { get; }
    public decimal Typical => Line.Forecast.TypicalMonth;
    public decimal Forecast => Line.Forecast.Predicted;
    public string ForecastRange => Line.Forecast == BudgetForecast.Empty ? "No spending in the last 12 months"
        : $"Likely between {Line.Forecast.Low:C0} and {Line.Forecast.High:C0} (about 8 months in 10). 12-month average {Line.Forecast.Average:C0}, typical month {Line.Forecast.TypicalMonth:C0}.";
    public string TrendText => Line.Forecast.MonthlyTrend switch
    {
        0 => "flat",
        > 0 => $"▲ {Line.Forecast.MonthlyTrend:C0}/mo",
        _ => $"▼ {-Line.Forecast.MonthlyTrend:C0}/mo",
    };
    public string TrendTip => Line.Forecast.MonthlyTrend == 0 ? "No trend"
        : $"The 12-month trend line {(Line.Forecast.MonthlyTrend > 0 ? "rises" : "falls")} {Math.Abs(Line.Forecast.MonthlyTrend):C0} a month ({Line.Forecast.TrendPercent:+0.0%;-0.0%} of a typical month); " +
          $"it explains {Line.Forecast.TrendStrength:0%} of the month-to-month variation, so the forecast follows it {(Line.Forecast.TrendStrength >= 0.5 ? "closely" : Line.Forecast.TrendStrength >= 0.2 ? "partly" : "only slightly")}.";
    public decimal Suggested => Line.Forecast.SuggestedTarget;
    public decimal Actual => Line.Actual;
    public decimal Projected => Line.Projected;
    public decimal? Remaining => Line.Remaining;
    /// <summary>0..100 for the progress bar (capped), actual against target.</summary>
    public double UsedPercent => Line.Used is { } u ? Math.Min(100, (double)u * 100) : 0;
    public BudgetStatus Status => Line.Status;
    public string StatusText => Line.Status switch
    {
        BudgetStatus.Over => "Over",
        BudgetStatus.AtRisk => "At risk",
        BudgetStatus.OnTrack => "On track",
        _ => CanHaveTarget ? "No target" : "Categorize to budget",
    };

    [ObservableProperty] private string _targetText;
    /// <summary>The value as loaded, so leaving the cell without a change does nothing.</summary>
    public string CommittedText { get; set; }
    public Func<BudgetRowItem, Task>? TargetCommitted { get; set; }
    public Task CommitTargetAsync() => TargetCommitted?.Invoke(this) ?? Task.CompletedTask;
}

/// <summary>
/// The Budget page: per category group, the 12-month history, the forecast for the month, the monthly target (typed in
/// the grid; a change applies from the month shown onward) and how the month is tracking against it.
/// </summary>
public sealed partial class BudgetViewModel : ObservableObject
{
    private readonly BudgetService _budget;
    private bool _loading;

    public BudgetViewModel(BudgetService budget, ThemeService theme)
    {
        _budget = budget;
        var thisMonth = BudgetService.MonthOf(DateOnly.FromDateTime(DateTime.Today));
        MonthOptions.Add(new MonthOption(thisMonth.AddMonths(1)));
        for (var i = 0; i < 24; i++) MonthOptions.Add(new MonthOption(thisMonth.AddMonths(-i)));
        _selectedMonth = MonthOptions[1];
        theme.Changed += () => { if (Rows.Count > 0) _ = LoadAsync(); }; // status brushes are resolved per load
    }

    /// <summary>Raised after a target changed, so reports and the dashboard reload when next shown.</summary>
    public event Action? TargetsChanged;
    /// <summary>Set by the main view model: opens Reports › Budget vs. actual.</summary>
    public Action? OpenReport { get; set; }

    public ObservableCollection<MonthOption> MonthOptions { get; } = new();
    [ObservableProperty] private MonthOption _selectedMonth;
    public ObservableCollection<BudgetRowItem> Rows { get; } = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _monthNote = "";
    [ObservableProperty] private decimal _totalTarget;
    [ObservableProperty] private decimal _totalForecast;
    [ObservableProperty] private decimal _totalActual;
    [ObservableProperty] private decimal _totalProjected;
    [ObservableProperty] private string _targetDetail = "";
    [ObservableProperty] private string _actualDetail = "";
    [ObservableProperty] private string _projectedDetail = "";
    [ObservableProperty] private decimal _unbudgeted;
    [ObservableProperty] private bool _isCurrentMonth;

    partial void OnSelectedMonthChanged(MonthOption value) => _ = LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        IsBusy = true;
        try
        {
            var month = SelectedMonth.Month;
            var b = await _budget.GetMonthAsync(month);
            Rows.Clear();
            foreach (var line in b.Lines)
                Rows.Add(new BudgetRowItem(line, month) { TargetCommitted = CommitTargetAsync });

            IsCurrentMonth = b.IsCurrentMonth;
            var future = month > BudgetService.MonthOf(DateOnly.FromDateTime(DateTime.Today));
            TotalTarget = b.TotalTarget;
            TotalForecast = b.TotalPredicted;
            TotalActual = b.TotalActual;
            TotalProjected = b.TotalProjected;
            var targeted = b.Lines.Count(l => l.Target is not null);
            TargetDetail = targeted == 0 ? "no targets yet" : $"{targeted} group(s) · forecast {b.Lines.Where(l => l.Target is not null).Sum(l => l.Forecast.Predicted):C0} for them";
            ActualDetail = b.IsCurrentMonth ? $"day {b.DayOfMonth} of {b.DaysInMonth}" : future ? "month not started" : "whole month";
            ProjectedDetail = b.TotalTarget > 0 ? $"{b.TotalTarget - b.TargetedProjected:+$#,##0;-$#,##0;$0} vs. targets for budgeted groups" : "";
            Unbudgeted = b.Lines.Where(l => l.Target is null).Sum(l => b.IsCurrentMonth ? l.Projected : l.Actual);
            MonthNote = (b.IsCurrentMonth
                    ? $"{month:MMMM yyyy} is in progress: \"Projected\" is what is spent so far plus the forecast for the {b.DaysInMonth - b.DayOfMonth} day(s) left. "
                    : future ? $"Planning {month:MMMM yyyy}. " : $"{month:MMMM yyyy} is finished; targets shown are the ones that applied then. ") +
                "Forecasts come from the 12 complete months before this one: a typical month (the highest and lowest months left out), " +
                "nudged toward the trend when spending has been rising or falling steadily. Type a target to set it from this month on; clear it to remove it.";
            Status = "";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally
        {
            IsBusy = false;
            _loading = false;
        }
    }

    private async Task CommitTargetAsync(BudgetRowItem row)
    {
        if (_loading || row.GroupId is not { } groupId) return;
        var text = row.TargetText?.Replace("$", "").Replace(",", "").Trim() ?? "";
        if (text == row.CommittedText.Trim()) return;
        decimal? target = text.Length == 0 ? null
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v) && v >= 0 ? v : null;
        if (text.Length > 0 && target is null) { Status = $"\"{row.TargetText}\" is not an amount."; return; }
        row.CommittedText = text;
        try
        {
            await _budget.SetTargetAsync(groupId, target, SelectedMonth.Month);
            Status = target is { } t and > 0
                ? $"{row.Group}: {t:C0} a month from {SelectedMonth.Label} on."
                : $"{row.Group}: no target from {SelectedMonth.Label} on.";
            TargetsChanged?.Invoke();
            await LoadAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Gives every group without a target its suggested (forecast) target.</summary>
    [RelayCommand]
    private async Task UseSuggestedAsync()
    {
        try
        {
            var n = await _budget.ApplySuggestedTargetsAsync(SelectedMonth.Month, overwrite: false);
            Status = n == 0 ? "Every group with spending already has a target." : $"Set suggested targets for {n} group(s) from {SelectedMonth.Label} on.";
            if (n > 0) TargetsChanged?.Invoke();
            await LoadAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Replaces every target with its suggestion (the view confirms first).</summary>
    public async Task ResetAllToSuggestedAsync()
    {
        try
        {
            var n = await _budget.ApplySuggestedTargetsAsync(SelectedMonth.Month, overwrite: true);
            Status = $"Reset {n} target(s) to the suggestion from {SelectedMonth.Label} on.";
            TargetsChanged?.Invoke();
            await LoadAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void ShowReport() => OpenReport?.Invoke();
}
