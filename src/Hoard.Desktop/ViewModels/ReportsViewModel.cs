using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Budgeting;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Reporting;
using Hoard.Desktop.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace Hoard.Desktop.ViewModels;

public sealed record RangeOption(string Label, int? Months)
{
    public override string ToString() => Label;
}

public sealed record MonthOption(DateOnly Month)
{
    public string Label => Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
    public override string ToString() => Label;
}

public sealed class IncomeExpenseRow
{
    public DateOnly MonthStart { get; init; }
    public string Month { get; init; } = "";
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal Net { get; init; }
    public decimal Uncategorized { get; init; }
    public decimal? SavingsRate => Income > 0 ? Net / Income : null;
}

public sealed class SpendingRow
{
    public long? GroupId { get; init; }
    public long? CategoryId { get; init; }
    public string Group { get; init; } = "";
    public string Category { get; init; } = "";
    public decimal Current { get; init; }
    public decimal Prior { get; init; }
    public decimal Change => Current - Prior;
    public string ChangePercent => Prior == 0 ? (Current == 0 ? "" : "new") : (Change / Prior).ToString("+0%;-0%;0%");
}

/// <summary>One charge in the Subscriptions grid.</summary>
public sealed class SubscriptionChargeRow
{
    public DateOnly Date { get; init; }
    public string Vendor { get; init; } = "";
    public string Cadence { get; init; } = "";
    public string Description { get; init; } = "";
    public string Account { get; init; } = "";
    public string? Category { get; init; }
    public decimal Amount { get; init; }
    public decimal AnnualCost { get; init; }
    public bool IsActive { get; init; }
    public string Status => IsActive ? "" : "lapsed";
}

/// <summary>One category group in the Budget vs. actual table.</summary>
public sealed class BudgetReportRow
{
    public long? GroupId { get; init; }
    public string Group { get; init; } = "";
    public decimal? Target { get; init; }
    public decimal Actual { get; init; }
    public decimal Projected { get; init; }
    public decimal Forecast { get; init; }
    /// <summary>Target minus the projected month-end spend (positive = under budget).</summary>
    public decimal? Variance => Target is { } t ? t - Projected : null;
    public decimal? Used { get; init; }
    public BudgetStatus Status { get; init; }
    public string StatusText => Status switch
    {
        BudgetStatus.Over => "Over",
        BudgetStatus.AtRisk => "At risk",
        BudgetStatus.OnTrack => "On track",
        _ => "No target",
    };
}

public sealed class TypeBalanceRow
{
    public string Type { get; init; } = "";
    public string? Color { get; init; }
    public string Account { get; init; } = "";
    public string Institution { get; init; } = "";
    public decimal Balance { get; init; }
    public bool IsExcluded { get; init; }
    public bool IsTypeTotal { get; init; }
    public string Note => IsExcluded ? "excluded from net worth" : "";
}

/// <summary>
/// Backs the Reports page. Colors follow the validated categorical order assigned in fixed order per entity
/// (or the configured account-type color); anything beyond eight series folds into "Other".
/// </summary>
public sealed partial class CollegeRow : ObservableObject
{
    public long AccountId { get; init; }
    public string Owner { get; init; } = "";
    public string Account { get; init; } = "";
    public string Detail { get; init; } = "";
    public decimal Balance { get; init; }
    public decimal Monthly { get; init; }
    public decimal? Projected { get; init; }
    public bool IsOwnerTotal { get; init; }
    public bool IsAccount => !IsOwnerTotal;
    /// <summary>Where the monthly figure came from: "planned" (entered here), "page" (the page-level figure) or "12-mo avg".</summary>
    public string MonthlySource { get; init; } = "";
    public string ProjectedText => Projected is { } p ? p.ToString("C0") : "—";
    /// <summary>Editable planned contribution; blank = not set (fall back to the page figure / deposit history).</summary>
    [ObservableProperty] private string _plannedText = "";
    /// <summary>The value as loaded, so leaving the cell without a change does nothing.</summary>
    public string CommittedText { get; set; } = "";
    /// <summary>Set by the view model: commits an edited planned contribution.</summary>
    public Func<CollegeRow, Task>? PlannedCommitted { get; set; }
    public Task CommitPlannedAsync() => PlannedCommitted?.Invoke(this) ?? Task.CompletedTask;
}

/// <summary>
/// Backs the Reports page. Charts are interactive: hover shows every series at a point, the net-worth chart zooms and
/// pans, clicking a month in Income vs. expenses or in the spending trend opens that month's spending, clicking a
/// slice of the spending donut (or a bar in Budget vs. actual) drills into that category group, and the grids open
/// the matching transactions. Colors follow the validated categorical order assigned per entity; anything beyond the
/// palette folds into "Other".
/// </summary>
public sealed partial class ReportsViewModel : ObservableObject
{
    public const int NetWorthTab = 0, IncomeTab = 1, SpendingTab = 2, BudgetTab = 3;

    private readonly ReportingService _reports;
    private readonly IAccountRepository _accounts;
    private readonly BudgetService _budget;
    private readonly ChartTheme _t;
    private SKColor[] Palette => _t.Palette;
    private SKColor Ink => _t.Ink;
    private const string OtherName = "Other";

    public ReportsViewModel(ReportingService reports, IAccountRepository accounts, BudgetService budget, ThemeService theme)
    {
        _reports = reports;
        _accounts = accounts;
        _budget = budget;
        _t = new ChartTheme(theme);
        var thisMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        for (var i = 0; i < 36; i++) MonthOptions.Add(new MonthOption(thisMonth.AddMonths(-i)));
        _selectedMonth = MonthOptions[0];
        _selectedBudgetMonth = MonthOptions[0];
        _selectedRange = RangeOptions[2];
        _selectedIncomeRange = IncomeRangeOptions[1];
        // Every chart gets its OWN axis instances: LiveCharts attaches an axis to the first chart that draws it,
        // so sharing one axis object between charts leaves the others blank. Axes also must never be empty.
        _netWorthYAxes = [_t.CurrencyAxis()];
        _incomeYAxes = [_t.CurrencyAxis()];
        _spendingTrendYAxes = [_t.CurrencyAxis()];
        _groupDetailXAxes = [_t.CurrencyAxis()];
        _collegeYAxes = [_t.CurrencyAxis()];
        _netWorthXAxes = [_t.DateAxis(30)];
        _incomeXAxes = [_t.CategoryAxis([])];
        _spendingTrendXAxes = [_t.CategoryAxis([])];
        _groupDetailYAxes = [_t.CategoryAxis([])];
        _typeXAxes = [_t.CurrencyAxis()];
        _typeYAxes = [_t.CategoryAxis([])];
        _subscriptionXAxes = [_t.CurrencyAxis()];
        _subscriptionYAxes = [_t.CategoryAxis([])];
        _collegeXAxes = [_t.CategoryAxis([])];
        _budgetXAxes = [_t.CurrencyAxis()];
        _budgetYAxes = [_t.CategoryAxis([])];
        _budgetHistoryXAxes = [_t.CategoryAxis([])];
        _budgetHistoryYAxes = [_t.CurrencyAxis()];
        theme.Changed += () => { if (_loadedOnce) _ = LoadAllAsync(); };
    }

