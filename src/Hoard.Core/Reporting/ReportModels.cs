using Hoard.Core.Domain;

namespace Hoard.Core.Reporting;

/// <summary>One account's balance on one day, either observed (snapshot) or reconstructed from transactions.</summary>
public readonly record struct DailyBalance(DateOnly Date, decimal Balance, bool IsEstimated);

/// <summary>Net worth on one day, broken down by account type.</summary>
public sealed class NetWorthPoint
{
    public DateOnly Date { get; init; }
    public decimal Assets { get; init; }
    /// <summary>Negative number (liabilities are stored as negative balances).</summary>
    public decimal Liabilities { get; init; }
    public decimal NetWorth => Assets + Liabilities;
    /// <summary>Balance per account type name ("Cash", "Retirement", …, "(untyped)"). Liabilities are negative.</summary>
    public IReadOnlyDictionary<string, decimal> ByType { get; init; } = new Dictionary<string, decimal>();
    /// <summary>True when any account's balance on this day was reconstructed rather than observed.</summary>
    public bool IsEstimated { get; init; }
}

public sealed class NetWorthReport
{
    public IReadOnlyList<NetWorthPoint> Points { get; init; } = [];
    /// <summary>Account type names in display order (assets first, then liabilities).</summary>
    public IReadOnlyList<string> AssetTypes { get; init; } = [];
    public IReadOnlyList<string> LiabilityTypes { get; init; } = [];
    /// <summary>Earliest date on which every active account has data (observed or reconstructable). Charts default to start here.</summary>
    public DateOnly? ReliableFrom { get; init; }
    public DateOnly? EarliestData { get; init; }
    /// <summary>Configured chart color per account type name (#RRGGBB), when set.</summary>
    public IReadOnlyDictionary<string, string> TypeColors { get; init; } = new Dictionary<string, string>();
}

/// <summary>One account type with its accounts and totals, for the dashboard and balances-by-type report.</summary>
public sealed class TypeBalanceGroup
{
    public long? TypeId { get; init; }
    public string Name { get; init; } = "";
    public AccountKind Kind { get; init; }
    public string? Color { get; init; }
    public int SortOrder { get; init; }
    public IReadOnlyList<AccountBalanceRow> Accounts { get; init; } = [];
    /// <summary>Sum of balances of accounts that count toward net worth.</summary>
    public decimal Total => Accounts.Where(a => !a.ExcludeFromNetWorth).Sum(a => a.Balance);
    public decimal ExcludedTotal => Accounts.Where(a => a.ExcludeFromNetWorth).Sum(a => a.Balance);
    public int AccountCount => Accounts.Count;
}

public sealed class BalancesByTypeReport
{
    public IReadOnlyList<TypeBalanceGroup> Groups { get; init; } = [];
    public decimal Assets => Groups.Where(g => g.Kind == AccountKind.Asset).Sum(g => g.Total);
    /// <summary>Negative number.</summary>
    public decimal Liabilities => Groups.Where(g => g.Kind == AccountKind.Liability).Sum(g => g.Total);
    public decimal NetWorth => Assets + Liabilities;
    public int AccountCount => Groups.Sum(g => g.AccountCount);
    public int ExcludedCount => Groups.Sum(g => g.Accounts.Count(a => a.ExcludeFromNetWorth));
    public DateTime? LatestBalanceUtc => Groups.SelectMany(g => g.Accounts).Max(a => a.BalanceDateUtc);
}

public enum ContributionSource { Planned = 1, PageOverride = 2, DepositAverage = 3 }

public sealed class CollegeAccountProjection
{
    public long AccountId { get; init; }
    public string Name { get; init; } = "";
    public string BankName { get; init; } = "";
    public decimal Balance { get; init; }
    /// <summary>The account's planned figure, else the page-level override, else the trailing-12-month average of deposits.</summary>
    public decimal MonthlyContribution { get; init; }
    public ContributionSource ContributionSource { get; init; }
    /// <summary>The per-account figure stored on the account (null when it relies on the fallbacks).</summary>
    public decimal? PlannedMonthlyContribution { get; init; }
    public decimal? ProjectedAt18 { get; init; }
}

public sealed class CollegeOwnerProjection
{
    public long? OwnerId { get; init; }
    public string OwnerName { get; init; } = "";
    public DateOnly? DateOfBirth { get; init; }
    public int? AgeNow { get; init; }
    public DateOnly? EighteenthBirthday { get; init; }
    public int? MonthsTo18 { get; init; }
    public IReadOnlyList<CollegeAccountProjection> Accounts { get; init; } = [];
    public decimal CurrentTotal => Accounts.Sum(a => a.Balance);
    public decimal MonthlyContribution => Accounts.Sum(a => a.MonthlyContribution);
    public decimal? ProjectedAt18 => Accounts.Any(a => a.ProjectedAt18 is not null) ? Accounts.Sum(a => a.ProjectedAt18 ?? a.Balance) : null;
    public bool AlreadyEighteen => MonthsTo18 == 0 && DateOfBirth is not null;
}

