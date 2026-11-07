using Hoard.Core.Reporting;
using Hoard.Core.Reporting.Weekly;
using Hoard.Core.Scheduling;
using Xunit;

namespace Hoard.Core.Tests;

public sealed class ScheduleTests
{
    private static readonly TimeOnly Seven = new(7, 0);

    [Fact]
    public void Daily_IsDueOnceTheTimePasses_AndOncePerDay()
    {
        var morning = new DateTime(2026, 11, 9, 6, 59, 0);
        var afterSeven = new DateTime(2026, 11, 9, 7, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 8, 7, 0, 0), Schedules.LatestDaily(morning, Seven));
        Assert.Equal(new DateTime(2026, 11, 9, 7, 0, 0), Schedules.LatestDaily(afterSeven, Seven));

        var ranYesterday = new DateTime(2026, 11, 8, 7, 0, 0);
        Assert.False(Schedules.IsDue(Schedules.LatestDaily(morning, Seven), ranYesterday));
        Assert.True(Schedules.IsDue(Schedules.LatestDaily(afterSeven, Seven), ranYesterday));
        Assert.False(Schedules.IsDue(Schedules.LatestDaily(afterSeven.AddHours(5), Seven), afterSeven));
    }

    [Fact]
    public void Daily_MissedWhileClosed_RunsOnceOnTheNextStart()
    {
        var lastRun = new DateTime(2026, 11, 5, 7, 0, 0);
        var openedDaysLater = new DateTime(2026, 11, 9, 15, 30, 0);
        var latest = Schedules.LatestDaily(openedDaysLater, Seven);
        Assert.True(Schedules.IsDue(latest, lastRun));
        Assert.False(Schedules.IsDue(Schedules.LatestDaily(openedDaysLater.AddMinutes(1), Seven), latest));
    }

    [Fact]
    public void Weekly_LatestOccurrence_AndTheWeekItCovers()
    {
        var wednesday = new DateTime(2026, 11, 11, 12, 0, 0);
        var monday = Schedules.LatestWeekly(wednesday, DayOfWeek.Monday, Seven);
        Assert.Equal(new DateTime(2026, 11, 9, 7, 0, 0), monday);
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9)), Schedules.WeekBefore(monday));

        // Monday before 07:00: the occurrence is still last week's.
        var earlyMonday = new DateTime(2026, 11, 9, 6, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 2, 7, 0, 0), Schedules.LatestWeekly(earlyMonday, DayOfWeek.Monday, Seven));
        Assert.Equal(new DateTime(2026, 11, 9, 7, 0, 0), Schedules.NextWeekly(earlyMonday, DayOfWeek.Monday, Seven));
    }

    [Theory]
    [InlineData("07:30", 7, 30)]
    [InlineData("7:05", 7, 5)]
    [InlineData("nonsense", 6, 0)]
    [InlineData(null, 6, 0)]
    public void ParseTime_FallsBackOnBadInput(string? text, int hour, int minute) =>
        Assert.Equal(new TimeOnly(hour, minute), Schedules.ParseTime(text, new TimeOnly(6, 0)));
}

public sealed class WeeklyReportBuilderTests
{
    private static readonly DateOnly Start = new(2026, 11, 2); // a Monday
    private long _id;

