using Dapper;
using Hoard.Core.Domain;

namespace Hoard.Core.Reporting;

public sealed class AccountSnapshotRow
{
    public long AccountId { get; set; }
    public DateTime AsOf { get; set; }
    public decimal Balance { get; set; }
}

public sealed class DailyTotalRow
{
    public long AccountId { get; set; }
    public DateTime Day { get; set; }
    public decimal Total { get; set; }
}

public sealed class ReportAccountRow
{
    public long AccountId { get; set; }
    public string Name { get; set; } = "";
    public string? TypeName { get; set; }
    public AccountKind? TypeKind { get; set; }
    public string? TypeColor { get; set; }
    public int TypeSortOrder { get; set; }
    public DateTime? FirstTransactionUtc { get; set; }
}

/// <summary>Current balance of one account with its type, for the dashboard and the balances-by-type report.</summary>
public sealed class AccountBalanceRow
{
    public long AccountId { get; set; }
    public string Name { get; set; } = "";
    public string InstitutionName { get; set; } = "";
    /// <summary>Bank/organization name as reported by the provider, falling back to the institution name.</summary>
    public string BankName { get; set; } = "";
    public long? OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public DateTime? OwnerDateOfBirth { get; set; }
    public long? TypeId { get; set; }
    public string? TypeName { get; set; }
    public AccountKind? TypeKind { get; set; }
    public string? TypeColor { get; set; }
    public int TypeSortOrder { get; set; }
    public decimal Balance { get; set; }
    public DateTime? BalanceDateUtc { get; set; }
    public bool ExcludeFromNetWorth { get; set; }
    /// <summary>User-entered monthly contribution for projections; null = derive from deposit history.</summary>
    public decimal? PlannedMonthlyContribution { get; set; }
}

public interface IReportRepository
{
    /// <summary>Accounts that take part in net worth: visible and not excluded.</summary>
    Task<IReadOnlyList<ReportAccountRow>> ListActiveAccountsAsync(CancellationToken ct = default);
    /// <summary>All visible accounts with current balances, including excluded ones (flagged).</summary>
    Task<IReadOnlyList<AccountBalanceRow>> ListAccountBalancesAsync(CancellationToken ct = default);
    /// <summary>Every defined account type, so reports can show types that currently have no accounts.</summary>
    Task<IReadOnlyList<AccountCategory>> ListAccountTypesAsync(CancellationToken ct = default);
    /// <summary>Sum of positive (deposit) amounts per account since <paramref name="fromUtc"/>, excluding pending.</summary>
    Task<IReadOnlyDictionary<long, decimal>> DepositTotalsSinceAsync(DateTime fromUtc, CancellationToken ct = default);
    Task<IReadOnlyList<AccountSnapshotRow>> ListSnapshotsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DailyTotalRow>> ListDailyTransactionTotalsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MonthlyIncomeExpense>> MonthlyIncomeExpenseAsync(DateOnly fromMonth, DateOnly toMonthExclusive, CancellationToken ct = default);
    Task<IReadOnlyList<CategorySpend>> SpendingByCategoryAsync(DateOnly monthStart, DateOnly monthEndExclusive, CancellationToken ct = default);
    Task<IReadOnlyList<MonthGroupSpend>> MonthlySpendingByGroupAsync(DateOnly fromMonth, DateOnly toMonthExclusive, CancellationToken ct = default);
    /// <summary>
    /// Every money-out transaction since <paramref name="fromUtc"/> in spending-report accounts, for recurring-charge
    /// detection: not pending, not hidden, and not in a Transfer or Income category (card payments recur too).
    /// </summary>
    Task<IReadOnlyList<SpendingTransactionRow>> ListSpendingTransactionsAsync(DateTime fromUtc, CancellationToken ct = default);
}

/// <summary>
/// Read-only aggregation queries for reports. Conventions: pending transactions and hidden accounts are excluded;
/// Transfer-kind categories are never income or spending; an uncategorized transaction counts by sign.
/// </summary>
public sealed class ReportRepository : IReportRepository
{
    private readonly Data.IDbConnectionFactory _f;
    public ReportRepository(Data.IDbConnectionFactory f) => _f = f;

