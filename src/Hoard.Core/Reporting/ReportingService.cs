using Hoard.Core.Domain;

namespace Hoard.Core.Reporting;

public sealed class ReportingService
{
    private readonly IReportRepository _repo;
    public ReportingService(IReportRepository repo) => _repo = repo;

    public const string UntypedName = "(untyped)";

    /// <summary>Daily net worth from <paramref name="from"/> (or the earliest data) to today.</summary>
    public async Task<NetWorthReport> NetWorthAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var accounts = await _repo.ListActiveAccountsAsync(ct);
        var snapshots = (await _repo.ListSnapshotsAsync(ct)).GroupBy(s => s.AccountId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(DateOnly, decimal)>)g.Select(s => (DateOnly.FromDateTime(s.AsOf), s.Balance)).ToList());
        var totals = (await _repo.ListDailyTransactionTotalsAsync(ct)).GroupBy(t => t.AccountId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<DateOnly, decimal>)g.ToDictionary(t => DateOnly.FromDateTime(t.Day), t => t.Total));

        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var withData = accounts.Where(a => snapshots.ContainsKey(a.AccountId)).ToList();
        if (withData.Count == 0) return new NetWorthReport();

        // An account's history is reconstructable from the earlier of its first snapshot and first transaction.
        var dataStart = withData.ToDictionary(a => a.AccountId, a =>
        {
            var firstSnap = snapshots[a.AccountId].Min(s => s.Item1);
            var firstTx = a.FirstTransactionUtc is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
            return firstTx is { } t && t < firstSnap ? t : firstSnap;
        });
        var earliest = dataStart.Values.Min();
        var reliableFrom = dataStart.Values.Max();
        var start = from ?? earliest;
        if (start > end) start = end;

        var perAccount = withData.ToDictionary(a => a.AccountId, a =>
            BalanceReconstructor.Reconstruct(snapshots[a.AccountId],
                totals.GetValueOrDefault(a.AccountId) ?? new Dictionary<DateOnly, decimal>(), start, end));

        var typeOf = withData.ToDictionary(a => a.AccountId, a => (Name: a.TypeName ?? UntypedName, Kind: a.TypeKind ?? AccountKind.Asset));
        var typeOrder = withData.GroupBy(a => a.TypeName ?? UntypedName).ToDictionary(g => g.Key, g => g.Min(a => a.TypeSortOrder));
        var typeColors = withData.Where(a => a.TypeName is not null && a.TypeColor is not null)
            .GroupBy(a => a.TypeName!).ToDictionary(g => g.Key, g => g.First().TypeColor!);
        var assetTypes = typeOf.Values.Where(t => t.Kind == AccountKind.Asset).Select(t => t.Name).Distinct()
            .OrderBy(n => n == UntypedName).ThenBy(n => typeOrder[n]).ThenBy(n => n).ToList();
        var liabilityTypes = typeOf.Values.Where(t => t.Kind == AccountKind.Liability).Select(t => t.Name).Distinct()
            .OrderBy(n => typeOrder[n]).ThenBy(n => n).ToList();

        var points = new List<NetWorthPoint>();
        var days = end.DayNumber - start.DayNumber + 1;
        for (var i = 0; i < days; i++)
        {
            var d = start.AddDays(i);
            var byType = new Dictionary<string, decimal>();
            decimal assets = 0, liabilities = 0;
            var estimated = false;
            foreach (var a in withData)
            {
                if (d < dataStart[a.AccountId]) continue; // account has no knowable history yet
                var series = perAccount[a.AccountId];
                if (series.Count == 0) continue;
                var b = series[i];
                estimated |= b.IsEstimated;
                var (name, kind) = typeOf[a.AccountId];
                byType[name] = byType.GetValueOrDefault(name) + b.Balance;
                if (kind == AccountKind.Liability) liabilities += b.Balance; else assets += b.Balance;
            }
            points.Add(new NetWorthPoint { Date = d, Assets = assets, Liabilities = liabilities, ByType = byType, IsEstimated = estimated });
        }

        return new NetWorthReport
        {
            Points = points,
            AssetTypes = assetTypes,
            LiabilityTypes = liabilityTypes,
            ReliableFrom = reliableFrom,
            EarliestData = earliest,
            TypeColors = typeColors,
        };
    }

    /// <summary>
    /// Current balances grouped by account type, in configured type order. Excluded accounts are included but flagged.
    /// With <paramref name="includeEmptyTypes"/> every defined type appears, even with no accounts.
    /// </summary>
    /// <summary>Marker for <paramref name="ownerId"/>: only accounts that have no owner.</summary>
    public const long UnassignedOwner = -1;

    public async Task<BalancesByTypeReport> BalancesByTypeAsync(bool includeEmptyTypes = false, long? ownerId = null, CancellationToken ct = default)
    {
        var rows = await _repo.ListAccountBalancesAsync(ct);
        if (ownerId == UnassignedOwner) rows = rows.Where(r => r.OwnerId is null).ToList();
        else if (ownerId is { } oid) rows = rows.Where(r => r.OwnerId == oid).ToList();
        var groups = rows
            .GroupBy(r => r.TypeId)
            .Select(g =>
            {
                var first = g.First();
                return new TypeBalanceGroup
                {
                    TypeId = g.Key,
                    Name = first.TypeName ?? UntypedName,
                    Kind = first.TypeKind ?? AccountKind.Asset,
                    Color = first.TypeColor,
                    SortOrder = first.TypeSortOrder,
                    Accounts = g.OrderByDescending(a => Math.Abs(a.Balance)).ToList(),
                };
            })
            .ToList();

        if (includeEmptyTypes)
        {
            var present = groups.Where(g => g.TypeId is not null).Select(g => g.TypeId!.Value).ToHashSet();
            foreach (var t in await _repo.ListAccountTypesAsync(ct))
                if (!present.Contains(t.Id))
                    groups.Add(new TypeBalanceGroup { TypeId = t.Id, Name = t.Name, Kind = t.Kind, Color = t.Color, SortOrder = t.SortOrder });
        }

        groups = groups.OrderBy(g => g.TypeId is null).ThenBy(g => g.Kind).ThenBy(g => g.SortOrder).ThenBy(g => g.Name).ToList();
        return new BalancesByTypeReport { Groups = groups };
    }

    /// <summary>
    /// Balances of one account type (normally "College Savings") grouped by owner, projected to each owner's 18th birthday.
    /// Each account's monthly contribution is its planned figure (accounts.planned_monthly_contribution) when set, else
    /// <paramref name="monthlyContributionOverride"/> when given, else the trailing-12-month average of deposits into the account.
    /// </summary>
    public async Task<CollegeSavingsReport> CollegeSavingsAsync(long typeId, decimal annualRate, decimal? monthlyContributionOverride = null, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var today = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        var rows = (await _repo.ListAccountBalancesAsync(ct)).Where(r => r.TypeId == typeId).ToList();
        var typeName = rows.FirstOrDefault()?.TypeName
                       ?? (await _repo.ListAccountTypesAsync(ct)).FirstOrDefault(t => t.Id == typeId)?.Name ?? "College Savings";
        var deposits = await _repo.DepositTotalsSinceAsync(today.AddMonths(-12).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ct);

        var owners = rows
            .GroupBy(r => r.OwnerId)
            .Select(g =>
            {
                var first = g.First();
                DateOnly? dob = first.OwnerDateOfBirth is { } d ? DateOnly.FromDateTime(d) : null;
                DateOnly? eighteenth = dob is { } b ? AgeCalculator.BirthdayAt(b, 18) : null;
                int? months = eighteenth is { } e ? CollegeProjection.MonthsBetween(today, e) : null;
                var accounts = g.OrderByDescending(a => a.Balance).Select(a =>
                {
                    var (monthly, source) = a.PlannedMonthlyContribution is { } planned ? (planned, ContributionSource.Planned)
                        : monthlyContributionOverride is { } over ? (over, ContributionSource.PageOverride)
                        : (Math.Round(deposits.GetValueOrDefault(a.AccountId) / 12m, 2), ContributionSource.DepositAverage);
                    return new CollegeAccountProjection
                    {
                        AccountId = a.AccountId,
                        Name = a.Name,
                        BankName = a.BankName,
                        Balance = a.Balance,
                        MonthlyContribution = monthly,
                        ContributionSource = source,
                        PlannedMonthlyContribution = a.PlannedMonthlyContribution,
                        ProjectedAt18 = months is { } m ? CollegeProjection.FutureValue(a.Balance, monthly, annualRate, m) : null,
                    };
                }).ToList();
                return new CollegeOwnerProjection
                {
                    OwnerId = g.Key,
                    OwnerName = first.OwnerName ?? "Unassigned",
                    DateOfBirth = dob,
                    AgeNow = dob is { } b2 ? AgeCalculator.AgeOn(b2, today) : null,
                    EighteenthBirthday = eighteenth,
                    MonthsTo18 = months,
                    Accounts = accounts,
                };
            })
            .OrderBy(o => o.OwnerId is null).ThenBy(o => o.DateOfBirth ?? DateOnly.MaxValue).ThenBy(o => o.OwnerName)
            .ToList();

        return new CollegeSavingsReport { TypeName = typeName, AnnualRate = annualRate, AsOf = today, Owners = owners };
    }

    /// <summary>Income and expenses for each calendar month in the range, with empty months filled in.</summary>
    public async Task<IReadOnlyList<MonthlyIncomeExpense>> IncomeVsExpenseAsync(DateOnly fromMonth, DateOnly toMonthInclusive, CancellationToken ct = default)
    {
        fromMonth = new DateOnly(fromMonth.Year, fromMonth.Month, 1);
        var toExclusive = new DateOnly(toMonthInclusive.Year, toMonthInclusive.Month, 1).AddMonths(1);
        var rows = (await _repo.MonthlyIncomeExpenseAsync(fromMonth, toExclusive, ct)).ToDictionary(r => r.Month);
        var result = new List<MonthlyIncomeExpense>();
        for (var m = fromMonth; m < toExclusive; m = m.AddMonths(1))
            result.Add(rows.GetValueOrDefault(m) ?? new MonthlyIncomeExpense { Month = m });
        return result;
    }

    /// <summary>Spending by category for a month and for the same month one year earlier.</summary>
    public async Task<SpendingComparison> SpendingComparisonAsync(DateOnly month, CancellationToken ct = default)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        var prior = month.AddYears(-1);
        var current = await _repo.SpendingByCategoryAsync(month, month.AddMonths(1), ct);
        var previous = await _repo.SpendingByCategoryAsync(prior, prior.AddMonths(1), ct);
        return new SpendingComparison { Month = month, PriorMonth = prior, Current = current, Prior = previous };
    }

    /// <summary>Spending per month per category group for the N months ending at <paramref name="endMonth"/>.</summary>
    public async Task<IReadOnlyList<MonthGroupSpend>> SpendingTrendAsync(DateOnly endMonth, int months = 12, CancellationToken ct = default)
    {
        endMonth = new DateOnly(endMonth.Year, endMonth.Month, 1);
        var from = endMonth.AddMonths(-(months - 1));
        return await _repo.MonthlySpendingByGroupAsync(from, endMonth.AddMonths(1), ct);
    }

    /// <summary>
    /// Recurring charges — same vendor, same or similar amount, on a monthly, quarterly or yearly rhythm — found in
    /// the last <paramref name="months"/> months of spending-account transactions (<see cref="SubscriptionDetector"/>).
    /// </summary>
    public async Task<SubscriptionReport> SubscriptionsAsync(int months = 36, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var today = asOf ?? DateOnly.FromDateTime(DateTime.Today);
        var from = today.AddMonths(-months);
        var rows = await _repo.ListSpendingTransactionsAsync(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ct);
        return new SubscriptionReport
        {
            From = from,
            AsOf = today,
            TransactionsExamined = rows.Count,
            Subscriptions = SubscriptionDetector.Detect(rows, today),
        };
    }
}