    private SpendingActivityRow Row(DateOnly day, decimal spent, string group, string description = "SHOP", string? payee = null) => new()
    {
        Id = ++_id, AccountName = "Card", PostedAtUtc = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), Spent = spent,
        Description = description, Payee = payee, CategoryName = group, GroupName = group,
    };

    [Fact]
    public void Totals_CompareWithPreviousWeeks()
    {
        var rows = new List<SpendingActivityRow>();
        for (var w = 1; w <= 4; w++) rows.Add(Row(Start.AddDays(-7 * w + 2), 100m * w, "Food & Dining")); // 100, 200, 300, 400 in the 4 prior weeks
        rows.Add(Row(Start.AddDays(-7 * 8), 1000m, "Travel"));                                            // older: only in the 12-week view
        rows.Add(Row(Start, 50m, "Food & Dining", "KROGER #412"));
        rows.Add(Row(Start.AddDays(3), 120m, "Shopping", "AMAZON MKTPLACE PMTS", payee: "Amazon"));
        rows.Add(Row(Start.AddDays(3), -20m, "Shopping", "AMAZON REFUND", payee: "Amazon"));             // refund lowers the total
        rows.Add(Row(Start.AddDays(7), 999m, "Food & Dining"));                                           // next week: ignored

        var r = WeeklyReportBuilder.Build(Start, rows);

        Assert.Equal(150m, r.Total);
        Assert.Equal(3, r.TransactionCount);
        Assert.Equal(100m, r.PriorWeek);
        Assert.Equal(250m, r.FourWeekAverage);
        Assert.Equal((1000m + 1000m) / 12m, r.TwelveWeekAverage, 2);
        Assert.Equal(13, r.Trend.Count);
        Assert.Equal(150m, r.Trend[^1].Amount);
        Assert.Equal(7, r.Days.Count);
        Assert.Equal(100m, r.Days[3].Amount);

        var food = Assert.Single(r.Groups, g => g.Name == "Food & Dining");
        Assert.Equal(50m, food.Amount);
        Assert.Equal(250m, food.FourWeekAverage);
        Assert.Equal(-200m, food.Change);
        Assert.Equal("Shopping", r.Groups[0].Name);

        var amazon = Assert.Single(r.TopMerchants, m => m.Name == "Amazon");
        Assert.Equal(120m, amazon.Amount); // refunds are not merchant spending
        Assert.Equal(120m, r.Largest[0].Amount);
        Assert.Equal("Kroger", r.TopMerchants.Single(m => m.Name != "Amazon").Name);
    }

    [Fact]
    public void EmptyWeek_HasZerosNotErrors()
    {
        var r = WeeklyReportBuilder.Build(Start, []);
        Assert.Equal(0m, r.Total);
        Assert.Empty(r.Groups);
        Assert.All(r.Trend, w => Assert.Equal(0m, w.Amount));
        Assert.Contains("Weekly", WeeklyReportEmail.Html(r));
    }

    [Fact]
    public void AnalysisPrompt_LeavesMerchantsOutUnlessAllowed()
    {
        var r = WeeklyReportBuilder.Build(Start, [Row(Start, 42m, "Shopping", payee: "Secret Store")]);
        Assert.DoesNotContain("Secret Store", WeeklyReportService.AnalysisPrompt(r, allowTransactionDetail: false));
        Assert.Contains("Secret Store", WeeklyReportService.AnalysisPrompt(r, allowTransactionDetail: true));
    }

    [Fact]
    public void Email_EncodesUserText()
    {
        var r = WeeklyReportBuilder.Build(Start, [Row(Start, 10m, "<script>", payee: "A&B")]);
        var html = WeeklyReportEmail.Html(r);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("A&amp;B", html);
    }

    [Fact]
    public void Markdown_ConvertsTheSubsetClaudeWrites()
    {
        var html = WeeklyReportEmail.MarkdownToHtml("Spending was **up**.\n\n### Trends\n- Dining *rose*\n- `x` < y\n\n1. Cook more\n2. Cancel\n\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<b>up</b>", html);
        Assert.Contains("Trends</div>", html);
        Assert.Contains("<li style=\"margin:0 0 4px\">Dining <i>rose</i></li>", html);
        Assert.Contains("&lt; y", html);
        Assert.Contains("<ol", html);
        Assert.Contains("<th align=\"left\"", html);
        Assert.DoesNotContain("---", html);
    }

    [Fact]
    public void Recipients_SplitOnCommasSemicolonsAndSpaces() =>
        Assert.Equal(["a@x.com", "b@y.org"], WeeklyReportService.ParseRecipients(" a@x.com; b@y.org,A@X.com \n"));
}
