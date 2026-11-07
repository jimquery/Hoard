using Hoard.Core.Budgeting;
using Hoard.Core.Categorization;

namespace Hoard.Core.Reporting.Weekly;

public sealed record DaySpend(DateOnly Date, decimal Amount);
public sealed record WeekTotal(DateOnly Start, decimal Amount);
public sealed record MerchantSpend(string Name, decimal Amount, int Count);
public sealed record WeeklyTransaction(DateOnly Date, string Merchant, string Description, string Account, string? Category, decimal Amount);

/// <summary>One category group's week against its own recent weeks.</summary>
public sealed record GroupWeekSpend(string Name, decimal Amount, decimal FourWeekAverage, decimal Share)
{
    public decimal Change => Amount - FourWeekAverage;
}

/// <summary>Where the month the week ends in stands against its budget targets.</summary>
public sealed class BudgetSnapshot
{
    public DateOnly Month { get; init; }
    public int DayOfMonth { get; init; }
    public int DaysInMonth { get; init; }
    public decimal Target { get; init; }
    public decimal Spent { get; init; }
    public decimal Projected { get; init; }
    public IReadOnlyList<(string Group, decimal Actual, decimal Target)> Over { get; init; } = [];
    public IReadOnlyList<(string Group, decimal Projected, decimal Target)> AtRisk { get; init; } = [];
}

/// <summary>A week of spending: totals against recent weeks, by day, by category group, top merchants, largest charges.</summary>
public sealed class WeeklySpendingReport
{
    public DateOnly Start { get; init; }
    public DateOnly EndExclusive { get; init; }
    public DateOnly End => EndExclusive.AddDays(-1);
    public decimal Total { get; init; }
    public int TransactionCount { get; init; }
    public decimal PriorWeek { get; init; }
    /// <summary>Average of the four weeks before this one.</summary>
    public decimal FourWeekAverage { get; init; }
    /// <summary>Average of the twelve weeks before this one.</summary>
    public decimal TwelveWeekAverage { get; init; }
    public IReadOnlyList<DaySpend> Days { get; init; } = [];
    public IReadOnlyList<GroupWeekSpend> Groups { get; init; } = [];
    public IReadOnlyList<MerchantSpend> TopMerchants { get; init; } = [];
    public IReadOnlyList<WeeklyTransaction> Largest { get; init; } = [];
    /// <summary>The 12 weeks before this one plus this one, oldest first.</summary>
    public IReadOnlyList<WeekTotal> Trend { get; init; } = [];
    public BudgetSnapshot? Budget { get; init; }
    /// <summary>Average monthly income (last 6 complete months) spread over a week, for "spending pace vs. income".</summary>
    public decimal? WeeklyIncome { get; set; }
    /// <summary>Claude's Markdown analysis, when it was requested and succeeded.</summary>
    public string? AiAnalysis { get; set; }
    /// <summary>Why there is no analysis (advisor off, API error), shown in its place.</summary>
    public string? AiNote { get; set; }

    public string Title => $"Weekly spending, {Start:MMM d} – {End:MMM d, yyyy}";
}

/// <summary>Builds a <see cref="WeeklySpendingReport"/> from spending rows; pure, so it is tested without a database.</summary>
public static class WeeklyReportBuilder
{
    public const int TrendWeeks = 12;
    private const int TopMerchantCount = 8;
    private const int LargestCount = 8;

