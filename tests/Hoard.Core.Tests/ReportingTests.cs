using Hoard.Core.Domain;
using Hoard.Core.Reporting;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class BalanceReconstructorTests
{
    private static DateOnly D(int day) => new(2026, 8, day);

    [Fact]
    public void ObservedDay_UsesSnapshotExactly_AndIsNotEstimated()
    {
        var result = BalanceReconstructor.Reconstruct([(D(10), 1000m)], new Dictionary<DateOnly, decimal> { [D(10)] = -50m }, D(10), D(10));
        var b = Assert.Single(result);
        Assert.Equal(1000m, b.Balance);
        Assert.False(b.IsEstimated);
    }

    [Fact]
    public void ForwardRoll_AddsTransactionsAfterSnapshot()
    {
        var tx = new Dictionary<DateOnly, decimal> { [D(11)] = -100m, [D(13)] = 250m };
        var result = BalanceReconstructor.Reconstruct([(D(10), 1000m)], tx, D(10), D(14));

        Assert.Equal(5, result.Count);
        Assert.Equal(1000m, result[0].Balance);   // 10th: snapshot
        Assert.Equal(900m, result[1].Balance);    // 11th: -100
        Assert.Equal(900m, result[2].Balance);    // 12th: no activity
        Assert.Equal(1150m, result[3].Balance);   // 13th: +250
        Assert.Equal(1150m, result[4].Balance);
        Assert.True(result[1].IsEstimated);
    }

    [Fact]
    public void BackwardRoll_SubtractsTransactionsUpToFirstSnapshot()
    {
        // Balance on the 10th is 1000. On the 8th we spent 100 and on the 10th we received 500.
        // So at end of 7th: 1000 - 500 - (-100) = 600; end of 8th: 500; end of 9th: 500.
        var tx = new Dictionary<DateOnly, decimal> { [D(8)] = -100m, [D(10)] = 500m };
        var result = BalanceReconstructor.Reconstruct([(D(10), 1000m)], tx, D(7), D(10));

        Assert.Equal(600m, result[0].Balance);
        Assert.Equal(500m, result[1].Balance);
        Assert.Equal(500m, result[2].Balance);
        Assert.Equal(1000m, result[3].Balance);
        Assert.True(result[0].IsEstimated);
        Assert.False(result[3].IsEstimated);
    }

    [Fact]
    public void MultipleSnapshots_LatestPriorSnapshotWins_EvenIfTransactionsDisagree()
    {
        // Bank says 1000 on the 10th and 1500 on the 20th; our transactions only explain +300 of that.
        var tx = new Dictionary<DateOnly, decimal> { [D(15)] = 300m };
        var result = BalanceReconstructor.Reconstruct([(D(10), 1000m), (D(20), 1500m)], tx, D(10), D(21));

        Assert.Equal(1300m, result[5].Balance);   // 15th, estimated from the 10th
        Assert.Equal(1300m, result[9].Balance);   // 19th
        Assert.Equal(1500m, result[10].Balance);  // 20th snapshot resets
        Assert.False(result[10].IsEstimated);
        Assert.Equal(1500m, result[11].Balance);
    }

    [Fact]
    public void RangeEntirelyBeforeSnapshots_IsEstimatedBackwards()
    {
        var tx = new Dictionary<DateOnly, decimal> { [D(5)] = -10m, [D(20)] = -20m };
        var result = BalanceReconstructor.Reconstruct([(D(25), 100m)], tx, D(1), D(3));
        Assert.All(result, b => Assert.True(b.IsEstimated));
        Assert.Equal(130m, result[0].Balance);
    }

    [Fact]
    public void NoSnapshots_ReturnsEmpty()
    {
        Assert.Empty(BalanceReconstructor.Reconstruct([], new Dictionary<DateOnly, decimal>(), D(1), D(5)));
    }
}

public sealed class CollegeProjectionTests
{
    [Fact]
    public void ZeroMonths_ReturnsCurrentBalance() => Assert.Equal(1234.56m, CollegeProjection.FutureValue(1234.56m, 500m, 0.07m, 0));

    [Fact]
    public void ZeroRate_IsBalancePlusContributions() => Assert.Equal(10000m + 12 * 100m, CollegeProjection.FutureValue(10000m, 100m, 0m, 12));

