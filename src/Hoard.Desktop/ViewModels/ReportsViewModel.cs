using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Reporting;
using Hoard.Desktop.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
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
    public string Month { get; init; } = "";
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal Net { get; init; }
    public decimal Uncategorized { get; init; }
}

public sealed class SpendingRow
{
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

public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly ReportingService _reports;
    private readonly IAccountRepository _accounts;
    private readonly ChartTheme _t;
    private SKColor[] Palette => _t.Palette;
    private SKColor Ink => _t.Ink;
    private const string OtherName = "Other";

    public ReportsViewModel(ReportingService reports, IAccountRepository accounts, ThemeService theme)
    {
        _reports = reports;
        _accounts = accounts;
        _t = new ChartTheme(theme);
        var thisMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        for (var i = 0; i < 36; i++) MonthOptions.Add(new MonthOption(thisMonth.AddMonths(-i)));
        _selectedMonth = MonthOptions[0];
        _selectedRange = RangeOptions[2];
        _selectedIncomeRange = IncomeRangeOptions[1];
        // Every chart gets its OWN axis instances: LiveCharts attaches an axis to the first chart that draws it,
        // so sharing one axis object between charts leaves the others blank. Axes also must never be empty.
        _netWorthYAxes = [_t.CurrencyAxis()];
        _incomeYAxes = [_t.CurrencyAxis()];
        _spendingTrendYAxes = [_t.CurrencyAxis()];
        _spendingCompareYAxes = [_t.CurrencyAxis()];
        _collegeYAxes = [_t.CurrencyAxis()];
        _netWorthXAxes = [_t.DateAxis(30)];
        _incomeXAxes = [_t.CategoryAxis([])];
        _spendingTrendXAxes = [_t.CategoryAxis([])];
        _spendingCompareXAxes = [_t.CategoryAxis([])];
        _typeXAxes = [_t.CurrencyAxis()];
        _typeYAxes = [_t.CategoryAxis([])];
        _subscriptionXAxes = [_t.CurrencyAxis()];
        _subscriptionYAxes = [_t.CategoryAxis([])];
        _collegeXAxes = [_t.CategoryAxis([])];
        theme.Changed += () => { if (_loadedOnce) _ = LoadAllAsync(); };
    }

    private bool _loadedOnce;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    /// <summary>Which report tab is showing (0 net worth, 1 income vs expenses, 2 spending, 3 balances by type).</summary>
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
    [ObservableProperty] private string _netWorthHeadline = "";
    [ObservableProperty] private Axis[] _netWorthYAxes = [];

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
    public ObservableCollection<IncomeExpenseRow> IncomeRows { get; } = new();

    partial void OnSelectedIncomeRangeChanged(RangeOption value) => _ = LoadIncomeAsync();

    // ---------- Spending ----------
    public ObservableCollection<MonthOption> MonthOptions { get; } = new();
    [ObservableProperty] private MonthOption _selectedMonth;
    [ObservableProperty] private bool _compareWithPriorYear = true;
    [ObservableProperty] private ISeries[] _spendingTrendSeries = [];
    [ObservableProperty] private Axis[] _spendingTrendXAxes = [];
    [ObservableProperty] private Axis[] _spendingTrendYAxes = [];
    [ObservableProperty] private ISeries[] _spendingCompareSeries = [];
    [ObservableProperty] private Axis[] _spendingCompareXAxes = [];
    [ObservableProperty] private Axis[] _spendingCompareYAxes = [];
    [ObservableProperty] private string _spendingHeadline = "";
    [ObservableProperty] private string _spendingNote = "";
    public ObservableCollection<SpendingRow> SpendingRows { get; } = new();

    partial void OnSelectedMonthChanged(MonthOption value) => _ = LoadSpendingAsync();
    partial void OnCompareWithPriorYearChanged(bool value) => _ = LoadSpendingAsync();

