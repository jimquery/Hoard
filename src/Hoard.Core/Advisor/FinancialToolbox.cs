using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hoard.Core.Budgeting;
using Hoard.Core.Categorization;
using Hoard.Core.Data;
using Hoard.Core.Domain;
using Hoard.Core.Reporting;

namespace Hoard.Core.Advisor;

/// <summary>A tool the advisor can call: its name, what it is for, and its JSON-schema input.</summary>
public sealed record AdvisorToolDefinition(string Name, string Description, JsonObject InputSchema, bool NeedsTransactionDetail = false);

/// <summary>
/// The advisor's only window onto the household's data. Every tool reads Hoard's database and returns compact JSON:
/// figures already aggregated where possible, amounts in the account currency rounded to cents, no account numbers or
/// provider ids. Tools that expose individual transactions or merchant names (<see cref="AdvisorToolDefinition.NeedsTransactionDetail"/>)
/// are offered only when the user allowed transaction detail. The toolbox never writes anything.
/// </summary>
public sealed class FinancialToolbox
{
    public const int MaxSearchRows = 100;

    private readonly ReportingService _reporting;
    private readonly IReportRepository _reports;
    private readonly BudgetService _budget;
    private readonly ITransactionRepository _transactions;
    private readonly ICategoryRepository _categories;
    private readonly IHouseholdRepository _household;
    private readonly Func<DateOnly> _today;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public FinancialToolbox(ReportingService reporting, IReportRepository reports, BudgetService budget, ITransactionRepository transactions,
        ICategoryRepository categories, IHouseholdRepository household, Func<DateOnly>? today = null)
    {
        _reporting = reporting;
        _reports = reports;
        _budget = budget;
        _transactions = transactions;
        _categories = categories;
        _household = household;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
    }

    public static IReadOnlyList<AdvisorToolDefinition> Definitions { get; } =
    [
        new("get_financial_overview",
            "Today's date, net worth, assets and liabilities, every account's current balance grouped by account type, household members' ages, " +
            "and income, expenses and savings rate over the last 12 complete months. Call this first.",
            Schema()),
        new("get_monthly_cash_flow",
            "Income, expenses, net savings and savings rate for each calendar month, most recent last. Transfers between accounts are excluded.",
            Schema(("months", Int("How many months back from the current month to include (1-36).", 1, 36)))),
        new("get_spending_by_category",
            "Spending per category (with its category group) between two months inclusive, with totals, transaction counts and the monthly average. " +
            "Only spending accounts (checking, credit cards) count; transfers and income are excluded.",
            Schema(("from_month", Month("First month, YYYY-MM.")), ("to_month", Month("Last month, YYYY-MM (inclusive).")))),
        new("get_spending_trend",
            "Monthly spending per category group for the last N months, oldest first; use it to spot rising or seasonal categories.",
            Schema(("months", Int("How many months back from the current month (2-36).", 2, 36)))),
        new("get_budget",
            "The household budget for a month by category group: the 12-month forecast (typical month, trend per month, likely range), " +
            "the monthly target if one is set, actual spending (so far, for the current month), the month-end projection and the status.",
            Schema([("month", Month("The month, YYYY-MM. Omit for the current month."))], required: [])),
        new("get_net_worth_history",
            "Net worth, assets and liabilities at the start of each month for the last N months (estimated from transaction history before the first sync).",
            Schema(("months", Int("How many months back (2-120).", 2, 120)))),
        new("get_subscriptions",
            "Recurring charges detected in the last three years: vendor, cadence, latest amount, annual cost, whether still active, category.",
            Schema(), NeedsTransactionDetail: true),
        new("get_top_merchants",
            "The merchants where the most money went between two months inclusive: total, number of charges, average charge and category.",
            Schema(("from_month", Month("First month, YYYY-MM.")), ("to_month", Month("Last month, YYYY-MM (inclusive).")),
                   ("limit", Int("How many merchants (1-50).", 1, 50))), NeedsTransactionDetail: true),
        new("search_transactions",
            $"Individual transactions matching optional filters, newest first or largest first (at most {MaxSearchRows}). " +
            "Use it to look into a specific merchant, category or large purchase.",
            Schema([("text", Str("Text to find in the description, payee, memo or notes.")),
                   ("category", Str("A category or category group name, exactly as other tools report it.")),
                   ("from_date", Str("Earliest posting date, YYYY-MM-DD.")),
                   ("to_date", Str("Latest posting date, YYYY-MM-DD.")),
                   ("sort", Enum("\"date\" (newest first, default) or \"amount\" (largest money-out first).", "date", "amount")),
                   ("limit", Int($"Maximum rows (1-{MaxSearchRows}).", 1, MaxSearchRows))], required: []), NeedsTransactionDetail: true),
    ];

