using System.Text.Json;
using Hoard.Core.Advisor;
using Hoard.Core.Budgeting;
using Hoard.Core.Configuration;
using Hoard.Core.Email;
using Hoard.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace Hoard.Core.Reporting.Weekly;

public sealed record WeeklySendResult(DateTime Occurrence, WeeklySpendingReport Report, IReadOnlyList<string> Recipients);

/// <summary>
/// The weekly spending report: builds a week, asks Claude (through the AI advisor) for trends and ways to spend less,
/// emails it, and runs the schedule in <see cref="WeeklyReportSettings"/>.
/// </summary>
public sealed class WeeklyReportService
{
    /// <summary>A failed scheduled send is retried at most this often until the next occurrence.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly IReportRepository _reports;
    private readonly BudgetService _budget;
    private readonly AdvisorService _advisor;
    private readonly IEmailSender _email;
    private readonly HoardSettings _settings;
    private readonly ILogger<WeeklyReportService> _log;
    private (DateTime Occurrence, WeeklySpendingReport Report)? _pending;

    public WeeklyReportService(IReportRepository reports, BudgetService budget, AdvisorService advisor, IEmailSender email,
        HoardSettings settings, ILogger<WeeklyReportService> log)
    {
        _reports = reports;
        _budget = budget;
        _advisor = advisor;
        _email = email;
        _settings = settings;
        _log = log;
    }

    public WeeklyReportSettings Settings => _settings.WeeklyReport;

    /// <summary>The last complete seven days ending the day before the most recent send day (Monday → last Mon–Sun).</summary>
    public static DateOnly LatestWeekStart(DateTime nowLocal, DayOfWeek sendDay) =>
        Schedules.WeekBefore(Schedules.LatestWeekly(nowLocal, sendDay, TimeOnly.MinValue)).Start;

    public async Task<WeeklySpendingReport> BuildAsync(DateOnly start, CancellationToken ct = default)
    {
        var end = start.AddDays(7);
        var rows = await _reports.ListSpendingActivityAsync(start.AddDays(-7 * WeeklyReportBuilder.TrendWeeks), end, ct);
        var budgetMonth = await _budget.GetMonthAsync(BudgetService.MonthOf(end.AddDays(-1)), today: end.AddDays(-1) < DateOnly.FromDateTime(DateTime.Today) ? end.AddDays(-1) : null, ct: ct);
        var report = WeeklyReportBuilder.Build(start, rows, budgetMonth);
        var income = await _budget.IncomeAsync(budgetMonth, BudgetService.DefaultIncomeAverageMonths, ct: ct);
        if (income.AverageIncome > 0) report.WeeklyIncome = Math.Round(income.AverageIncome * 12m / 52m, 2);
        return report;
    }

    /// <summary>Fills <see cref="WeeklySpendingReport.AiAnalysis"/>, or <see cref="WeeklySpendingReport.AiNote"/> with the reason it could not.</summary>
    public async Task AnalyzeAsync(WeeklySpendingReport report, CancellationToken ct = default)
    {
        if (!await _advisor.IsReadyAsync(ct))
        {
            report.AiNote = "AI analysis is off: turn on the AI Advisor (with an Anthropic API key) to include trends and suggestions.";
            return;
        }
        try
        {
            var agent = await _advisor.StartConversationAsync(ct);
            var answer = await agent.AskAsync(AnalysisPrompt(report, _advisor.Settings.AllowTransactionDetail), ct: ct);
            report.AiAnalysis = answer.Markdown;
            report.AiNote = null;
        }
        catch (Exception ex) when (ex is AdvisorException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Weekly report analysis failed");
            report.AiNote = "The AI analysis could not be generated this time: " + ex.Message;
        }
    }

    public Task SendAsync(WeeklySpendingReport report, IReadOnlyCollection<string> recipients, CancellationToken ct = default) =>
        _email.SendAsync(recipients, WeeklyReportEmail.Subject(report), WeeklyReportEmail.Html(report), WeeklyReportEmail.Text(report), ct);

    /// <summary>The next automatic send, or null when the schedule is off.</summary>
    public DateTime? NextScheduled(DateTime nowLocal)
    {
        if (!Settings.Enabled) return null;
        var at = Schedules.ParseTime(Settings.Time, new TimeOnly(7, 0));
        var latest = Schedules.LatestWeekly(nowLocal, Settings.Day, at);
        return Schedules.IsDue(latest, Settings.LastScheduledFor) ? latest : Schedules.NextWeekly(nowLocal, Settings.Day, at);
    }

    /// <summary>Saves the schedule. Turning it on (or moving it) starts with the next occurrence, not one that already passed.</summary>
    public void SaveSchedule(bool enabled, DayOfWeek day, TimeOnly time, IEnumerable<string> recipients, bool includeAi, DateTime nowLocal)
    {
        var s = Settings;
        var changed = !s.Enabled || s.Day != day || s.Time != Schedules.FormatTime(time);
        s.Enabled = enabled;
        s.Day = day;
        s.Time = Schedules.FormatTime(time);
        s.Recipients = ParseRecipients(string.Join(";", recipients));
        s.IncludeAiAnalysis = includeAi;
        if (enabled && changed) s.LastScheduledFor = Schedules.LatestWeekly(nowLocal, day, time);
        SettingsStore.Save(_settings);
    }