    [RelayCommand]
    public async Task LoadAllAsync()
    {
        IsBusy = true;
        try
        {
            await LoadNetWorthAsync();
            await LoadIncomeAsync();
            await LoadSpendingAsync();
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
            NetWorthHeadline = "";
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
        var span = points[^1].Date.DayNumber - points[0].Date.DayNumber;
        NetWorthXAxes = [_t.DateAxis(span)];
        NetWorthYAxes = [_t.CurrencyAxis()];

        var last = points[^1];
        var first = points[0];
        var change = last.NetWorth - first.NetWorth;
        NetWorthHeadline = $"{last.NetWorth:C0} net worth  ·  {change:+#,##0;-#,##0;0} since {first.Date:MMM d, yyyy}";
        var estimatedDays = points.Count(p => p.IsEstimated);
        NetWorthNote = observedOnlyEmpty
            ? "No day has an observed balance for every account yet, so estimated history is shown; each sync records the balances of that day."
            : estimatedDays == 0
                ? $"All {points.Count} day(s) shown were observed from bank balances." + (IncludeEstimated ? "" : " Tick \"Include estimated history\" to fill in the days between syncs from transaction history.")
                : $"{estimatedDays} of {points.Count} days are estimated by rolling balances backward from the first sync using transaction history" +
                  (report.ReliableFrom is { } r ? $"; every account has data from {r:MMM d, yyyy}." : ".");
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

        var labels = rows.Select(r => r.Month.ToString("MMM yy", CultureInfo.CurrentCulture)).ToArray();
        IncomeSeries =
        [
            _t.Column("Income", rows.Select(r => (double)r.Income), Palette[0]),
            _t.Column("Expenses", rows.Select(r => (double)r.Expense), Palette[1]),
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
        foreach (var r in rows)
            IncomeRows.Add(new IncomeExpenseRow
            {
                Month = r.Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
                Income = r.Income, Expense = r.Expense, Net = r.Net,
                Uncategorized = r.UncategorizedIncome + r.UncategorizedExpense,
            });

        var totalIncome = rows.Sum(r => r.Income);
        var totalExpense = rows.Sum(r => r.Expense);
        var unc = rows.Sum(r => r.UncategorizedIncome + r.UncategorizedExpense);
        IncomeNote = $"{months} months: income {totalIncome:C0}, expenses {totalExpense:C0}, net {totalIncome - totalExpense:+#,##0;-#,##0;0}. Transfers are excluded." +
                     (unc > 0 ? $" {unc:C0} of uncategorized activity was counted by sign — categorize it for accuracy." : "") +
                     " " + await SpendingScopeNoteAsync();
    }

    private async Task LoadSpendingAsync()
    {
        var month = SelectedMonth.Month;

        // 12-month trend by category group, stacked.
        var trend = await _reports.SpendingTrendAsync(month, 12);
        var months = Enumerable.Range(0, 12).Select(i => month.AddMonths(-(11 - i))).ToList();
        var groupTotals = trend.GroupBy(t => t.GroupName).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var groups = groupTotals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        var topGroups = groups.Take(Palette.Length - 1).ToList();
        var folded = groups.Skip(Palette.Length - 1).ToHashSet();
        var seriesNames = folded.Count > 0 ? topGroups.Append(OtherName).ToList() : topGroups;

        decimal TrendValue(DateOnly m, string g) => g == OtherName
            ? trend.Where(t => t.Month == m && folded.Contains(t.GroupName)).Sum(t => t.Amount)
            : trend.Where(t => t.Month == m && t.GroupName == g).Sum(t => t.Amount);

        SpendingTrendSeries = seriesNames.Select((g, i) => (ISeries)new StackedColumnSeries<double>
        {
            Name = g,
            Values = months.Select(m => (double)TrendValue(m, g)).ToList(),
            Fill = new SolidColorPaint(Palette[i % Palette.Length]),
            Stroke = new SolidColorPaint(_t.Surface) { StrokeThickness = 1 },
            MaxBarWidth = 48,
            Rx = 2, Ry = 2,
            YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
        }).ToArray();
        SpendingTrendXAxes = [_t.CategoryAxis(months.Select(m => m.ToString("MMM yy", CultureInfo.CurrentCulture)).ToArray())];
        SpendingTrendYAxes = [_t.CurrencyAxis()];

        // Selected month vs. same month last year, by group; table by sub-category.
        var cmp = await _reports.SpendingComparisonAsync(month);
        var currentByGroup = cmp.Current.GroupBy(c => c.GroupName).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));
        var priorByGroup = cmp.Prior.GroupBy(c => c.GroupName).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));
        var cmpGroups = currentByGroup.Keys.Union(priorByGroup.Keys)
            .OrderByDescending(g => currentByGroup.GetValueOrDefault(g) + priorByGroup.GetValueOrDefault(g)).ToList();

        var compareSeries = new List<ISeries>
        {
            _t.Column(month.ToString("MMM yyyy", CultureInfo.CurrentCulture), cmpGroups.Select(g => (double)currentByGroup.GetValueOrDefault(g)), Palette[0]),
        };
        if (CompareWithPriorYear)
            compareSeries.Add(_t.Column(cmp.PriorMonth.ToString("MMM yyyy", CultureInfo.CurrentCulture), cmpGroups.Select(g => (double)priorByGroup.GetValueOrDefault(g)), Palette[1]));
        SpendingCompareSeries = compareSeries.ToArray();
        SpendingCompareXAxes = [_t.CategoryAxis(cmpGroups.ToArray(), rotation: 20)];
        SpendingCompareYAxes = [_t.CurrencyAxis()];

        SpendingRows.Clear();
        var keys = cmp.Current.Select(c => (c.GroupName, c.CategoryName)).Union(cmp.Prior.Select(c => (c.GroupName, c.CategoryName)));
        var currentMap = cmp.Current.ToDictionary(c => (c.GroupName, c.CategoryName), c => c.Amount);
        var priorMap = cmp.Prior.ToDictionary(c => (c.GroupName, c.CategoryName), c => c.Amount);
        foreach (var k in keys.OrderByDescending(k => currentMap.GetValueOrDefault(k)).ThenByDescending(k => priorMap.GetValueOrDefault(k)))
            SpendingRows.Add(new SpendingRow { Group = k.GroupName, Category = k.CategoryName, Current = currentMap.GetValueOrDefault(k), Prior = priorMap.GetValueOrDefault(k) });

        SpendingNote = await SpendingScopeNoteAsync();
        var delta = cmp.CurrentTotal - cmp.PriorTotal;
        SpendingHeadline = CompareWithPriorYear
            ? $"{cmp.Month:MMMM yyyy}: {cmp.CurrentTotal:C0} spent  ·  {cmp.PriorMonth:MMMM yyyy}: {cmp.PriorTotal:C0}  ·  {delta:+#,##0;-#,##0;0}" +
              (cmp.PriorTotal > 0 ? $" ({delta / cmp.PriorTotal:+0%;-0%;0%})" : "")
            : $"{cmp.Month:MMMM yyyy}: {cmp.CurrentTotal:C0} spent across {cmp.Current.Count} categories";
    }

    private static DateTime ToDate(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);
}