    public async Task<IReadOnlyList<ReportAccountRow>> ListActiveAccountsAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<ReportAccountRow>("""
            SELECT a.id AS account_id, COALESCE(NULLIF(a.display_name, ''), a.name) AS name,
                   cat.name AS type_name, cat.kind AS type_kind, cat.color AS type_color, COALESCE(cat.sort_order, 9999) AS type_sort_order,
                   (SELECT MIN(t.posted_at_utc) FROM transactions t WHERE t.account_id = a.id AND t.is_pending = 0) AS first_transaction_utc
              FROM accounts a
              LEFT JOIN account_categories cat ON cat.id = a.category_id
             WHERE a.is_hidden = 0 AND a.exclude_from_net_worth = 0
             ORDER BY a.id
            """)).ToList();
    }

    public async Task<IReadOnlyList<AccountBalanceRow>> ListAccountBalancesAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<AccountBalanceRow>("""
            SELECT a.id AS account_id, COALESCE(NULLIF(a.display_name, ''), a.name) AS name, i.name AS institution_name,
                   COALESCE(NULLIF(a.org_name, ''), i.name) AS bank_name,
                   a.owner_id, o.name AS owner_name, o.date_of_birth AS owner_date_of_birth,
                   cat.id AS type_id, cat.name AS type_name, cat.kind AS type_kind, cat.color AS type_color, COALESCE(cat.sort_order, 9999) AS type_sort_order,
                   a.balance, a.balance_date_utc, a.exclude_from_net_worth, a.planned_monthly_contribution
              FROM accounts a
              JOIN institutions i ON i.id = a.institution_id
              LEFT JOIN account_categories cat ON cat.id = a.category_id
              LEFT JOIN household_members o ON o.id = a.owner_id
             WHERE a.is_hidden = 0
             ORDER BY COALESCE(cat.sort_order, 9999), cat.name, a.balance DESC
            """)).ToList();
    }

    public async Task<IReadOnlyList<AccountCategory>> ListAccountTypesAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<AccountCategory>("SELECT id, name, kind, sort_order, color, is_system, include_in_spending FROM account_categories ORDER BY sort_order, name")).ToList();
    }

    public async Task<IReadOnlyDictionary<long, decimal>> DepositTotalsSinceAsync(DateTime fromUtc, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var rows = await c.QueryAsync<(long AccountId, decimal Total)>("""
            SELECT t.account_id, SUM(t.amount) AS total
              FROM transactions t
             WHERE t.is_pending = 0 AND t.amount > 0 AND t.posted_at_utc >= @fromUtc
             GROUP BY t.account_id
            """, new { fromUtc });
        return rows.ToDictionary(r => r.AccountId, r => r.Total);
    }

    public async Task<IReadOnlyList<AccountSnapshotRow>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<AccountSnapshotRow>("""
            SELECT s.account_id, s.as_of, s.balance
              FROM balance_snapshots s
              JOIN accounts a ON a.id = s.account_id
             WHERE a.is_hidden = 0 AND a.exclude_from_net_worth = 0
             ORDER BY s.account_id, s.as_of
            """)).ToList();
    }

    public async Task<IReadOnlyList<DailyTotalRow>> ListDailyTransactionTotalsAsync(CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<DailyTotalRow>("""
            SELECT t.account_id, DATE(t.posted_at_utc) AS day, SUM(t.amount) AS total
              FROM transactions t
              JOIN accounts a ON a.id = t.account_id
             WHERE t.is_pending = 0 AND a.is_hidden = 0 AND a.exclude_from_net_worth = 0
             GROUP BY t.account_id, DATE(t.posted_at_utc)
            """)).ToList();
    }

    private const string CategorizedTransactions = """
        SELECT t.amount, t.posted_at_utc, t.category_id,
               c.kind AS kind, c.parent_id, c.name AS category_name,
               COALESCE(p.id, c.id) AS group_id, COALESCE(p.name, c.name) AS group_name
          FROM transactions t
          JOIN accounts a ON a.id = t.account_id
          -- Only account types flagged for spending reports (Cash and Credit Card by default); untyped accounts are left out.
          JOIN account_categories acat ON acat.id = a.category_id AND acat.include_in_spending = 1
          LEFT JOIN transaction_categories c ON c.id = t.category_id
          LEFT JOIN transaction_categories p ON p.id = c.parent_id
         WHERE t.is_pending = 0 AND a.is_hidden = 0
           AND t.posted_at_utc >= @from AND t.posted_at_utc < @to
        """;

    public async Task<IReadOnlyList<MonthlyIncomeExpense>> MonthlyIncomeExpenseAsync(DateOnly fromMonth, DateOnly toMonthExclusive, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var rows = await c.QueryAsync<(DateTime Month, decimal Income, decimal Expense, decimal UncIncome, decimal UncExpense)>($"""
            SELECT DATE(DATE_FORMAT(x.posted_at_utc, '%Y-%m-01')) AS month,
                   SUM(CASE WHEN x.kind = @income THEN x.amount WHEN x.kind IS NULL AND x.amount > 0 THEN x.amount ELSE 0 END) AS income,
                   SUM(CASE WHEN x.kind = @expense THEN -x.amount WHEN x.kind IS NULL AND x.amount < 0 THEN -x.amount ELSE 0 END) AS expense,
                   SUM(CASE WHEN x.kind IS NULL AND x.amount > 0 THEN x.amount ELSE 0 END) AS unc_income,
                   SUM(CASE WHEN x.kind IS NULL AND x.amount < 0 THEN -x.amount ELSE 0 END) AS unc_expense
              FROM ({CategorizedTransactions}) x
             GROUP BY DATE(DATE_FORMAT(x.posted_at_utc, '%Y-%m-01'))
             ORDER BY month
            """, new { from = ToDateTime(fromMonth), to = ToDateTime(toMonthExclusive), income = CategoryKind.Income, expense = CategoryKind.Expense });
        return rows.Select(r => new MonthlyIncomeExpense
        {
            Month = DateOnly.FromDateTime(r.Month),
            Income = r.Income,
            Expense = r.Expense,
            UncategorizedIncome = r.UncIncome,
            UncategorizedExpense = r.UncExpense,
        }).ToList();
    }

    public async Task<IReadOnlyList<CategorySpend>> SpendingByCategoryAsync(DateOnly monthStart, DateOnly monthEndExclusive, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<CategorySpend>($"""
            SELECT x.group_id, COALESCE(x.group_name, 'Uncategorized') AS group_name,
                   x.category_id, COALESCE(x.category_name, 'Uncategorized') AS category_name,
                   SUM(-x.amount) AS amount, COUNT(*) AS transaction_count
              FROM ({CategorizedTransactions}) x
             WHERE x.kind = @expense OR (x.kind IS NULL AND x.amount < 0)
             GROUP BY x.group_id, x.group_name, x.category_id, x.category_name
             ORDER BY amount DESC
            """, new { from = ToDateTime(monthStart), to = ToDateTime(monthEndExclusive), expense = CategoryKind.Expense })).ToList();
    }

    public async Task<IReadOnlyList<MonthGroupSpend>> MonthlySpendingByGroupAsync(DateOnly fromMonth, DateOnly toMonthExclusive, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        var rows = await c.QueryAsync<(DateTime Month, string GroupName, decimal Amount)>($"""
            SELECT DATE(DATE_FORMAT(x.posted_at_utc, '%Y-%m-01')) AS month,
                   COALESCE(x.group_name, 'Uncategorized') AS group_name,
                   SUM(-x.amount) AS amount
              FROM ({CategorizedTransactions}) x
             WHERE x.kind = @expense OR (x.kind IS NULL AND x.amount < 0)
             GROUP BY DATE(DATE_FORMAT(x.posted_at_utc, '%Y-%m-01')), COALESCE(x.group_name, 'Uncategorized')
             ORDER BY month, amount DESC
            """, new { from = ToDateTime(fromMonth), to = ToDateTime(toMonthExclusive), expense = CategoryKind.Expense });
        return rows.Select(r => new MonthGroupSpend { Month = DateOnly.FromDateTime(r.Month), GroupName = r.GroupName, Amount = r.Amount }).ToList();
    }

    public async Task<IReadOnlyList<SpendingTransactionRow>> ListSpendingTransactionsAsync(DateTime fromUtc, CancellationToken ct = default)
    {
        using var c = await _f.OpenAsync(ct);
        return (await c.QueryAsync<SpendingTransactionRow>("""
            SELECT t.id, t.account_id, COALESCE(NULLIF(a.display_name, ''), a.name) AS account_name, i.name AS institution_name,
                   t.posted_at_utc, t.amount, t.description, t.payee, c.name AS category_name
              FROM transactions t
              JOIN accounts a ON a.id = t.account_id
              JOIN institutions i ON i.id = a.institution_id
              JOIN account_categories acat ON acat.id = a.category_id AND acat.include_in_spending = 1
              LEFT JOIN transaction_categories c ON c.id = t.category_id
             WHERE t.is_pending = 0 AND a.is_hidden = 0
               AND t.posted_at_utc >= @fromUtc AND t.amount < 0
               AND (c.kind IS NULL OR c.kind = @expense)
             ORDER BY t.posted_at_utc
            """, new { fromUtc, expense = CategoryKind.Expense })).ToList();
    }

    private static DateTime ToDateTime(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