    public static List<string> ParseRecipients(string? text) =>
        (text ?? "").Split([';', ',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Sends the scheduled report when one is due (enabled, recipients set, occurrence not yet sent, no failed attempt
    /// in the last <see cref="RetryInterval"/>). Returns what was sent, or null when nothing was due.
    /// </summary>
    public async Task<WeeklySendResult?> RunScheduledAsync(DateTime nowLocal, CancellationToken ct = default)
    {
        var s = Settings;
        if (!s.Enabled || s.Recipients.Count == 0) return null;
        var at = Schedules.ParseTime(s.Time, new TimeOnly(7, 0));
        var occurrence = Schedules.LatestWeekly(nowLocal, s.Day, at);
        if (!Schedules.IsDue(occurrence, s.LastScheduledFor)) return null;
        if (s.LastAttemptUtc is { } tried && DateTime.UtcNow - tried < RetryInterval) return null;

        s.LastAttemptUtc = DateTime.UtcNow;
        try
        {
            var report = _pending is { } p && p.Occurrence == occurrence ? p.Report : null;
            if (report is null)
            {
                report = await BuildAsync(Schedules.WeekBefore(occurrence).Start, ct);
                if (s.IncludeAiAnalysis) await AnalyzeAsync(report, ct);
                _pending = (occurrence, report);
            }
            await SendAsync(report, s.Recipients, ct);
            _pending = null;
            s.LastScheduledFor = occurrence;
            s.LastSentUtc = DateTime.UtcNow;
            s.LastError = null;
            _log.LogInformation("Weekly report for {Start} sent to {Count} recipient(s)", report.Start, s.Recipients.Count);
            return new WeeklySendResult(occurrence, report, s.Recipients.ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            s.LastError = ex.Message;
            _log.LogWarning(ex, "Scheduled weekly report failed; will retry");
            throw;
        }
        finally
        {
            SettingsStore.Save(_settings);
        }
    }

    /// <summary>Records a manual send, so the page can show when the report last went out.</summary>
    public void RecordManualSend()
    {
        Settings.LastSentUtc = DateTime.UtcNow;
        Settings.LastError = null;
        SettingsStore.Save(_settings);
    }

    /// <summary>The question put to the advisor: the week's figures as JSON, and what to write.</summary>
    public static string AnalysisPrompt(WeeklySpendingReport r, bool allowTransactionDetail)
    {
        var data = new Dictionary<string, object?>
        {
            ["week"] = new { start = r.Start.ToString("yyyy-MM-dd"), end = r.End.ToString("yyyy-MM-dd") },
            ["spent_this_week"] = r.Total,
            ["transactions"] = r.TransactionCount,
            ["previous_week"] = r.PriorWeek,
            ["four_week_average"] = Math.Round(r.FourWeekAverage, 2),
            ["twelve_week_average"] = Math.Round(r.TwelveWeekAverage, 2),
            ["average_weekly_income"] = r.WeeklyIncome,
            ["weekly_totals_oldest_first"] = r.Trend.Select(w => new { week_of = w.Start.ToString("yyyy-MM-dd"), spent = w.Amount }),
            ["by_day"] = r.Days.Select(d => new { day = d.Date.ToString("ddd yyyy-MM-dd"), spent = d.Amount }),
            ["by_category_group"] = r.Groups.Select(g => new { group = g.Name, this_week = g.Amount, four_week_average = Math.Round(g.FourWeekAverage, 2) }),
            ["budget_month_to_date"] = r.Budget is { Target: > 0 } b
                ? new { month = b.Month.ToString("yyyy-MM"), day = b.DayOfMonth, days_in_month = b.DaysInMonth, target = b.Target, spent = b.Spent, projected = b.Projected,
                        over = b.Over.Select(o => new { group = o.Group, actual = o.Actual, target = o.Target }),
                        at_risk = b.AtRisk.Select(a => new { group = a.Group, projected = a.Projected, target = a.Target }) }
                : null,
        };
        if (allowTransactionDetail)
        {
            data["top_merchants"] = r.TopMerchants.Select(m => new { merchant = m.Name, spent = m.Amount, count = m.Count });
            data["largest_charges"] = r.Largest.Select(t => new { date = t.Date.ToString("yyyy-MM-dd"), merchant = t.Merchant, category = t.Category, amount = t.Amount });
        }
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
        return $"""
            Write the analysis section of my weekly spending email for {r.Start:MMMM d} to {r.End:MMMM d, yyyy}. The week's figures, already
            computed by Hoard (spending-report accounts only; transfers, income and duplicates excluded), are below as JSON. You may call
            your tools for longer history if it helps explain a trend, but keep tool use light.

            Write, in Markdown:
            1. One or two sentences summarizing the week against recent weeks (and against income if given).
            2. "### Trends" — 2 to 4 bullets on what is rising, falling or unusual (categories, merchants, days, one-off large charges),
               with amounts.
            3. "### Ways to spend less" — 3 to 5 specific, practical suggestions tied to this household's actual spending, each with a
               rough monthly saving where you can estimate one.
            Keep it under 300 words. No greeting or sign-off; it goes straight into the email.

            {json}
            """;
    }
}
