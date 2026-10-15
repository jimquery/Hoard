using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Hoard.Core.Advisor;
using Hoard.Core.Budgeting;
using Hoard.Core.Domain;
using Hoard.Core.Reporting;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class BudgetForecasterTests
{
    [Fact]
    public void FlatHistory_PredictsTheTypicalMonth_WithNoTrend()
    {
        var f = BudgetForecaster.Forecast(Enumerable.Repeat(500m, 12).ToList());
        Assert.Equal(500m, f.Predicted);
        Assert.Equal(0m, f.MonthlyTrend);
        Assert.Equal(500m, f.Low);
        Assert.Equal(500m, f.High);
        Assert.Equal(500m, f.SuggestedTarget);
    }

    [Fact]
    public void OneOffSpike_DoesNotSetTheBaseline()
    {
        var history = Enumerable.Repeat(300m, 12).ToList();
        history[4] = 2400m; // an annual bill
        var f = BudgetForecaster.Forecast(history);

        Assert.Equal(475m, f.Average);
        Assert.Equal(300m, f.TypicalMonth);
        Assert.InRange(f.Predicted, 290m, 320m);
        Assert.True(f.High > f.Predicted);
    }

    [Fact]
    public void SteadyRise_PullsTheForecastUpTowardTheTrendLine()
    {
        var history = Enumerable.Range(0, 12).Select(i => 400m + 20m * i).ToList(); // 400 .. 620
        var f = BudgetForecaster.Forecast(history);

        Assert.Equal(20m, f.MonthlyTrend);
        Assert.Equal(1.0, f.TrendStrength, 3);
        // Typical month 510, next point on the line 640: a perfect trend moves the forecast halfway.
        Assert.Equal(575m, f.Predicted);
        Assert.Equal(580m, f.SuggestedTarget);
    }

    [Fact]
    public void NoSpending_IsEmpty()
    {
        Assert.Equal(BudgetForecast.Empty, BudgetForecaster.Forecast(new decimal[12]));
        Assert.Equal(BudgetForecast.Empty, BudgetForecaster.Forecast([]));
    }

    [Theory]
    [InlineData(100, 600, 10, 30, 500)]  // spent 100 by the 10th; 20 days left at 600/month
    [InlineData(700, 600, 10, 30, 1100)]
    [InlineData(250, 600, 30, 30, 250)]  // last day: nothing left to add
    public void MonthEndProjection_AddsTheForecastForTheDaysLeft(decimal spent, decimal predicted, int day, int days, decimal expected) =>
        Assert.Equal(expected, BudgetForecaster.ProjectMonthEnd(spent, predicted, day, days));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(42.10, 45)]
    [InlineData(101, 110)]
    [InlineData(1234, 1250)]
    public void Tidy_RoundsUpToAFriendlyStep(decimal amount, decimal expected) => Assert.Equal(expected, BudgetForecaster.Tidy(amount));
}

public sealed class BudgetServiceTests
{
    private static readonly DateOnly Oct = new(2026, 10, 1);

    private readonly FakeBudgetRepository _budgets = new();
    private readonly FakeReportRepository _reports = new();
    private readonly FakeCategoryRepository _categories = new();

    public BudgetServiceTests()
    {
        _categories.Categories.Add(new TransactionCategory { Id = 1, Name = "Food & Dining", Kind = CategoryKind.Expense, SortOrder = 1 });
        _categories.Categories.Add(new TransactionCategory { Id = 2, Name = "Restaurants", ParentId = 1, Kind = CategoryKind.Expense });
        _categories.Categories.Add(new TransactionCategory { Id = 3, Name = "Shopping", Kind = CategoryKind.Expense, SortOrder = 2 });
        _categories.Categories.Add(new TransactionCategory { Id = 4, Name = "Income", Kind = CategoryKind.Income, SortOrder = 3 });
        for (var i = 1; i <= 12; i++)
            _reports.Spend.Add(new MonthGroupSpend { Month = Oct.AddMonths(-i), GroupId = 1, GroupName = "Food & Dining", Amount = 600m });
    }

    private BudgetService Sut() => new(_budgets, _reports, _categories);