    [Fact]
    public void GrowthOnly_MatchesCompoundInterest()
    {
        // 10,000 at 6% nominal, monthly compounding, 10 years → 18,193.97
        Assert.Equal(18193.97m, CollegeProjection.FutureValue(10000m, 0m, 0.06m, 120));
    }

    [Fact]
    public void ContributionsOnly_MatchesAnnuityFormula()
    {
        // 100/month at 6% for 10 years → 16,387.93
        Assert.Equal(16387.93m, CollegeProjection.FutureValue(0m, 100m, 0.06m, 120));
    }

    [Theory]
    [InlineData(2026, 9, 7, 2033, 3, 14, 78)]
    [InlineData(2026, 9, 7, 2026, 9, 7, 0)]
    [InlineData(2026, 9, 7, 2020, 1, 1, 0)]
    [InlineData(2026, 1, 31, 2026, 2, 28, 0)]
    [InlineData(2026, 1, 15, 2026, 3, 15, 2)]
    public void MonthsBetween_CountsWholeMonths(int y1, int m1, int d1, int y2, int m2, int d2, int expected) =>
        Assert.Equal(expected, CollegeProjection.MonthsBetween(new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2)));

    [Fact]
    public void Age_HandlesBirthdayNotYetReached()
    {
        Assert.Equal(11, AgeCalculator.AgeOn(new DateOnly(2015, 3, 14), new DateOnly(2026, 9, 7)));
        Assert.Equal(10, AgeCalculator.AgeOn(new DateOnly(2015, 3, 14), new DateOnly(2026, 3, 13)));
        Assert.Equal(11, AgeCalculator.AgeOn(new DateOnly(2015, 3, 14), new DateOnly(2026, 3, 14)));
    }
}

public sealed class FakeReportRepository : IReportRepository
{
    public List<ReportAccountRow> Accounts { get; } = new();
    public List<AccountSnapshotRow> Snapshots { get; } = new();
    public List<DailyTotalRow> Totals { get; } = new();
    public List<MonthlyIncomeExpense> Monthly { get; } = new();
    public List<AccountBalanceRow> Balances { get; } = new();
    public List<AccountCategory> Types { get; } = new();
    public Dictionary<long, decimal> Deposits { get; } = new();