    /// <summary>
    /// <paramref name="rows"/> must cover at least the <see cref="TrendWeeks"/> weeks before <paramref name="start"/> and the
    /// week itself; anything outside that is ignored.
    /// </summary>
    public static WeeklySpendingReport Build(DateOnly start, IReadOnlyList<Reporting.SpendingActivityRow> rows, BudgetMonth? budget = null)
    {
        var end = start.AddDays(7);
        var historyStart = start.AddDays(-7 * TrendWeeks);
        static DateOnly Day(Reporting.SpendingActivityRow r) => DateOnly.FromDateTime(r.PostedAtUtc);
        var week = rows.Where(r => Day(r) >= start && Day(r) < end).ToList();
        var history = rows.Where(r => Day(r) >= historyStart && Day(r) < start).ToList();

        decimal WeekSum(DateOnly s) => rows.Where(r => Day(r) >= s && Day(r) < s.AddDays(7)).Sum(r => r.Spent);
        var trend = Enumerable.Range(0, TrendWeeks + 1).Select(i => historyStart.AddDays(7 * i)).Select(s => new WeekTotal(s, WeekSum(s))).ToList();
        var prior = trend.Take(TrendWeeks).ToList();
        var total = week.Sum(r => r.Spent);

        var fourWeekStart = start.AddDays(-28);
        var recent = history.Where(r => Day(r) >= fourWeekStart).ToList();
        var groupNames = week.Select(r => r.GroupName).Union(recent.Select(r => r.GroupName)).Distinct();
        var groups = groupNames
            .Select(g => new GroupWeekSpend(g, week.Where(r => r.GroupName == g).Sum(r => r.Spent),
                recent.Where(r => r.GroupName == g).Sum(r => r.Spent) / 4m,
                total > 0 ? week.Where(r => r.GroupName == g).Sum(r => r.Spent) / total : 0))
            .Where(g => g.Amount != 0 || g.FourWeekAverage != 0)
            .OrderByDescending(g => g.Amount).ThenByDescending(g => g.FourWeekAverage).ToList();

        var merchants = week.Where(r => r.Spent > 0)
            .GroupBy(r => Merchant(r))
            .Select(g => new MerchantSpend(g.Key, g.Sum(r => r.Spent), g.Count()))
            .OrderByDescending(m => m.Amount).Take(TopMerchantCount).ToList();

        var largest = week.Where(r => r.Spent > 0).OrderByDescending(r => r.Spent).Take(LargestCount)
            .Select(r => new WeeklyTransaction(Day(r), Merchant(r), r.Description, r.AccountName, r.CategoryName, r.Spent)).ToList();

        return new WeeklySpendingReport
        {
            Start = start,
            EndExclusive = end,
            Total = total,
            TransactionCount = week.Count,
            PriorWeek = prior.Count > 0 ? prior[^1].Amount : 0,
            FourWeekAverage = prior.TakeLast(4).Select(w => w.Amount).DefaultIfEmpty(0).Average(),
            TwelveWeekAverage = prior.Select(w => w.Amount).DefaultIfEmpty(0).Average(),
            Days = Enumerable.Range(0, 7).Select(i => start.AddDays(i)).Select(d => new DaySpend(d, week.Where(r => Day(r) == d).Sum(r => r.Spent))).ToList(),
            Groups = groups,
            TopMerchants = merchants,
            Largest = largest,
            Trend = trend,
            Budget = budget is null ? null : Snapshot(budget),
        };
    }

    /// <summary>A readable merchant: the payee when the bank gave one, else the cleaned-up description key.</summary>
    public static string Merchant(Reporting.SpendingActivityRow r)
    {
        if (!string.IsNullOrWhiteSpace(r.Payee)) return r.Payee.Trim();
        var key = MerchantKey.Extract(r.Description);
        return key.Length > 0 ? SubscriptionDetector.DisplayName(key) : r.Description.Trim();
    }

    private static BudgetSnapshot Snapshot(BudgetMonth b)
    {
        var targeted = b.Lines.Where(l => l.Target is not null).ToList();
        return new BudgetSnapshot
        {
            Month = b.Month,
            DayOfMonth = b.DayOfMonth,
            DaysInMonth = b.DaysInMonth,
            Target = b.TotalTarget,
            Spent = b.TargetedActual,
            Projected = b.TargetedProjected,
            Over = targeted.Where(l => l.Status == BudgetStatus.Over).Select(l => (l.GroupName, l.Actual, l.Target!.Value)).ToList(),
            AtRisk = targeted.Where(l => l.Status == BudgetStatus.AtRisk).Select(l => (l.GroupName, l.Projected, l.Target!.Value)).ToList(),
        };
    }
}