    public static IReadOnlyList<AdvisorToolDefinition> DefinitionsFor(bool allowTransactionDetail) =>
        Definitions.Where(d => allowTransactionDetail || !d.NeedsTransactionDetail).ToList();

    /// <summary>Runs a tool and returns its JSON result. Bad input throws <see cref="ArgumentException"/> with a message for the model.</summary>
    public async Task<string> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, bool allowTransactionDetail, CancellationToken ct = default)
    {
        var def = Definitions.FirstOrDefault(d => d.Name == name) ?? throw new ArgumentException($"Unknown tool \"{name}\".");
        if (def.NeedsTransactionDetail && !allowTransactionDetail)
            throw new ArgumentException($"\"{name}\" is turned off: the user has not allowed transaction-level detail to be shared.");

        object result = name switch
        {
            "get_financial_overview" => await OverviewAsync(ct),
            "get_monthly_cash_flow" => await CashFlowAsync(GetInt(input, "months", 12, 1, 36), ct),
            "get_spending_by_category" => await SpendingAsync(GetMonth(input, "from_month"), GetMonth(input, "to_month"), ct),
            "get_spending_trend" => await TrendAsync(GetInt(input, "months", 12, 2, 36), ct),
            "get_budget" => await BudgetAsync(input.ContainsKey("month") ? GetMonth(input, "month") : CurrentMonth, ct),
            "get_net_worth_history" => await NetWorthAsync(GetInt(input, "months", 24, 2, 120), ct),
            "get_subscriptions" => await SubscriptionsAsync(ct),
            "get_top_merchants" => await TopMerchantsAsync(GetMonth(input, "from_month"), GetMonth(input, "to_month"), GetInt(input, "limit", 20, 1, 50), ct),
            "search_transactions" => await SearchAsync(input, ct),
            _ => throw new ArgumentException($"Unknown tool \"{name}\"."),
        };
        return JsonSerializer.Serialize(result, Json);
    }

    private DateOnly CurrentMonth => BudgetService.MonthOf(_today());

    // ---------------------------------------------------------------------------------------------------------------

    private async Task<object> OverviewAsync(CancellationToken ct)
    {
        var today = _today();
        var balances = await _reporting.BalancesByTypeAsync(ct: ct);
        var lastFull = CurrentMonth.AddMonths(-1);
        var flows = await _reporting.IncomeVsExpenseAsync(lastFull.AddMonths(-11), lastFull, ct);
        var income = flows.Sum(f => f.Income);
        var expense = flows.Sum(f => f.Expense);
        var members = await _household.ListAsync(ct);

        return new
        {
            Today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Currency = "USD",
            NetWorth = R(balances.NetWorth),
            Assets = R(balances.Assets),
            Liabilities = R(balances.Liabilities),
            AccountTypes = balances.Groups.Select(g => new
            {
                Type = g.Name,
                Kind = g.Kind.ToString().ToLowerInvariant(),
                Total = R(g.Total),
                Accounts = g.Accounts.Select(a => new
                {
                    a.Name,
                    Institution = a.InstitutionName,
                    Balance = R(a.Balance),
                    Owner = a.OwnerName,
                    ExcludedFromNetWorth = a.ExcludeFromNetWorth ? true : (bool?)null,
                }),
            }),
            Household = members.Select(m => new { m.Name, m.Relationship, Age = m.AgeOn(today) }),
            Last12Months = new
            {
                From = Ym(lastFull.AddMonths(-11)),
                To = Ym(lastFull),
                Income = R(income),
                Expenses = R(expense),
                NetSavings = R(income - expense),
                SavingsRate = income > 0 ? Math.Round((income - expense) / income, 3) : (decimal?)null,
                AverageMonthlyExpenses = R(expense / 12),
            },
            Notes = "Liability balances are negative. Income and expenses count only account types marked for spending reports; " +
                    "contributions straight from a paycheck to a retirement plan are not visible as income or expenses.",
        };
    }

    private async Task<object> CashFlowAsync(int months, CancellationToken ct)
    {
        var rows = await _reporting.IncomeVsExpenseAsync(CurrentMonth.AddMonths(-(months - 1)), CurrentMonth, ct);
        return new
        {
            CurrentMonthIsPartial = true,
            Months = rows.Select(r => new
            {
                Month = Ym(r.Month),
                Income = R(r.Income),
                Expenses = R(r.Expense),
                Net = R(r.Net),
                SavingsRate = r.Income > 0 ? Math.Round(r.Net / r.Income, 3) : (decimal?)null,
                Uncategorized = r.UncategorizedIncome + r.UncategorizedExpense > 0 ? R(r.UncategorizedIncome + r.UncategorizedExpense) : (decimal?)null,
            }),
        };
    }

    private async Task<object> SpendingAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) (from, to) = (to, from);
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
        var rows = await _reports.SpendingByCategoryAsync(from, to.AddMonths(1), ct);
        var total = rows.Sum(r => r.Amount);
        return new
        {
            From = Ym(from),
            To = Ym(to),
            Months = months,
            Total = R(total),
            MonthlyAverage = R(total / months),
            Groups = rows.GroupBy(r => r.GroupName).OrderByDescending(g => g.Sum(r => r.Amount)).Select(g => new
            {
                Group = g.Key,
                Total = R(g.Sum(r => r.Amount)),
                MonthlyAverage = R(g.Sum(r => r.Amount) / months),
                ShareOfSpending = total > 0 ? Math.Round(g.Sum(r => r.Amount) / total, 3) : 0,
                Categories = g.OrderByDescending(r => r.Amount).Select(r => new
                {
                    Category = r.CategoryName,
                    Total = R(r.Amount),
                    Transactions = r.TransactionCount,
                }),
            }),
        };
    }

    private async Task<object> TrendAsync(int months, CancellationToken ct)
    {
        var rows = await _reporting.SpendingTrendAsync(CurrentMonth, months, ct);
        var monthList = Enumerable.Range(0, months).Select(i => CurrentMonth.AddMonths(-(months - 1) + i)).ToList();
        return new
        {
            Months = monthList.Select(Ym),
            CurrentMonthIsPartial = true,
            Groups = rows.GroupBy(r => r.GroupName).OrderByDescending(g => g.Sum(r => r.Amount)).Select(g => new
            {
                Group = g.Key,
                Amounts = monthList.Select(m => R(g.Where(r => r.Month == m).Sum(r => r.Amount))),
            }),
        };
    }

    private async Task<object> BudgetAsync(DateOnly month, CancellationToken ct)
    {
        var b = await _budget.GetMonthAsync(month, _today(), ct);
        return new
        {
            Month = Ym(b.Month),
            IsCurrentMonth = b.IsCurrentMonth,
            DayOfMonth = b.DayOfMonth,
            DaysInMonth = b.DaysInMonth,
            TotalTarget = R(b.TotalTarget),
            TotalActual = R(b.TotalActual),
            TotalProjected = R(b.TotalProjected),
            TotalForecast = R(b.TotalPredicted),
            Groups = b.Lines.Select(l => new
            {
                Group = l.GroupName,
                Target = l.Target is { } t ? R(t) : (decimal?)null,
                Actual = R(l.Actual),
                Projected = R(l.Projected),
                Status = l.Status.ToString(),
                Forecast = R(l.Forecast.Predicted),
                ForecastRange = new[] { R(l.Forecast.Low), R(l.Forecast.High) },
                TypicalMonth = R(l.Forecast.TypicalMonth),
                TrendPerMonth = R(l.Forecast.MonthlyTrend),
                TrendStrength = l.Forecast.TrendStrength,
                Last12Months = l.History.Select(R),
            }),
        };
    }

    private async Task<object> NetWorthAsync(int months, CancellationToken ct)
    {
        var today = _today();
        var report = await _reporting.NetWorthAsync(today.AddMonths(-months), today, ct);
        var points = report.Points.Where(p => p.Date.Day == 1 || p.Date == today).ToList();
        return new
        {
            EstimatedBefore = report.ReliableFrom is { } r ? r.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            Points = points.Select(p => new { Date = p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), NetWorth = R(p.NetWorth), Assets = R(p.Assets), Liabilities = R(p.Liabilities) }),
        };
    }

    private async Task<object> SubscriptionsAsync(CancellationToken ct)
    {
        var report = await _reporting.SubscriptionsAsync(asOf: _today(), ct: ct);
        return new
        {
            ActiveAnnualCost = R(report.ActiveAnnualCost),
            Subscriptions = report.Subscriptions.Select(s => new
            {
                s.Vendor,
                Cadence = s.Cadence.ToString().ToLowerInvariant(),
                LatestAmount = R(s.LatestAmount),
                AnnualCost = R(s.AnnualCost),
                Active = s.IsActive,
                FirstCharge = s.FirstCharge.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                LastCharge = s.LastCharge.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Category = s.CategoryName,
            }),
        };
    }

    private async Task<object> TopMerchantsAsync(DateOnly from, DateOnly to, int limit, CancellationToken ct)
    {
        if (to < from) (from, to) = (to, from);
        var end = to.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = (await _reports.ListSpendingTransactionsAsync(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ct))
            .Where(r => r.PostedAtUtc < end).ToList();
        return new
        {
            From = Ym(from),
            To = Ym(to),
            Merchants = rows.GroupBy(r => MerchantKey.Extract(r.Description, r.Payee))
                .Where(g => g.Key.Length > 0)
                .Select(g => new
                {
                    Merchant = SubscriptionDetector.DisplayName(g.Key),
                    Total = R(-g.Sum(r => r.Amount)),
                    Charges = g.Count(),
                    AverageCharge = R(-g.Average(r => r.Amount)),
                    Category = g.GroupBy(r => r.CategoryName).OrderByDescending(c => c.Count()).First().Key,
                })
                .OrderByDescending(m => m.Total).Take(limit),
        };
    }

    private async Task<object> SearchAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var limit = GetInt(input, "limit", 25, 1, MaxSearchRows);
        var filter = new TransactionFilter
        {
            Text = GetString(input, "text"),
            FromUtc = GetDate(input, "from_date"),
            ToUtc = GetDate(input, "to_date")?.AddDays(1),
            Limit = 2000,
        };
        if (GetString(input, "category") is { } categoryName)
        {
            var match = (await _categories.ListCategoriesAsync(includeHidden: true, ct))
                .FirstOrDefault(c => string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No category named \"{categoryName}\".");
            filter.CategoryId = match.Id;
        }
        var rows = await _transactions.ListAsync(filter, ct);
        var sorted = GetString(input, "sort") == "amount" ? rows.OrderBy(r => r.Amount) : rows.OrderByDescending(r => r.PostedAtUtc);
        return new
        {
            Matched = rows.Count,
            Shown = Math.Min(limit, rows.Count),
            Transactions = sorted.Take(limit).Select(r => new
            {
                Date = r.PostedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Account = r.AccountName,
                r.Description,
                r.Payee,
                Amount = R(r.Amount),
                Category = string.IsNullOrEmpty(r.CategoryPath) ? null : r.CategoryPath,
                Pending = r.IsPending ? true : (bool?)null,
            }),
        };
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static decimal R(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
    private static string Ym(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static int GetInt(IReadOnlyDictionary<string, JsonElement> input, string key, int fallback, int min, int max)
    {
        if (!input.TryGetValue(key, out var v)) return fallback;
        var n = v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => throw new ArgumentException($"\"{key}\" must be a whole number."),
        };
        return Math.Clamp(n, min, max);
    }

    private static DateOnly GetMonth(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        var s = GetString(input, key) ?? throw new ArgumentException($"\"{key}\" is required (YYYY-MM).");
        return DateOnly.TryParseExact(s, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new ArgumentException($"\"{key}\" must look like 2026-03.");
    }

    private static DateTime? GetDate(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        GetString(input, key) is { } s
            ? DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date
              : throw new ArgumentException($"\"{key}\" must look like 2026-03-15.")
            : null;

    // Schema helpers --------------------------------------------------------------------------------------------------

    private static JsonObject Schema(params (string Name, JsonObject Schema)[] props) =>
        Schema(props, required: props.Select(p => p.Name).ToArray());

    private static JsonObject Schema((string Name, JsonObject Schema)[] props, string[] required)
    {
        var properties = new JsonObject();
        foreach (var (name, schema) in props) properties[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject Int(string description, int min, int max) =>
        new() { ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max };

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Month(string description) =>
        new() { ["type"] = "string", ["description"] = description, ["pattern"] = "^\\d{4}-\\d{2}$" };

    private static JsonObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
}
