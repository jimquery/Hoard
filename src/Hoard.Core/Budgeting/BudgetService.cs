using Dapper;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Reporting;

namespace Hoard.Core.Budgeting;

/// <summary>One stored target change: from <see cref="EffectiveMonth"/> on, the group's monthly target is <see cref="MonthlyTarget"/> (null = none).</summary>
public sealed class BudgetTargetRow
{
    public long GroupId { get; set; }
    public DateTime EffectiveMonth { get; set; }
    public decimal? MonthlyTarget { get; set; }
}

public interface IBudgetRepository
{
    Task<IReadOnlyList<BudgetTargetRow>> ListTargetsAsync(CancellationToken ct = default);
    /// <summary>Sets (or with null, removes) a group's target from <paramref name="fromMonth"/> on.</summary>
    Task SetTargetAsync(long groupId, DateOnly fromMonth, decimal? monthlyTarget, CancellationToken ct = default);
}

public sealed class BudgetRepository : IBudgetRepository
{
    private readonly IDbConnectionFactory _f;
    public BudgetRepository(IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<BudgetTargetRow>> ListTargetsAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<BudgetTargetRow>(
            "SELECT group_id, effective_month, monthly_target FROM budget_targets ORDER BY group_id, effective_month")).ToList();
    }

    public async Task SetTargetAsync(long groupId, DateOnly fromMonth, decimal? monthlyTarget, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO budget_targets (group_id, effective_month, monthly_target, updated_at_utc)
            VALUES (@groupId, @month, @monthlyTarget, @now)
            ON DUPLICATE KEY UPDATE monthly_target = VALUES(monthly_target), updated_at_utc = VALUES(updated_at_utc)
            """, new { groupId, month = fromMonth.ToDateTime(TimeOnly.MinValue), monthlyTarget, now = DateTime.UtcNow });
    }
}

public enum BudgetStatus
{
    /// <summary>No target set for the group.</summary>
    NoTarget,
    /// <summary>Spent (or projected to spend) within the target.</summary>
    OnTrack,
    /// <summary>Current month: still under the target, but the month-end projection is over it.</summary>
    AtRisk,
    /// <summary>Already over the target.</summary>
    Over,
}

/// <summary>One category group in a budget month.</summary>
public sealed class BudgetLine
{
    /// <summary>The top-level expense category, or null for uncategorized spending (which cannot have a target).</summary>
    public long? GroupId { get; init; }
    public string GroupName { get; init; } = "";
    /// <summary>Spending in each of the 12 complete months before the budget month, oldest first.</summary>
    public IReadOnlyList<decimal> History { get; init; } = [];
    public BudgetForecast Forecast { get; init; } = BudgetForecast.Empty;
    public decimal? Target { get; init; }
    /// <summary>Spent in the budget month (so far, for the current month).</summary>
    public decimal Actual { get; init; }
    /// <summary>Month-end estimate (equal to <see cref="Actual"/> for a finished month).</summary>
    public decimal Projected { get; init; }
    public decimal? Remaining => Target is { } t ? t - Actual : null;
    /// <summary>Actual as a share of the target, e.g. 0.8 = 80 % used.</summary>
    public decimal? Used => Target is { } t && t > 0 ? Actual / t : null;
    public BudgetStatus Status { get; init; }
}

public sealed class BudgetMonth
{
    public DateOnly Month { get; init; }
    public bool IsCurrentMonth { get; init; }
    public int DayOfMonth { get; init; }
    public int DaysInMonth { get; init; }
    public IReadOnlyList<BudgetLine> Lines { get; init; } = [];
    public decimal TotalTarget => Lines.Sum(l => l.Target ?? 0);
    public decimal TotalActual => Lines.Sum(l => l.Actual);
    public decimal TotalProjected => Lines.Sum(l => l.Projected);
    public decimal TotalPredicted => Lines.Sum(l => l.Forecast.Predicted);
    /// <summary>Actual spending of the groups that have a target (the like-for-like figure to compare with <see cref="TotalTarget"/>).</summary>
    public decimal TargetedActual => Lines.Where(l => l.Target is not null).Sum(l => l.Actual);
    public decimal TargetedProjected => Lines.Where(l => l.Target is not null).Sum(l => l.Projected);
}

/// <summary>Target and actual for one group in one month, for the target-vs-actual history.</summary>
public sealed record BudgetHistoryPoint(DateOnly Month, long? GroupId, string GroupName, decimal? Target, decimal Actual);

/// <summary>
/// Budgets by category group (the top-level expense categories): forecasts from the 12 complete months before the
/// month being viewed, monthly targets with history, and actual spending measured exactly as the Spending report does
/// (spending-report account types only, transfers and income excluded, uncategorized money-out counted).
/// </summary>
public sealed class BudgetService
{
    public const int HistoryMonths = 12;
    public const string UncategorizedName = "Uncategorized";

    private readonly IBudgetRepository _budgets;
    private readonly IReportRepository _reports;
    private readonly ICategoryRepository _categories;

    public BudgetService(IBudgetRepository budgets, IReportRepository reports, ICategoryRepository categories)
    {
        _budgets = budgets;
        _reports = reports;
        _categories = categories;
    }

    public static DateOnly MonthOf(DateOnly d) => new(d.Year, d.Month, 1);

    /// <summary>The budget for <paramref name="month"/> as of <paramref name="today"/> (defaults to the current date).</summary>
    public async Task<BudgetMonth> GetMonthAsync(DateOnly month, DateOnly? today = null, CancellationToken ct = default)
    {
        month = MonthOf(month);
        var now = today ?? DateOnly.FromDateTime(DateTime.Today);
        var isCurrent = month == MonthOf(now);
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        var dayOfMonth = isCurrent ? now.Day : daysInMonth;

        // The forecast reads the 12 complete months before the month shown; planning a future month still reads the
        // 12 before the current one, so a half-finished month never counts as history.
        var historyEnd = month > MonthOf(now) ? MonthOf(now) : month;
        var from = historyEnd.AddMonths(-HistoryMonths);
        var spend = await _reports.MonthlySpendingByGroupAsync(from, month.AddMonths(1), ct);
        var groups = await ExpenseGroupsAsync(ct);
        var targets = await _budgets.ListTargetsAsync(ct);

        var historyMonths = Enumerable.Range(0, HistoryMonths).Select(i => from.AddMonths(i)).ToList();
        var byKey = spend.GroupBy(s => (s.GroupId, s.Month)).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));

        // Every expense group, plus any other group that actually has spending (an uncategorized line, or a group whose
        // parent category is not of the Expense kind but still had money out).
        var keys = groups.Select(g => (Id: (long?)g.Id, g.Name)).ToList();
        foreach (var s in spend.Where(s => s.GroupId is null || groups.All(g => g.Id != s.GroupId)).GroupBy(s => s.GroupId))
            keys.Add((s.Key, s.Key is null ? UncategorizedName : s.First().GroupName));

        var lines = new List<BudgetLine>();
        foreach (var (id, name) in keys)
        {
            var history = historyMonths.Select(m => byKey.GetValueOrDefault((id, m))).ToList();
            var actual = byKey.GetValueOrDefault((id, month));
            var forecast = BudgetForecaster.Forecast(history);
            var target = id is { } gid ? TargetFor(targets, gid, month) : null;
            var projected = isCurrent ? BudgetForecaster.ProjectMonthEnd(actual, forecast.Predicted, dayOfMonth, daysInMonth) : actual;
            if (forecast == BudgetForecast.Empty && actual == 0 && target is null && id is null) continue;
            lines.Add(new BudgetLine
            {
                GroupId = id,
                GroupName = name,
                History = history,
                Forecast = forecast,
                Target = target,
                Actual = actual,
                Projected = projected,
                Status = StatusOf(target, actual, projected, isCurrent),
            });
        }

        return new BudgetMonth
        {
            Month = month,
            IsCurrentMonth = isCurrent,
            DayOfMonth = dayOfMonth,
            DaysInMonth = daysInMonth,
            Lines = lines.OrderByDescending(l => l.Target ?? 0).ThenByDescending(l => l.Forecast.Predicted).ThenBy(l => l.GroupName).ToList(),
        };
    }

    public static BudgetStatus StatusOf(decimal? target, decimal actual, decimal projected, bool isCurrentMonth)
    {
        if (target is not { } t) return BudgetStatus.NoTarget;
        if (actual > t) return BudgetStatus.Over;
        if (isCurrentMonth && projected > t) return BudgetStatus.AtRisk;
        return BudgetStatus.OnTrack;
    }

    /// <summary>The target in force for a group in a month: the latest change on or before it.</summary>
    public static decimal? TargetFor(IReadOnlyList<BudgetTargetRow> targets, long groupId, DateOnly month)
    {
        var at = month.ToDateTime(TimeOnly.MinValue);
        return targets.Where(t => t.GroupId == groupId && t.EffectiveMonth <= at)
                      .OrderByDescending(t => t.EffectiveMonth)
                      .FirstOrDefault()?.MonthlyTarget;
    }

    /// <summary>Sets a group's monthly target from <paramref name="fromMonth"/> on (null or zero removes it). Earlier months keep theirs.</summary>
    public Task SetTargetAsync(long groupId, decimal? monthlyTarget, DateOnly fromMonth, CancellationToken ct = default) =>
        _budgets.SetTargetAsync(groupId, MonthOf(fromMonth), monthlyTarget is > 0 ? Math.Round(monthlyTarget.Value, 2) : null, ct);

    /// <summary>Sets every expense group without a target this month to its suggested (forecast) target. Returns how many were set.</summary>
    public async Task<int> ApplySuggestedTargetsAsync(DateOnly month, bool overwrite, CancellationToken ct = default)
    {
        var budget = await GetMonthAsync(month, ct: ct);
        var set = 0;
        foreach (var line in budget.Lines.Where(l => l.GroupId is not null && l.Forecast.SuggestedTarget > 0 && (overwrite || l.Target is null)))
        {
            await SetTargetAsync(line.GroupId!.Value, line.Forecast.SuggestedTarget, month, ct);
            set++;
        }
        return set;
    }

    /// <summary>Target and actual per group per month for the months from <paramref name="fromMonth"/> through <paramref name="toMonth"/>.</summary>
    public async Task<IReadOnlyList<BudgetHistoryPoint>> HistoryAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken ct = default)
    {
        fromMonth = MonthOf(fromMonth);
        toMonth = MonthOf(toMonth);
        var spend = await _reports.MonthlySpendingByGroupAsync(fromMonth, toMonth.AddMonths(1), ct);
        var groups = await ExpenseGroupsAsync(ct);
        var targets = await _budgets.ListTargetsAsync(ct);
        var names = groups.ToDictionary(g => (long?)g.Id, g => g.Name);
        foreach (var s in spend) names.TryAdd(s.GroupId, s.GroupId is null ? UncategorizedName : s.GroupName);
        var byKey = spend.GroupBy(s => (s.GroupId, s.Month)).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));

        var points = new List<BudgetHistoryPoint>();
        for (var m = fromMonth; m <= toMonth; m = m.AddMonths(1))
            foreach (var (id, name) in names)
            {
                var target = id is { } gid ? TargetFor(targets, gid, m) : null;
                var actual = byKey.GetValueOrDefault((id, m));
                if (target is null && actual == 0) continue;
                points.Add(new BudgetHistoryPoint(m, id, name, target, actual));
            }
        return points;
    }

    private async Task<List<TransactionCategory>> ExpenseGroupsAsync(CancellationToken ct) =>
        (await _categories.ListCategoriesAsync(includeHidden: false, ct))
            .Where(c => c.ParentId is null && c.Kind == CategoryKind.Expense)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList();
}