    [Fact]
    public void TargetHistory_UsesTheLatestChangeOnOrBeforeTheMonth()
    {
        var rows = new List<BudgetTargetRow>
        {
            new() { GroupId = 1, EffectiveMonth = new DateTime(2026, 1, 1), MonthlyTarget = 500m },
            new() { GroupId = 1, EffectiveMonth = new DateTime(2026, 6, 1), MonthlyTarget = 650m },
            new() { GroupId = 1, EffectiveMonth = new DateTime(2026, 9, 1), MonthlyTarget = null },
        };
        Assert.Null(BudgetService.TargetFor(rows, 1, new DateOnly(2025, 12, 1)));
        Assert.Equal(500m, BudgetService.TargetFor(rows, 1, new DateOnly(2026, 5, 1)));
        Assert.Equal(650m, BudgetService.TargetFor(rows, 1, new DateOnly(2026, 8, 1)));
        Assert.Null(BudgetService.TargetFor(rows, 1, new DateOnly(2026, 10, 1)));
        Assert.Null(BudgetService.TargetFor(rows, 2, new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public async Task CurrentMonth_ProjectsMonthEnd_AndFlagsAtRisk()
    {
        _reports.Spend.Add(new MonthGroupSpend { Month = Oct, GroupId = 1, GroupName = "Food & Dining", Amount = 400m });
        await Sut().SetTargetAsync(1, 550m, Oct);

        var month = await Sut().GetMonthAsync(Oct, today: new DateOnly(2026, 10, 11));
        var food = Assert.Single(month.Lines, l => l.GroupId == 1);

        Assert.True(month.IsCurrentMonth);
        Assert.Equal(600m, food.Forecast.Predicted);
        Assert.Equal(400m, food.Actual);
        Assert.Equal(400m + 600m * 20 / 31, food.Projected, 2);
        Assert.Equal(BudgetStatus.AtRisk, food.Status);
        Assert.Equal(150m, food.Remaining);
        // Every expense group is listed (Shopping, with nothing yet); income groups are not.
        Assert.Contains(month.Lines, l => l.GroupName == "Shopping");
        Assert.DoesNotContain(month.Lines, l => l.GroupName == "Income");
    }

    [Fact]
    public async Task PastMonth_ComparesActualWithTheTargetThatAppliedThen()
    {
        var sep = Oct.AddMonths(-1);
        await Sut().SetTargetAsync(1, 550m, sep);
        await Sut().SetTargetAsync(1, 700m, Oct);

        var month = await Sut().GetMonthAsync(sep, today: new DateOnly(2026, 10, 11));
        var food = Assert.Single(month.Lines, l => l.GroupId == 1);

        Assert.False(month.IsCurrentMonth);
        Assert.Equal(550m, food.Target);
        Assert.Equal(600m, food.Projected);
        Assert.Equal(BudgetStatus.Over, food.Status);
    }

    [Fact]
    public async Task ApplySuggested_SetsOnlyGroupsWithoutATarget_UnlessOverwriting()
    {
        _reports.Spend.Add(new MonthGroupSpend { Month = Oct.AddMonths(-2), GroupId = 3, GroupName = "Shopping", Amount = 1200m });
        await Sut().SetTargetAsync(3, 80m, Oct);

        Assert.Equal(1, await Sut().ApplySuggestedTargetsAsync(Oct, overwrite: false));
        Assert.Equal(600m, BudgetService.TargetFor(_budgets.Rows, 1, Oct));
        Assert.Equal(80m, BudgetService.TargetFor(_budgets.Rows, 3, Oct));

        Assert.Equal(2, await Sut().ApplySuggestedTargetsAsync(Oct, overwrite: true));
        Assert.NotEqual(80m, BudgetService.TargetFor(_budgets.Rows, 3, Oct));
    }

    [Fact]
    public async Task FutureMonth_ForecastsFromCompleteMonthsOnly()
    {
        _reports.Spend.Add(new MonthGroupSpend { Month = Oct, GroupId = 1, GroupName = "Food & Dining", Amount = 50m }); // October so far

        var nov = await Sut().GetMonthAsync(Oct.AddMonths(1), today: new DateOnly(2026, 10, 11));
        var food = Assert.Single(nov.Lines, l => l.GroupId == 1);

        Assert.Equal(600m, food.Forecast.Predicted);
        Assert.Equal(0m, food.Actual);
        Assert.DoesNotContain(50m, food.History);
    }

    [Fact]
    public async Task ZeroTarget_RemovesIt()
    {
        await Sut().SetTargetAsync(1, 500m, Oct);
        await Sut().SetTargetAsync(1, 0m, Oct);
        Assert.Null(BudgetService.TargetFor(_budgets.Rows, 1, Oct));
    }
}

public sealed class AdvisorPlumbingTests
{
    [Fact]
    public void DetailTools_AreOfferedOnlyWhenAllowed()
    {
        var aggregate = FinancialToolbox.DefinitionsFor(allowTransactionDetail: false).Select(d => d.Name).ToList();
        var all = FinancialToolbox.DefinitionsFor(allowTransactionDetail: true).Select(d => d.Name).ToList();

        Assert.Contains("get_budget", aggregate);
        Assert.DoesNotContain("search_transactions", aggregate);
        Assert.DoesNotContain("get_top_merchants", aggregate);
        Assert.Contains("search_transactions", all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void EveryToolSchema_IsAClosedObject()
    {
        foreach (var d in FinancialToolbox.Definitions)
        {
            Assert.Equal("object", d.InputSchema["type"]!.GetValue<string>());
            Assert.False(d.InputSchema["additionalProperties"]!.GetValue<bool>());
            var props = d.InputSchema["properties"]!.AsObject().Select(p => p.Key).ToHashSet();
            Assert.All(d.InputSchema["required"]!.AsArray(), r => Assert.Contains(r!.GetValue<string>(), props));
        }
    }

    [Fact]
    public void DefaultFallbacks_SerializeAsTheDefaultKeyword() =>
        Assert.Equal("\"default\"", JsonSerializer.Serialize<BetaFallbacksParam>(new Default()));

    [Fact]
    public void ToolCallsAreDescribedForTheProgressList()
    {
        var input = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("netflix") };
        Assert.Equal("Searching transactions for \"netflix\"", AdvisorAgent.Describe("search_transactions", input));
    }
}

public sealed class FakeBudgetRepository : IBudgetRepository
{
    public List<BudgetTargetRow> Rows { get; } = new();
    public Task<IReadOnlyList<BudgetTargetRow>> ListTargetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BudgetTargetRow>>(Rows.ToList());
    public Task SetTargetAsync(long groupId, DateOnly fromMonth, decimal? monthlyTarget, CancellationToken ct = default)
    {
        var at = fromMonth.ToDateTime(TimeOnly.MinValue);
        Rows.RemoveAll(r => r.GroupId == groupId && r.EffectiveMonth == at);
        Rows.Add(new BudgetTargetRow { GroupId = groupId, EffectiveMonth = at, MonthlyTarget = monthlyTarget });
        return Task.CompletedTask;
    }
}