    public Task<IReadOnlyList<ReportAccountRow>> ListActiveAccountsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReportAccountRow>>(Accounts);
    public Task<IReadOnlyList<AccountBalanceRow>> ListAccountBalancesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountBalanceRow>>(Balances);
    public Task<IReadOnlyList<AccountCategory>> ListAccountTypesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountCategory>>(Types);
    public Task<IReadOnlyDictionary<long, decimal>> DepositTotalsSinceAsync(DateTime fromUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<long, decimal>>(Deposits);
    public Task<IReadOnlyList<AccountSnapshotRow>> ListSnapshotsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountSnapshotRow>>(Snapshots);
    public Task<IReadOnlyList<DailyTotalRow>> ListDailyTransactionTotalsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DailyTotalRow>>(Totals);
    public Task<IReadOnlyList<MonthlyIncomeExpense>> MonthlyIncomeExpenseAsync(DateOnly fromMonth, DateOnly toMonthExclusive, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MonthlyIncomeExpense>>(Monthly.Where(m => m.Month >= fromMonth && m.Month < toMonthExclusive).ToList());
    public Task<IReadOnlyList<CategorySpend>> SpendingByCategoryAsync(DateOnly monthStart, DateOnly monthEndExclusive, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CategorySpend>>([]);
    public List<MonthGroupSpend> Spend { get; } = new();
    public Task<IReadOnlyList<MonthGroupSpend>> MonthlySpendingByGroupAsync(DateOnly fromMonth, DateOnly toMonthExclusive, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MonthGroupSpend>>(Spend.Where(s => s.Month >= fromMonth && s.Month < toMonthExclusive).ToList());
    public List<SpendingTransactionRow> Spending { get; } = new();
    public Task<IReadOnlyList<SpendingTransactionRow>> ListSpendingTransactionsAsync(DateTime fromUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SpendingTransactionRow>>(Spending.Where(r => r.PostedAtUtc >= fromUtc).ToList());
}

public sealed class ReportingServiceTests
{
    private static DateTime Dt(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task NetWorth_AggregatesByType_SplitsAssetsAndLiabilities_AndReportsReliableFrom()
    {
        var repo = new FakeReportRepository();
        repo.Accounts.Add(new ReportAccountRow { AccountId = 1, Name = "Checking", TypeName = "Cash", TypeKind = AccountKind.Asset, FirstTransactionUtc = Dt(8, 1) });
        repo.Accounts.Add(new ReportAccountRow { AccountId = 2, Name = "Card", TypeName = "Credit Card", TypeKind = AccountKind.Liability, FirstTransactionUtc = Dt(8, 20) });
        repo.Accounts.Add(new ReportAccountRow { AccountId = 3, Name = "Never synced", TypeName = "Cash", TypeKind = AccountKind.Asset });
        repo.Snapshots.Add(new AccountSnapshotRow { AccountId = 1, AsOf = Dt(9, 1), Balance = 1000m });
        repo.Snapshots.Add(new AccountSnapshotRow { AccountId = 2, AsOf = Dt(9, 1), Balance = -300m });
        repo.Totals.Add(new DailyTotalRow { AccountId = 1, Day = Dt(8, 25), Total = 200m });
        repo.Totals.Add(new DailyTotalRow { AccountId = 2, Day = Dt(8, 25), Total = -50m });

        var report = await new ReportingService(repo).NetWorthAsync(from: new DateOnly(2026, 8, 24), to: new DateOnly(2026, 9, 1));

        Assert.Equal(new DateOnly(2026, 8, 1), report.EarliestData);
        Assert.Equal(new DateOnly(2026, 8, 20), report.ReliableFrom);
        Assert.Equal("Cash", Assert.Single(report.AssetTypes));
        Assert.Equal("Credit Card", Assert.Single(report.LiabilityTypes));
        Assert.Equal(9, report.Points.Count);

        var aug24 = report.Points[0];
        Assert.Equal(800m, aug24.Assets);          // 1000 - 200 received on the 25th
        Assert.Equal(-250m, aug24.Liabilities);    // -300 - (-50) spent on the 25th
        Assert.Equal(550m, aug24.NetWorth);
        Assert.True(aug24.IsEstimated);

        var sep1 = report.Points[^1];
        Assert.Equal(700m, sep1.NetWorth);
        Assert.False(sep1.IsEstimated);
        Assert.Equal(1000m, sep1.ByType["Cash"]);
        Assert.Equal(-300m, sep1.ByType["Credit Card"]);
    }

    [Fact]
    public async Task BalancesByType_GroupsInTypeOrder_AndKeepsExcludedOutOfTotals()
    {
        var repo = new FakeReportRepository();
        repo.Balances.Add(new AccountBalanceRow { AccountId = 1, Name = "Checking", TypeId = 1, TypeName = "Cash", TypeKind = AccountKind.Asset, TypeSortOrder = 10, Balance = 1000m, TypeColor = "#2a78d6" });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 2, Name = "401k", TypeId = 4, TypeName = "Retirement", TypeKind = AccountKind.Asset, TypeSortOrder = 40, Balance = 50000m });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 3, Name = "Kid's card", TypeId = 2, TypeName = "Credit Card", TypeKind = AccountKind.Liability, TypeSortOrder = 20, Balance = -200m, ExcludeFromNetWorth = true });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 4, Name = "Visa", TypeId = 2, TypeName = "Credit Card", TypeKind = AccountKind.Liability, TypeSortOrder = 20, Balance = -800m });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 5, Name = "Mystery", TypeId = null, Balance = 5m });

        var report = await new ReportingService(repo).BalancesByTypeAsync();

        Assert.Equal(4, report.Groups.Count);
        Assert.Equal("Cash", report.Groups[0].Name);          // assets first, in sort order
        Assert.Equal("Retirement", report.Groups[1].Name);
        Assert.Equal("Credit Card", report.Groups[2].Name);   // then liabilities
        Assert.Equal(ReportingService.UntypedName, report.Groups[3].Name); // untyped last
        Assert.Equal(-800m, report.Groups[2].Total);          // excluded card not in the total
        Assert.Equal(-200m, report.Groups[2].ExcludedTotal);
        Assert.Equal(2, report.Groups[2].AccountCount);
        Assert.Equal(51005m, report.Assets);
        Assert.Equal(-800m, report.Liabilities);
        Assert.Equal(50205m, report.NetWorth);
        Assert.Equal(1, report.ExcludedCount);
        Assert.Equal("#2a78d6", report.Groups[0].Color);
    }

    [Fact]
    public async Task BalancesByType_IncludeEmptyTypes_ListsEveryDefinedType()
    {
        var repo = new FakeReportRepository();
        repo.Balances.Add(new AccountBalanceRow { AccountId = 1, Name = "Checking", TypeId = 1, TypeName = "Cash", TypeKind = AccountKind.Asset, TypeSortOrder = 10, Balance = 100m });
        repo.Types.Add(new AccountCategory { Id = 1, Name = "Cash", Kind = AccountKind.Asset, SortOrder = 10 });
        repo.Types.Add(new AccountCategory { Id = 4, Name = "Retirement", Kind = AccountKind.Asset, SortOrder = 40, Color = "#4a3aa7" });
        repo.Types.Add(new AccountCategory { Id = 7, Name = "Mortgage", Kind = AccountKind.Liability, SortOrder = 70 });

        var report = await new ReportingService(repo).BalancesByTypeAsync(includeEmptyTypes: true);

        Assert.Equal(3, report.Groups.Count);
        Assert.Equal("Retirement", report.Groups[1].Name);
        Assert.Equal(0m, report.Groups[1].Total);
        Assert.Equal("#4a3aa7", report.Groups[1].Color);
        Assert.Equal("Mortgage", report.Groups[2].Name);
        Assert.Equal(100m, report.NetWorth);
    }

    [Fact]
    public async Task CollegeSavings_GroupsByOwner_ProjectsTo18_AndDerivesContributions()
    {
        var repo = new FakeReportRepository();
        var kidDob = new DateTime(2015, 3, 14);
        repo.Balances.Add(new AccountBalanceRow { AccountId = 1, Name = "Kid 529", BankName = "State 529", TypeId = 5, TypeName = "College Savings", TypeKind = AccountKind.Asset, Balance = 10000m, OwnerId = 1, OwnerName = "Kid", OwnerDateOfBirth = kidDob });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 2, Name = "Kid UTMA", BankName = "Broker", TypeId = 5, TypeName = "College Savings", TypeKind = AccountKind.Asset, Balance = 2000m, OwnerId = 1, OwnerName = "Kid", OwnerDateOfBirth = kidDob });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 3, Name = "Unowned 529", TypeId = 5, TypeName = "College Savings", TypeKind = AccountKind.Asset, Balance = 500m });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 4, Name = "Checking", TypeId = 1, TypeName = "Cash", TypeKind = AccountKind.Asset, Balance = 999m, OwnerId = 1, OwnerName = "Kid" });
        repo.Deposits[1] = 2400m; // $200/month over the trailing year

        var asOf = new DateOnly(2026, 9, 7);
        var report = await new ReportingService(repo).CollegeSavingsAsync(typeId: 5, annualRate: 0.06m, asOf: asOf);

        Assert.Equal("College Savings", report.TypeName);
        Assert.Equal(2, report.Owners.Count);
        var kid = report.Owners[0];
        Assert.Equal("Kid", kid.OwnerName);
        Assert.Equal(11, kid.AgeNow);
        Assert.Equal(new DateOnly(2033, 3, 14), kid.EighteenthBirthday);
        Assert.Equal(78, kid.MonthsTo18);
        Assert.Equal(2, kid.Accounts.Count);
        Assert.Equal(12000m, kid.CurrentTotal);
        Assert.Equal(200m, kid.Accounts[0].MonthlyContribution);
        Assert.Equal(CollegeProjection.FutureValue(10000m, 200m, 0.06m, 78), kid.Accounts[0].ProjectedAt18);
        Assert.Equal(CollegeProjection.FutureValue(2000m, 0m, 0.06m, 78), kid.Accounts[1].ProjectedAt18);
        Assert.True(kid.ProjectedAt18 > kid.CurrentTotal);

        var unassigned = report.Owners[1];
        Assert.Equal("Unassigned", unassigned.OwnerName);
        Assert.Null(unassigned.AgeNow);
        Assert.Null(unassigned.ProjectedAt18);
        Assert.Equal(500m, unassigned.CurrentTotal);
        Assert.Equal(12500m, report.CurrentTotal);
    }

    [Fact]
    public async Task CollegeSavings_PlannedContribution_BeatsOverride_WhichBeatsDepositAverage()
    {
        var repo = new FakeReportRepository();
        var dob = new DateTime(2015, 3, 14);
        repo.Balances.Add(new AccountBalanceRow { AccountId = 1, Name = "529 A", TypeId = 5, TypeName = "College Savings", Balance = 1000m, OwnerId = 1, OwnerName = "Kid", OwnerDateOfBirth = dob, PlannedMonthlyContribution = 350m });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 2, Name = "529 B", TypeId = 5, TypeName = "College Savings", Balance = 1000m, OwnerId = 1, OwnerName = "Kid", OwnerDateOfBirth = dob });
        repo.Balances.Add(new AccountBalanceRow { AccountId = 3, Name = "529 C", TypeId = 5, TypeName = "College Savings", Balance = 1000m, OwnerId = 1, OwnerName = "Kid", OwnerDateOfBirth = dob });
        repo.Deposits[3] = 1200m;
        var svc = new ReportingService(repo);
        var asOf = new DateOnly(2026, 9, 7);

        var withOverride = await svc.CollegeSavingsAsync(5, 0.06m, monthlyContributionOverride: 50m, asOf: asOf);
        var accts = withOverride.Owners[0].Accounts.OrderBy(a => a.AccountId).ToList();
        Assert.Equal((350m, ContributionSource.Planned), (accts[0].MonthlyContribution, accts[0].ContributionSource));
        Assert.Equal((50m, ContributionSource.PageOverride), (accts[1].MonthlyContribution, accts[1].ContributionSource));
        Assert.Equal((50m, ContributionSource.PageOverride), (accts[2].MonthlyContribution, accts[2].ContributionSource));
        Assert.Equal(450m, withOverride.Owners[0].MonthlyContribution);

        var noOverride = await svc.CollegeSavingsAsync(5, 0.06m, asOf: asOf);
        accts = noOverride.Owners[0].Accounts.OrderBy(a => a.AccountId).ToList();
        Assert.Equal(350m, accts[0].MonthlyContribution);
        Assert.Equal((0m, ContributionSource.DepositAverage), (accts[1].MonthlyContribution, accts[1].ContributionSource));
        Assert.Equal((100m, ContributionSource.DepositAverage), (accts[2].MonthlyContribution, accts[2].ContributionSource));
    }

    [Fact]
    public async Task NetWorth_NoSnapshots_ReturnsEmptyReport()
    {
        var repo = new FakeReportRepository();
        repo.Accounts.Add(new ReportAccountRow { AccountId = 1, Name = "Checking" });
        var report = await new ReportingService(repo).NetWorthAsync();
        Assert.Empty(report.Points);
        Assert.Null(report.ReliableFrom);
    }

    [Fact]
    public async Task IncomeVsExpense_FillsEmptyMonths()
    {
        var repo = new FakeReportRepository();
        repo.Monthly.Add(new MonthlyIncomeExpense { Month = new DateOnly(2026, 7, 1), Income = 5000m, Expense = 3200m });
        var rows = await new ReportingService(repo).IncomeVsExpenseAsync(new DateOnly(2026, 6, 15), new DateOnly(2026, 8, 3));

        Assert.Equal(3, rows.Count);
        Assert.Equal(new DateOnly(2026, 6, 1), rows[0].Month);
        Assert.Equal(0m, rows[0].Income);
        Assert.Equal(1800m, rows[1].Net);
        Assert.Equal(new DateOnly(2026, 8, 1), rows[2].Month);
    }

    [Fact]
    public async Task SpendingComparison_UsesSameMonthPriorYear()
    {
        var cmp = await new ReportingService(new FakeReportRepository()).SpendingComparisonAsync(new DateOnly(2026, 8, 17));
        Assert.Equal(new DateOnly(2026, 8, 1), cmp.Month);
        Assert.Equal(new DateOnly(2025, 8, 1), cmp.PriorMonth);
    }
}