public sealed class CollegeSavingsReport
{
    public string TypeName { get; init; } = "";
    public decimal AnnualRate { get; init; }
    public DateOnly AsOf { get; init; }
    public IReadOnlyList<CollegeOwnerProjection> Owners { get; init; } = [];
    public decimal CurrentTotal => Owners.Sum(o => o.CurrentTotal);
    public decimal ProjectedTotal => Owners.Sum(o => o.ProjectedAt18 ?? o.CurrentTotal);
}

public sealed class MonthlyIncomeExpense
{
    public DateOnly Month { get; init; }
    public decimal Income { get; init; }
    /// <summary>Positive magnitude.</summary>
    public decimal Expense { get; init; }
    public decimal Net => Income - Expense;
    public decimal UncategorizedIncome { get; init; }
    public decimal UncategorizedExpense { get; init; }
}

public sealed class CategorySpend
{
    public long? GroupId { get; init; }
    public string GroupName { get; init; } = "";
    public long? CategoryId { get; init; }
    public string CategoryName { get; init; } = "";
    /// <summary>Positive magnitude of spending.</summary>
    public decimal Amount { get; init; }
    public int TransactionCount { get; init; }
}

public sealed class MonthGroupSpend
{
    public DateOnly Month { get; init; }
    public string GroupName { get; init; } = "";
    public decimal Amount { get; init; }
}

/// <summary>Spending in one month compared with the same month a year earlier.</summary>
public sealed class SpendingComparison
{
    public DateOnly Month { get; init; }
    public DateOnly PriorMonth { get; init; }
    public IReadOnlyList<CategorySpend> Current { get; init; } = [];
    public IReadOnlyList<CategorySpend> Prior { get; init; } = [];
    public decimal CurrentTotal => Current.Sum(c => c.Amount);
    public decimal PriorTotal => Prior.Sum(c => c.Amount);
}

/// <summary>One expense transaction from a spending-report account, as the subscription detector sees it.</summary>
public sealed class SpendingTransactionRow
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public string AccountName { get; init; } = "";
    public string InstitutionName { get; init; } = "";
    public DateTime PostedAtUtc { get; init; }
    /// <summary>Signed as stored (negative = money out).</summary>
    public decimal Amount { get; init; }
    public string Description { get; init; } = "";
    public string? Payee { get; init; }
    public string? CategoryName { get; init; }
}

public enum SubscriptionCadence
{
    Monthly = 1,
    Quarterly = 2,
    Yearly = 3,
}

/// <summary>One charge that belongs to a detected subscription.</summary>
public sealed class SubscriptionCharge
{
    public long TransactionId { get; init; }
    public DateOnly PostedOn { get; init; }
    /// <summary>Positive magnitude.</summary>
    public decimal Amount { get; init; }
    public string Description { get; init; } = "";
    public string AccountName { get; init; } = "";
    public string? CategoryName { get; init; }
}

/// <summary>A vendor charging the same (or nearly the same) amount on a regular cadence.</summary>
public sealed class Subscription
{
    /// <summary>Merchant key the charges were grouped by (upper case, noise stripped).</summary>
    public string VendorKey { get; init; } = "";
    /// <summary>Display name derived from the key.</summary>
    public string Vendor { get; init; } = "";
    public SubscriptionCadence Cadence { get; init; }
    /// <summary>Median charge over the whole run.</summary>
    public decimal TypicalAmount { get; init; }
    /// <summary>The most recent charge; what the subscription costs today.</summary>
    public decimal LatestAmount { get; init; }
    /// <summary>What a year costs at the latest amount and the detected cadence.</summary>
    public decimal AnnualCost { get; init; }
    public int Occurrences => Charges.Count;
    public DateOnly FirstCharge { get; init; }
    public DateOnly LastCharge { get; init; }
    /// <summary>The last charge is recent enough that the next one is still expected.</summary>
    public bool IsActive { get; init; }
    /// <summary>Accounts the charges hit, comma-separated.</summary>
    public string Accounts { get; init; } = "";
    public string? CategoryName { get; init; }
    public IReadOnlyList<SubscriptionCharge> Charges { get; init; } = [];
}

public sealed class SubscriptionReport
{
    public DateOnly From { get; init; }
    public DateOnly AsOf { get; init; }
    public int TransactionsExamined { get; init; }
    public IReadOnlyList<Subscription> Subscriptions { get; init; } = [];
    public IReadOnlyList<Subscription> Active => Subscriptions.Where(s => s.IsActive).ToList();
    public decimal ActiveAnnualCost => Subscriptions.Where(s => s.IsActive).Sum(s => s.AnnualCost);
}