    private bool _loadedOnce;

    /// <summary>Set by the main view model: opens the Transactions page filtered to a category (or all) and a date range.</summary>
    public Func<long?, DateOnly, DateOnly, Task>? ShowTransactions { get; set; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    /// <summary>Which report tab is showing (see the *Tab constants).</summary>
    [ObservableProperty] private int _selectedReportIndex;

    // ---------- Net worth ----------
    public IReadOnlyList<RangeOption> RangeOptions { get; } =
        [new("3 months", 3), new("6 months", 6), new("1 year", 12), new("2 years", 24), new("All history", null)];
    [ObservableProperty] private RangeOption _selectedRange;
    [ObservableProperty] private bool _breakDownByType = true;
    [ObservableProperty] private bool _includeEstimated = true;
    [ObservableProperty] private ISeries[] _netWorthSeries = [];
    [ObservableProperty] private Axis[] _netWorthXAxes = [];
    [ObservableProperty] private string _netWorthNote = "";
    [ObservableProperty] private Axis[] _netWorthYAxes = [];
    [ObservableProperty] private decimal _netWorthNow;
    [ObservableProperty] private decimal _netWorthChange;
    [ObservableProperty] private string _netWorthChangeDetail = "";
    [ObservableProperty] private decimal _assetsNow;
    [ObservableProperty] private decimal _liabilitiesNow;
    private int _netWorthSpanDays = 30;

    /// <summary>Back to the whole range after zooming or panning the net-worth chart.</summary>
    [RelayCommand]
    private void ResetNetWorthZoom() => NetWorthXAxes = [_t.DateAxis(_netWorthSpanDays)];

    // ---------- Balances by type ----------
    [ObservableProperty] private ISeries[] _typeSeries = [];
    [ObservableProperty] private Axis[] _typeXAxes = [];
    [ObservableProperty] private Axis[] _typeYAxes = [];
    [ObservableProperty] private double _typeChartHeight = 320;
    [ObservableProperty] private string _typeHeadline = "";
    [ObservableProperty] private string _typeNote = "";

    // ---------- Subscriptions ----------
    [ObservableProperty] private ISeries[] _subscriptionSeries = [];
    [ObservableProperty] private Axis[] _subscriptionXAxes = [];
    [ObservableProperty] private Axis[] _subscriptionYAxes = [];
    [ObservableProperty] private double _subscriptionChartHeight = 320;
    [ObservableProperty] private string _subscriptionHeadline = "";
    [ObservableProperty] private string _subscriptionNote = "";
    /// <summary>Hide subscriptions whose last charge is too old for the next one to still be expected.</summary>
    [ObservableProperty] private bool _activeSubscriptionsOnly = true;
    public ObservableCollection<SubscriptionChargeRow> SubscriptionRows { get; } = new();
    partial void OnActiveSubscriptionsOnlyChanged(bool value) => _ = LoadSubscriptionsAsync();
    public ObservableCollection<TypeBalanceRow> TypeRows { get; } = new();

    // ---------- College savings ----------
    public ObservableCollection<AccountCategory> CollegeTypeOptions { get; } = new();
    [ObservableProperty] private AccountCategory? _selectedCollegeType;
    [ObservableProperty] private decimal _collegeRatePercent = 6m;
    /// <summary>Blank = use each account's trailing-12-month average deposits.</summary>
    [ObservableProperty] private string _collegeContributionOverride = "";
    [ObservableProperty] private ISeries[] _collegeSeries = [];
    [ObservableProperty] private Axis[] _collegeXAxes = [];
    [ObservableProperty] private Axis[] _collegeYAxes = [];
    [ObservableProperty] private string _collegeHeadline = "";
    [ObservableProperty] private string _collegeNote = "";
    public ObservableCollection<CollegeRow> CollegeRows { get; } = new();

    private bool _collegeLoading;

    // Ignore the null the ComboBox pushes while the options list is being rebuilt, and anything raised during a load:
    // reacting to those re-entered LoadCollegeAsync endlessly (flicker, hang, runaway memory).
    partial void OnSelectedCollegeTypeChanged(AccountCategory? value) { if (_loadedOnce && !_collegeLoading && value is not null) _ = LoadCollegeAsync(); }

    [RelayCommand]
    private Task RecalculateCollegeAsync() => LoadCollegeAsync();

    partial void OnSelectedRangeChanged(RangeOption value) => _ = LoadNetWorthAsync();
    partial void OnBreakDownByTypeChanged(bool value) => _ = LoadNetWorthAsync();
    partial void OnIncludeEstimatedChanged(bool value) => _ = LoadNetWorthAsync();

    // ---------- Income vs. expenses ----------
    public IReadOnlyList<RangeOption> IncomeRangeOptions { get; } = [new("6 months", 6), new("12 months", 12), new("24 months", 24)];
    [ObservableProperty] private RangeOption _selectedIncomeRange;
    [ObservableProperty] private ISeries[] _incomeSeries = [];
    [ObservableProperty] private Axis[] _incomeXAxes = [];
    [ObservableProperty] private Axis[] _incomeYAxes = [];
    [ObservableProperty] private string _incomeNote = "";
    [ObservableProperty] private decimal _averageIncome;
    [ObservableProperty] private decimal _averageExpense;
    [ObservableProperty] private decimal? _savingsRate;
    [ObservableProperty] private string _savingsDetail = "";
    public ObservableCollection<IncomeExpenseRow> IncomeRows { get; } = new();
    [ObservableProperty] private IncomeExpenseRow? _selectedIncomeRow;

    partial void OnSelectedIncomeRangeChanged(RangeOption value) => _ = LoadIncomeAsync();

    /// <summary>Opens the Spending tab on a month (from a click on the income chart or a double-click in its grid).</summary>
    public void DrillIntoMonth(DateOnly month)
    {
        var option = MonthOptions.FirstOrDefault(o => o.Month == month);
        if (option is null) return;
        SelectedGroup = null;
        SelectedMonth = option;
        SelectedReportIndex = SpendingTab;
    }

    // ---------- Spending ----------
    public ObservableCollection<MonthOption> MonthOptions { get; } = new();
    [ObservableProperty] private MonthOption _selectedMonth;
    [ObservableProperty] private ISeries[] _spendingTrendSeries = [];
    [ObservableProperty] private Axis[] _spendingTrendXAxes = [];
    [ObservableProperty] private Axis[] _spendingTrendYAxes = [];
    [ObservableProperty] private ISeries[] _spendingPieSeries = [];
    [ObservableProperty] private ISeries[] _groupDetailSeries = [];
    [ObservableProperty] private Axis[] _groupDetailXAxes = [];
    [ObservableProperty] private Axis[] _groupDetailYAxes = [];
    [ObservableProperty] private double _groupDetailHeight = 300;
    [ObservableProperty] private decimal _spentThisMonth;
    [ObservableProperty] private decimal _spentPriorYear;
    [ObservableProperty] private string _spendingChangeText = "";
    [ObservableProperty] private string _topGroupText = "";
    [ObservableProperty] private string _spendingNote = "";
    /// <summary>The category group the user drilled into (null = all groups).</summary>
    [ObservableProperty] private string? _selectedGroup;
    public bool HasSelectedGroup => SelectedGroup is not null;
    public string SpendingDetailTitle => SelectedGroup is null
        ? $"{SelectedMonth.Label} by category group — click a slice to see its categories"
        : $"{SelectedGroup}: {SelectedMonth.Label} vs. a year earlier, by category";
    public ObservableCollection<SpendingRow> SpendingRows { get; } = new();
    [ObservableProperty] private SpendingRow? _selectedSpendingRow;
    private SpendingComparison? _comparison;

    partial void OnSelectedMonthChanged(MonthOption value) => _ = LoadSpendingAsync();
    partial void OnSelectedGroupChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSelectedGroup));
        OnPropertyChanged(nameof(SpendingDetailTitle));
        if (_comparison is not null) ShowSpendingDetail(_comparison);
    }

    [RelayCommand]
    private void ClearSpendingGroup() => SelectedGroup = null;

    /// <summary>Transactions behind the selected row (or the drilled-into group, or the whole month).</summary>
    [RelayCommand]
    private async Task OpenSpendingTransactionsAsync()
    {
        if (ShowTransactions is null) return;
        var month = SelectedMonth.Month;
        var category = SelectedSpendingRow?.CategoryId ?? (SelectedGroup is null ? null : SpendingRows.FirstOrDefault()?.GroupId);
        await ShowTransactions(category, month, month.AddMonths(1).AddDays(-1));
    }

    // ---------- Budget vs. actual ----------
    [ObservableProperty] private MonthOption _selectedBudgetMonth;
    [ObservableProperty] private ISeries[] _budgetSeries = [];
    [ObservableProperty] private Axis[] _budgetXAxes = [];
    [ObservableProperty] private Axis[] _budgetYAxes = [];
    [ObservableProperty] private double _budgetChartHeight = 320;
    [ObservableProperty] private ISeries[] _budgetHistorySeries = [];
    [ObservableProperty] private Axis[] _budgetHistoryXAxes = [];
    [ObservableProperty] private Axis[] _budgetHistoryYAxes = [];
    [ObservableProperty] private string _budgetHistoryTitle = "";
    [ObservableProperty] private decimal _budgetTarget;
    [ObservableProperty] private decimal _budgetActual;
    [ObservableProperty] private decimal _budgetProjected;
    [ObservableProperty] private string _budgetActualDetail = "";
    [ObservableProperty] private string _budgetProjectedDetail = "";
    [ObservableProperty] private string _budgetGroupsDetail = "";
    [ObservableProperty] private int _budgetGroupsOver;
    [ObservableProperty] private string _budgetNote = "";
    [ObservableProperty] private bool _hasBudgetTargets;
    public ObservableCollection<BudgetReportRow> BudgetRows { get; } = new();
    [ObservableProperty] private BudgetReportRow? _selectedBudgetRow;
    /// <summary>The group whose 12-month history is charted (null = all groups with a target).</summary>
    [ObservableProperty] private BudgetReportRow? _budgetHistoryGroup;

    partial void OnSelectedBudgetMonthChanged(MonthOption value) => _ = LoadBudgetAsync();
    partial void OnBudgetHistoryGroupChanged(BudgetReportRow? value) => _ = LoadBudgetHistoryAsync();

    [RelayCommand]
    private void ShowAllBudgetHistory() => BudgetHistoryGroup = null;

    [RelayCommand]
    private async Task OpenBudgetTransactionsAsync()
    {
        if (ShowTransactions is null) return;
        var month = SelectedBudgetMonth.Month;
        await ShowTransactions(SelectedBudgetRow?.GroupId, month, month.AddMonths(1).AddDays(-1));
    }

    [RelayCommand]
    public async Task LoadAllAsync()
    {
        IsBusy = true;
        try
        {
            await LoadNetWorthAsync();
            await LoadIncomeAsync();
            await LoadSpendingAsync();
            await LoadBudgetAsync();
            await LoadBalancesByTypeAsync();
            await LoadCollegeAsync();
            await LoadSubscriptionsAsync();
            _loadedOnce = true;
            Status = "";
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadBalancesByTypeAsync()
    {
        var report = await _reports.BalancesByTypeAsync(includeEmptyTypes: true);
        var groups = report.Groups.ToList();
        TypeChartHeight = ChartTheme.BarChartHeight(groups.Count);
        TypeYAxes = [_t.CategoryAxis(groups.Select(g => g.Name).ToArray(), everyLabel: true)];
        TypeXAxes = [_t.CurrencyAxis()];
        TypeSeries = ChartTheme.TypeBars(groups, _t);

        TypeRows.Clear();
        foreach (var g in report.Groups)
        {
            TypeRows.Add(new TypeBalanceRow { Type = g.Name, Color = g.Color, Account = $"{g.Name} total", Balance = g.Total, IsTypeTotal = true });
            foreach (var a in g.Accounts)
                TypeRows.Add(new TypeBalanceRow { Type = g.Name, Color = g.Color, Account = a.Name, Institution = a.InstitutionName, Balance = a.Balance, IsExcluded = a.ExcludeFromNetWorth });
        }

        TypeHeadline = $"{report.NetWorth:C0} net worth  ·  {report.Assets:C0} assets  ·  {report.Liabilities:C0} liabilities";
        TypeNote = $"{report.AccountCount} account(s) across {report.Groups.Count} type(s)" +
                   (report.ExcludedCount > 0 ? $"; {report.ExcludedCount} excluded from net worth (listed, not totalled)." : ".") +
                   " Manage types under Accounts › Account types.";
    }

    private async Task LoadSubscriptionsAsync()
    {
        var report = await _reports.SubscriptionsAsync();
        var shown = (ActiveSubscriptionsOnly ? report.Active : report.Subscriptions).ToList();

        // One horizontal bar per vendor, tallest first, coloured by position; the tooltip carries the cadence.
        var byCost = shown.OrderByDescending(x => x.AnnualCost).ThenBy(x => x.Vendor).ToList();
        var labels = byCost.Select(x => $"{x.Vendor} ({SubscriptionDetector.CadenceLabel(x.Cadence)})").ToArray();
        SubscriptionChartHeight = ChartTheme.BarChartHeight(byCost.Count);
        SubscriptionYAxes = [_t.CategoryAxis(labels, everyLabel: true)];
        SubscriptionXAxes = [_t.CurrencyAxis()];
        var wide = _t.WidePalette;
        var series = new List<ISeries>();
        for (var i = 0; i < byCost.Count; i++)
        {
            var values = new double?[byCost.Count];
            values[i] = (double)byCost[i].AnnualCost;
            var sub = byCost[i];
            series.Add(new RowSeries<double?>
            {
                Name = sub.Vendor,
                Values = values,
                Fill = new SolidColorPaint(sub.IsActive ? wide[i % wide.Length] : wide[i % wide.Length].WithAlpha(110)),
                Stroke = null,
                MaxBarWidth = 26,
                Rx = 4, Ry = 4,
                IgnoresBarPosition = true,
                YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue:C0} a year · {sub.LatestAmount:C2} {SubscriptionDetector.CadenceLabel(sub.Cadence)}" + (sub.IsActive ? "" : " · lapsed"),
                IsVisibleAtLegend = false,
            });
        }
        SubscriptionSeries = series.ToArray();

        SubscriptionRows.Clear();
        foreach (var sub in byCost)
            foreach (var c in sub.Charges.OrderByDescending(c => c.PostedOn))
                SubscriptionRows.Add(new SubscriptionChargeRow
                {
                    Date = c.PostedOn, Vendor = sub.Vendor, Cadence = SubscriptionDetector.CadenceLabel(sub.Cadence),
                    Description = c.Description, Account = c.AccountName, Category = c.CategoryName,
                    Amount = c.Amount, AnnualCost = sub.AnnualCost, IsActive = sub.IsActive,
                });

        var active = report.Active;
        var monthlyEquivalent = report.ActiveAnnualCost / 12m;
        SubscriptionHeadline = active.Count == 0
            ? "No recurring charges found"
            : $"{report.ActiveAnnualCost:C0} a year in {active.Count} active subscription(s)  ·  about {monthlyEquivalent:C0} a month";
        var lapsed = report.Subscriptions.Count - active.Count;
        SubscriptionNote = $"Looked at {report.TransactionsExamined:N0} money-out transactions since {report.From:MMM yyyy} in the account types ticked for spending reports. " +
                           "A subscription is the same vendor charging the same or a similar amount every month, quarter or year (a small price change stays one subscription). " +
                           (lapsed > 0 ? $"{lapsed} lapsed subscription(s) whose charges stopped are {(ActiveSubscriptionsOnly ? "hidden" : "shown greyed")}." : "");
    }

    // ==================================================================================================

    private async Task LoadCollegeAsync()
    {
        if (_collegeLoading) return;
        _collegeLoading = true;
        try { await LoadCollegeCoreAsync(); }
        finally { _collegeLoading = false; }
    }

    private async Task LoadCollegeCoreAsync()
    {
        var types = await _reports.BalancesByTypeAsync(includeEmptyTypes: true);
        var keepId = SelectedCollegeType?.Id;
        var fresh = types.Groups.Where(g => g.TypeId is not null)
            .Select(g => new AccountCategory { Id = g.TypeId!.Value, Name = g.Name, Kind = g.Kind, Color = g.Color }).ToList();
        // Only rebuild the options when they actually changed, so the ComboBox keeps its selection.
        if (!fresh.Select(t => (t.Id, t.Name)).SequenceEqual(CollegeTypeOptions.Select(t => (t.Id, t.Name))))
        {
            CollegeTypeOptions.Clear();
            foreach (var t in fresh) CollegeTypeOptions.Add(t);
        }
        var pick = CollegeTypeOptions.FirstOrDefault(t => t.Id == keepId)
                   ?? CollegeTypeOptions.FirstOrDefault(t => t.Name.Contains("College", StringComparison.OrdinalIgnoreCase))
                   ?? CollegeTypeOptions.FirstOrDefault();
        if (!ReferenceEquals(_selectedCollegeType, pick))
        {
            _selectedCollegeType = pick;
            OnPropertyChanged(nameof(SelectedCollegeType));
        }
        if (pick is null)
        {
            CollegeSeries = [];
            CollegeRows.Clear();
            CollegeHeadline = "";
            CollegeNote = "Create a \"College Savings\" account type and assign accounts to it to use this report.";
            return;
        }

        decimal? overrideContribution = decimal.TryParse(CollegeContributionOverride, NumberStyles.Number, CultureInfo.CurrentCulture, out var oc) ? oc : null;
        var rate = Math.Clamp(CollegeRatePercent, -50m, 50m) / 100m;
        var report = await _reports.CollegeSavingsAsync(pick.Id, rate, overrideContribution);

        var owners = report.Owners;
        CollegeSeries =
        [
            _t.Column("Today", owners.Select(o => (double)o.CurrentTotal), Palette[0]),
            _t.Column("Projected at 18", owners.Select(o => (double)(o.ProjectedAt18 ?? o.CurrentTotal)), Palette[2]),
        ];
        CollegeXAxes = [_t.CategoryAxis(owners.Select(o => o.OwnerName).ToArray(), everyLabel: true)];
        CollegeYAxes = [_t.CurrencyAxis()];

        CollegeRows.Clear();
        foreach (var o in owners)
        {
            var when = o.EighteenthBirthday is { } e ? $"turns 18 on {e:MMM d, yyyy}" : "no date of birth";
            var age = o.AgeNow is { } a ? $"age {a}" : "";
            CollegeRows.Add(new CollegeRow
            {
                Owner = o.OwnerName, Account = $"{o.OwnerName} total", Detail = string.Join(" · ", new[] { age, when }.Where(x => x.Length > 0)),
                Balance = o.CurrentTotal, Monthly = o.MonthlyContribution, Projected = o.ProjectedAt18, IsOwnerTotal = true,
            });
            foreach (var acct in o.Accounts)
                CollegeRows.Add(new CollegeRow
                {
                    AccountId = acct.AccountId, Owner = o.OwnerName, Account = acct.Name, Detail = acct.BankName, Balance = acct.Balance,
                    Monthly = acct.MonthlyContribution, Projected = acct.ProjectedAt18,
                    MonthlySource = acct.ContributionSource switch
                    {
                        ContributionSource.Planned => "planned",
                        ContributionSource.PageOverride => "page figure",
                        _ => "12-mo avg",
                    },
                    PlannedText = acct.PlannedMonthlyContribution?.ToString("0.##", CultureInfo.CurrentCulture) ?? "",
                    CommittedText = acct.PlannedMonthlyContribution?.ToString("0.##", CultureInfo.CurrentCulture) ?? "",
                    PlannedCommitted = CommitPlannedAsync,
                });
        }

        CollegeHeadline = $"{report.TypeName}: {report.CurrentTotal:C0} today  ·  {report.ProjectedTotal:C0} projected when each owner turns 18";
        var withoutDob = owners.Count(o => o.OwnerId is not null && o.DateOfBirth is null);
        CollegeNote = $"Assumes {report.AnnualRate:P1} annual growth, compounded monthly. Each account contributes its planned monthly figure (type one in the Planned column), otherwise " +
                      (overrideContribution is { } c ? $"{c:C0}/month," : "its average monthly deposits over the last 12 months,") +
                      " continuing until the owner turns 18." +
                      (withoutDob > 0 ? $" {withoutDob} owner(s) have no date of birth, so their balances are shown without a projection." : "") +
                      " Set owners and dates of birth under Accounts › Household members.";
    }

    /// <summary>Saves an edited planned contribution for one account and recalculates the projections.</summary>
    private async Task CommitPlannedAsync(CollegeRow row)
    {
        if (_collegeLoading) return; // focus changes while the grid is being rebuilt are not edits
        var text = row.PlannedText?.Replace("$", "").Replace(",", "").Trim() ?? "";
        if (text == row.CommittedText.Trim()) return;
        row.CommittedText = text;
        decimal? planned = text.Length == 0 ? null
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ? Math.Round(v, 2) : null;
        if (text.Length > 0 && planned is null) { Status = $"\"{row.PlannedText}\" is not a number."; return; }
        try
        {
            await _accounts.SetPlannedContributionAsync(row.AccountId, planned);
            Status = "";
            await LoadCollegeAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private async Task LoadNetWorthAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = SelectedRange.Months is { } m ? today.AddMonths(-m) : (DateOnly?)null;
        var report = await _reports.NetWorthAsync(from, today);

        if (report.Points.Count == 0)
        {
            NetWorthSeries = [];
            NetWorthNote = "No balance data yet. Sync at least once to start the net-worth history.";
            NetWorthNow = AssetsNow = LiabilitiesNow = NetWorthChange = 0;
            NetWorthChangeDetail = "";
            return;
        }

        // "Include estimated history" off: only days where every balance was actually observed (a sync snapshot).
        var points = IncludeEstimated ? report.Points : report.Points.Where(p => !p.IsEstimated).ToList();
        var observedOnlyEmpty = points.Count == 0;
        if (observedOnlyEmpty) points = report.Points;
        // Sparse series (a handful of sync days) would draw as invisible zero-length lines: show markers instead.
        var markers = points.Count < 40;
        var wide = _t.WidePalette;

        var series = new List<ISeries>();
        if (BreakDownByType)
        {
            var (names, folded) = FoldTypes(report.AssetTypes, points, maxSeries: wide.Length);
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                var color = wide[i % wide.Length];
                series.Add(new StackedAreaSeries<DateTimePoint>
                {
                    Name = name,
                    Values = points.Select(p => new DateTimePoint(ToDate(p.Date), (double)folded(p, name))).ToList(),
                    Fill = new SolidColorPaint(color.WithAlpha(150)),
                    Stroke = new SolidColorPaint(color) { StrokeThickness = 1 },
                    GeometrySize = markers ? 8 : 0,
                    GeometryFill = markers ? new SolidColorPaint(color) : null,
                    GeometryStroke = markers ? new SolidColorPaint(_t.Surface) { StrokeThickness = 1 } : null,
                    LineSmoothness = 0,
                    YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
                });
            }
        }
        else
        {
            series.Add(_t.Line("Assets", points.Select(p => new DateTimePoint(ToDate(p.Date), (double)p.Assets)), Palette[2], markers: markers));
        }
        if (report.LiabilityTypes.Count > 0)
            series.Add(_t.Line("Liabilities", points.Select(p => new DateTimePoint(ToDate(p.Date), (double)p.Liabilities)), Palette[7], markers: markers));
        series.Add(_t.Line("Net worth", points.Select(p => new DateTimePoint(ToDate(p.Date), (double)p.NetWorth)), Ink, thickness: 3, markers: markers));

        NetWorthSeries = series.ToArray();
        _netWorthSpanDays = points[^1].Date.DayNumber - points[0].Date.DayNumber;
        NetWorthXAxes = [_t.DateAxis(_netWorthSpanDays)];
        NetWorthYAxes = [_t.CurrencyAxis()];

        var last = points[^1];
        var first = points[0];
        NetWorthNow = last.NetWorth;
        AssetsNow = last.Assets;
        LiabilitiesNow = last.Liabilities;
        NetWorthChange = last.NetWorth - first.NetWorth;
        NetWorthChangeDetail = $"since {first.Date:MMM d, yyyy}" +
            (first.NetWorth != 0 ? $" ({NetWorthChange / Math.Abs(first.NetWorth):+0.0%;-0.0%;0%})" : "");
        var estimatedDays = points.Count(p => p.IsEstimated);
        NetWorthNote = "Drag to pan, scroll to zoom; hover for every series on a day. " + (observedOnlyEmpty
            ? "No day has an observed balance for every account yet, so estimated history is shown; each sync records the balances of that day."
            : estimatedDays == 0
                ? $"All {points.Count} day(s) shown were observed from bank balances." + (IncludeEstimated ? "" : " Tick \"Include estimated history\" to fill in the days between syncs from transaction history.")
                : $"{estimatedDays} of {points.Count} days are estimated by rolling balances backward from the first sync using transaction history" +
                  (report.ReliableFrom is { } r ? $"; every account has data from {r:MMM d, yyyy}." : "."));
    }

    private static (List<string> Names, Func<NetWorthPoint, string, decimal> Value) FoldTypes(IReadOnlyList<string> types, IReadOnlyList<NetWorthPoint> points, int maxSeries)
    {
        if (types.Count <= maxSeries)
            return (types.ToList(), (p, n) => p.ByType.GetValueOrDefault(n));
        // Keep the largest types by latest balance; fold the rest into "Other". Assignment is fixed for the life of the chart.
        var latest = points[^1].ByType;
        var keep = types.OrderByDescending(t => latest.GetValueOrDefault(t)).Take(maxSeries - 1).ToList();
        var rest = types.Except(keep).ToHashSet();
        var names = keep.Append(OtherName).ToList();
        return (names, (p, n) => n == OtherName ? rest.Sum(t => p.ByType.GetValueOrDefault(t)) : p.ByType.GetValueOrDefault(n));
    }

    /// <summary>Which account types feed the transaction-based reports, for the notes under them.</summary>
    private async Task<string> SpendingScopeNoteAsync()
    {
        var types = (await _accounts.ListCategoriesAsync()).Where(t => t.IncludeInSpending).Select(t => t.Name).ToList();
        return types.Count == 0
            ? "No account type is ticked for spending reports (Accounts › Account types), so nothing is counted."
            : $"Counts transactions in {string.Join(", ", types)} accounts only (Accounts › Account types › Spending reports).";
    }

    private async Task LoadIncomeAsync()
    {
        var thisMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var months = SelectedIncomeRange.Months ?? 12;
        var rows = await _reports.IncomeVsExpenseAsync(thisMonth.AddMonths(-(months - 1)), thisMonth);
        var monthStarts = rows.Select(r => r.Month).ToList();

        var labels = rows.Select(r => r.Month.ToString("MMM yy", CultureInfo.CurrentCulture)).ToArray();
        var income = _t.Column("Income", rows.Select(r => (double)r.Income), Palette[0]);
        var expense = _t.Column("Expenses", rows.Select(r => (double)r.Expense), Palette[1]);
        foreach (var column in new[] { income, expense })
            column.ChartPointPointerDown += (_, point) => { if (point is not null) OnUi(() => DrillIntoMonth(monthStarts[point.Index])); };
        IncomeSeries =
        [
            income,
            expense,
            new LineSeries<double>
            {
                Name = "Net",
                Values = rows.Select(r => (double)r.Net).ToList(),
                Stroke = new SolidColorPaint(Ink) { StrokeThickness = 2 },
                Fill = null,
                GeometrySize = 8,
                GeometryFill = new SolidColorPaint(_t.Surface),
                GeometryStroke = new SolidColorPaint(Ink) { StrokeThickness = 2 },
                LineSmoothness = 0,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
            },
        ];
        IncomeXAxes = [_t.CategoryAxis(labels)];
        IncomeYAxes = [_t.CurrencyAxis()];

        IncomeRows.Clear();
        foreach (var r in rows.AsEnumerable().Reverse())
            IncomeRows.Add(new IncomeExpenseRow
            {
                MonthStart = r.Month,
                Month = r.Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                Income = r.Income, Expense = r.Expense, Net = r.Net,
                Uncategorized = r.UncategorizedIncome + r.UncategorizedExpense,
            });

        // KPIs over complete months only: the current month is still filling in.
        var complete = rows.Where(r => r.Month < thisMonth).ToList();
        var totalIncome = complete.Sum(r => r.Income);
        var totalExpense = complete.Sum(r => r.Expense);
        AverageIncome = complete.Count == 0 ? 0 : totalIncome / complete.Count;
        AverageExpense = complete.Count == 0 ? 0 : totalExpense / complete.Count;
        SavingsRate = totalIncome > 0 ? (totalIncome - totalExpense) / totalIncome : null;
        SavingsDetail = complete.Count == 0 ? "" : $"{totalIncome - totalExpense:+$#,##0;-$#,##0;$0} saved over {complete.Count} complete month(s)";
        var unc = rows.Sum(r => r.UncategorizedIncome + r.UncategorizedExpense);
        IncomeNote = "Click a month's bar, or double-click a row, to see what was spent that month. Transfers are excluded." +
                     (unc > 0 ? $" {unc:C0} of uncategorized activity was counted by sign — categorize it for accuracy." : "") +
                     " " + await SpendingScopeNoteAsync();
    }

    private async Task LoadSpendingAsync()
    {
        var month = SelectedMonth.Month;

        // 12-month trend by category group, stacked; clicking a month selects it.
        var trend = await _reports.SpendingTrendAsync(month, 12);
        var months = Enumerable.Range(0, 12).Select(i => month.AddMonths(-(11 - i))).ToList();
        var groupTotals = trend.GroupBy(t => t.GroupName).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var groups = groupTotals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var topGroups = groups.Take(Palette.Length - 1).ToList();
        var folded = groups.Skip(Palette.Length - 1).ToHashSet();
        var seriesNames = folded.Count > 0 ? topGroups.Append(OtherName).ToList() : topGroups;
        _groupColors = seriesNames.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => Palette[x.i % Palette.Length]);

        decimal TrendValue(DateOnly m, string g) => g == OtherName
            ? trend.Where(t => t.Month == m && folded.Contains(t.GroupName)).Sum(t => t.Amount)
            : trend.Where(t => t.Month == m && t.GroupName == g).Sum(t => t.Amount);

        SpendingTrendSeries = seriesNames.Select((g, i) =>
        {
            var s = new StackedColumnSeries<double>
            {
                Name = g,
                Values = months.Select(m => (double)TrendValue(m, g)).ToList(),
                Fill = new SolidColorPaint(Palette[i % Palette.Length]),
                Stroke = new SolidColorPaint(_t.Surface) { StrokeThickness = 1 },
                MaxBarWidth = 48,
                Rx = 2, Ry = 2,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
            };
            s.ChartPointPointerDown += (_, point) =>
            {
                if (point is null) return;
                OnUi(() =>
                {
                    var option = MonthOptions.FirstOrDefault(o => o.Month == months[point.Index]);
                    if (option is not null) SelectedMonth = option;
                    if (g != OtherName) SelectedGroup = g;
                });
            };
            return (ISeries)s;
        }).ToArray();
        SpendingTrendXAxes = [_t.CategoryAxis(months.Select(m => m.ToString("MMM yy", CultureInfo.CurrentCulture)).ToArray())];
        SpendingTrendYAxes = [_t.CurrencyAxis()];

        var cmp = await _reports.SpendingComparisonAsync(month);
        _comparison = cmp;
        SpentThisMonth = cmp.CurrentTotal;
        SpentPriorYear = cmp.PriorTotal;
        var delta = cmp.CurrentTotal - cmp.PriorTotal;
        SpendingChangeText = cmp.PriorTotal > 0
            ? $"{delta:+$#,##0;-$#,##0;$0} ({delta / cmp.PriorTotal:+0%;-0%;0%}) vs. {cmp.PriorMonth:MMM yyyy}"
            : $"nothing recorded in {cmp.PriorMonth:MMM yyyy}";
        var top = cmp.Current.GroupBy(c => c.GroupName).Select(g => (Name: g.Key, Total: g.Sum(c => c.Amount))).OrderByDescending(g => g.Total).FirstOrDefault();
        TopGroupText = top.Name is null ? "—" : $"{top.Name} · {top.Total:C0}";
        if (SelectedGroup is not null && cmp.Current.All(c => c.GroupName != SelectedGroup) && cmp.Prior.All(c => c.GroupName != SelectedGroup))
            _selectedGroup = null;
        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(HasSelectedGroup));
        OnPropertyChanged(nameof(SpendingDetailTitle));
        ShowSpendingDetail(cmp);

        SpendingNote = "Click a column to open that month (and its group), a slice to drill into a group; double-click a row to see its transactions. " +
                       await SpendingScopeNoteAsync();
    }

    private Dictionary<string, SKColor> _groupColors = new();

    /// <summary>The lower half of the Spending tab: the donut of all groups, or one group's categories against last year, plus the grid.</summary>
    private void ShowSpendingDetail(SpendingComparison cmp)
    {
        var currentByGroup = cmp.Current.GroupBy(c => c.GroupName).Select(g => (Name: g.Key, Total: g.Sum(c => c.Amount)))
            .Where(g => g.Total > 0).OrderByDescending(g => g.Total).ToList();
        var total = currentByGroup.Sum(g => g.Total);
        var extra = 0;
        SpendingPieSeries = currentByGroup.Select(g =>
        {
            var color = _groupColors.TryGetValue(g.Name, out var c) ? c : _t.WidePalette[(_groupColors.Count + extra++) % _t.WidePalette.Length];
            var share = total > 0 ? g.Total / total : 0;
            var slice = new PieSeries<double>
            {
                Name = g.Name,
                Values = [(double)g.Total],
                Fill = new SolidColorPaint(SelectedGroup is null || SelectedGroup == g.Name ? color : color.WithAlpha(70)),
                Stroke = new SolidColorPaint(_t.Surface) { StrokeThickness = 2 },
                InnerRadius = 70,
                Pushout = SelectedGroup == g.Name ? 10 : 0,
                ToolTipLabelFormatter = _ => $"{g.Total:C0} · {share:0%}",
            };
            slice.ChartPointPointerDown += (_, point) => { if (point is not null) OnUi(() => SelectedGroup = SelectedGroup == g.Name ? null : g.Name); };
            return (ISeries)slice;
        }).ToArray();

        // One group's categories: this month vs. the same month last year.
        if (SelectedGroup is { } group)
        {
            var cur = cmp.Current.Where(c => c.GroupName == group).ToDictionary(c => c.CategoryName, c => c.Amount);
            var pri = cmp.Prior.Where(c => c.GroupName == group).ToDictionary(c => c.CategoryName, c => c.Amount);
            var cats = cur.Keys.Union(pri.Keys).OrderBy(k => cur.GetValueOrDefault(k)).ThenBy(k => pri.GetValueOrDefault(k)).ToList();
            GroupDetailHeight = ChartTheme.BarChartHeight(cats.Count);
            GroupDetailYAxes = [_t.CategoryAxis(cats.ToArray(), everyLabel: true)];
            GroupDetailXAxes = [_t.CurrencyAxis()];
            var color = _groupColors.GetValueOrDefault(group, Palette[0]);
            GroupDetailSeries =
            [
                new RowSeries<double> { Name = cmp.PriorMonth.ToString("MMM yyyy", CultureInfo.CurrentCulture), Values = cats.Select(k => (double)pri.GetValueOrDefault(k)).ToList(),
                    Fill = new SolidColorPaint(_t.Muted.WithAlpha(120)), MaxBarWidth = 16, Rx = 3, Ry = 3, XToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0") },
                new RowSeries<double> { Name = cmp.Month.ToString("MMM yyyy", CultureInfo.CurrentCulture), Values = cats.Select(k => (double)cur.GetValueOrDefault(k)).ToList(),
                    Fill = new SolidColorPaint(color), MaxBarWidth = 16, Rx = 3, Ry = 3, XToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0") },
            ];
        }
        else
        {
            GroupDetailSeries = [];
        }

        SpendingRows.Clear();
        var keys = cmp.Current.Select(c => (c.GroupId, c.GroupName, c.CategoryId, c.CategoryName))
            .Union(cmp.Prior.Select(c => (c.GroupId, c.GroupName, c.CategoryId, c.CategoryName)))
            .Where(k => SelectedGroup is null || k.GroupName == SelectedGroup);
        var currentMap = cmp.Current.ToDictionary(c => (c.GroupName, c.CategoryName), c => c.Amount);
        var priorMap = cmp.Prior.ToDictionary(c => (c.GroupName, c.CategoryName), c => c.Amount);
        foreach (var k in keys.OrderByDescending(k => currentMap.GetValueOrDefault((k.GroupName, k.CategoryName))).ThenByDescending(k => priorMap.GetValueOrDefault((k.GroupName, k.CategoryName))))
            SpendingRows.Add(new SpendingRow
            {
                GroupId = k.GroupId, CategoryId = k.CategoryId, Group = k.GroupName, Category = k.CategoryName,
                Current = currentMap.GetValueOrDefault((k.GroupName, k.CategoryName)), Prior = priorMap.GetValueOrDefault((k.GroupName, k.CategoryName)),
            });
    }

    private async Task LoadBudgetAsync()
    {
        var budget = await _budget.GetMonthAsync(SelectedBudgetMonth.Month);
        var targeted = budget.Lines.Where(l => l.Target is not null).ToList();
        HasBudgetTargets = targeted.Count > 0;
        BudgetTarget = budget.TotalTarget;
        BudgetActual = budget.TargetedActual;
        BudgetProjected = budget.TargetedProjected;
        BudgetGroupsOver = targeted.Count(l => l.Status == BudgetStatus.Over);
        var atRisk = targeted.Count(l => l.Status == BudgetStatus.AtRisk);
        BudgetActualDetail = budget.TotalTarget > 0 ? $"{budget.TargetedActual / budget.TotalTarget:0%} of target" + (budget.IsCurrentMonth ? $" · day {budget.DayOfMonth} of {budget.DaysInMonth}" : "") : "";
        BudgetProjectedDetail = budget.IsCurrentMonth
            ? $"{budget.TotalTarget - budget.TargetedProjected:+$#,##0;-$#,##0;$0} vs. target at month end"
            : $"{budget.TotalTarget - budget.TargetedActual:+$#,##0;-$#,##0;$0} vs. target";
        BudgetGroupsDetail = $"{atRisk} at risk · {targeted.Count} with a target";

        // Bars: one row per group, target vs actual (and projected while the month is open).
        var shown = budget.Lines.Where(l => l.Target is not null || l.Actual > 0).OrderBy(l => l.Target ?? 0).ThenBy(l => l.Actual).ToList();
        var labels = shown.Select(l => l.GroupName).ToArray();
        BudgetChartHeight = Math.Max(320, 46 * shown.Count + 80);
        BudgetYAxes = [_t.CategoryAxis(labels, everyLabel: true)];
        BudgetXAxes = [_t.CurrencyAxis()];
        var series = new List<RowSeries<double?>>
        {
            new() { Name = "Target", Values = shown.Select(l => l.Target is { } t ? (double?)t : null).ToList(),
                    Fill = new SolidColorPaint(_t.Muted.WithAlpha(110)), MaxBarWidth = 14, Rx = 3, Ry = 3, XToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0") },
            new() { Name = budget.IsCurrentMonth ? "Spent so far" : "Actual", Values = shown.Select(l => (double?)l.Actual).ToList(),
                    Fill = new SolidColorPaint(Palette[0]), MaxBarWidth = 14, Rx = 3, Ry = 3, XToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0") },
        };
        if (budget.IsCurrentMonth)
            series.Add(new() { Name = "Projected month end", Values = shown.Select(l => (double?)l.Projected).ToList(),
                               Fill = new SolidColorPaint(Palette[1].WithAlpha(170)), MaxBarWidth = 14, Rx = 3, Ry = 3, XToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0") });
        foreach (var s in series)
            s.ChartPointPointerDown += (_, point) => { if (point is not null) OnUi(() => BudgetHistoryGroup = BudgetRows.FirstOrDefault(r => r.Group == shown[point.Index].GroupName)); };
        BudgetSeries = series.Cast<ISeries>().ToArray();

        var keep = BudgetHistoryGroup?.Group;
        BudgetRows.Clear();
        foreach (var l in budget.Lines.Where(l => l.Target is not null || l.Actual > 0 || l.Forecast.Predicted > 0))
            BudgetRows.Add(new BudgetReportRow
            {
                GroupId = l.GroupId, Group = l.GroupName, Target = l.Target, Actual = l.Actual, Projected = l.Projected,
                Forecast = l.Forecast.Predicted, Used = l.Used, Status = l.Status,
            });
        _budgetHistoryGroup = BudgetRows.FirstOrDefault(r => r.Group == keep);
        OnPropertyChanged(nameof(BudgetHistoryGroup));
        await LoadBudgetHistoryAsync();

        BudgetNote = HasBudgetTargets
            ? "Click a bar or a row to chart that group's last 12 months; double-click a row to see its transactions. Set targets on the Budget page."
            : "No budget targets yet. Set them on the Budget page — Hoard suggests each from the last 12 months of spending.";
    }

    private async Task LoadBudgetHistoryAsync()
    {
        var end = SelectedBudgetMonth.Month;
        var start = end.AddMonths(-11);
        var history = await _budget.HistoryAsync(start, end);
        var group = BudgetHistoryGroup;
        var months = Enumerable.Range(0, 12).Select(i => start.AddMonths(i)).ToList();
        IEnumerable<BudgetHistoryPoint> Of(DateOnly m) => history.Where(h => h.Month == m && (group is null ? h.Target is not null : h.GroupName == group.Group));

        var actual = months.Select(m => (double)Of(m).Sum(h => h.Actual)).ToList();
        var target = months.Select(m => Of(m).Any(h => h.Target is not null) ? (double?)Of(m).Sum(h => h.Target ?? 0) : null).ToList();
        BudgetHistoryTitle = group is null
            ? "Groups with a target: spending vs. target, last 12 months"
            : $"{group.Group}: spending vs. target, last 12 months";
        BudgetHistoryXAxes = [_t.CategoryAxis(months.Select(m => m.ToString("MMM yy", CultureInfo.CurrentCulture)).ToArray())];
        BudgetHistoryYAxes = [_t.CurrencyAxis()];
        BudgetHistorySeries =
        [
            new ColumnSeries<double>
            {
                Name = "Actual",
                Values = actual,
                Fill = new SolidColorPaint(Palette[0]),
                MaxBarWidth = 36, Rx = 3, Ry = 3,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
            },
            new StepLineSeries<double?>
            {
                Name = "Target",
                Values = target,
                Stroke = new SolidColorPaint(Ink) { StrokeThickness = 2, PathEffect = new DashEffect([6, 4]) },
                Fill = null,
                GeometrySize = 0,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
            },
        ];
    }

    /// <summary>Chart pointer events can arrive off the UI thread; view-model changes must not.</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static DateTime ToDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}
