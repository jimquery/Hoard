using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoard.Core.Configuration;
using Hoard.Core.Reporting.Weekly;
using Hoard.Core.Scheduling;
using Hoard.Desktop.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace Hoard.Desktop.ViewModels;

public sealed record WeekOption(DateOnly Start)
{
    public string Label => $"{Start:MMM d} – {Start.AddDays(6):MMM d, yyyy}";
    public override string ToString() => Label;
}

public sealed record DayOfWeekOption(DayOfWeek Day)
{
    public string Label => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(Day);
    public override string ToString() => Label;
}

/// <summary>
/// Reports › Weekly spending: a week's breakdown with Claude's analysis, "Send now", and the schedule and recipients for
/// the automatic weekly email (sent by <see cref="DesktopScheduler"/> while Hoard is open).
/// </summary>
public sealed partial class WeeklyReportViewModel : ObservableObject
{
    private readonly WeeklyReportService _service;
    private readonly HoardSettings _settings;
    private readonly ChartTheme _t;
    private bool _loading;

    public WeeklyReportViewModel(WeeklyReportService service, HoardSettings settings, ThemeService theme)
    {
        _service = service;
        _settings = settings;
        _t = new ChartTheme(theme);
        var s = settings.WeeklyReport;
        _scheduleEnabled = s.Enabled;
        _selectedDay = DayOptions.First(d => d.Day == s.Day);
        _scheduleTime = Schedules.FormatTime(Schedules.ParseTime(s.Time, new TimeOnly(7, 0)));
        _recipientsText = string.Join("; ", s.Recipients);
        _includeAi = s.IncludeAiAnalysis;
        RebuildWeeks();
        _trendXAxes = [_t.CategoryAxis([])];
        _trendYAxes = [_t.CurrencyAxis()];
        theme.Changed += () => { if (Report is not null) ShowCharts(Report); };
    }

    /// <summary>Set by the main view model: opens Settings (for the Email section).</summary>
    public Action? OpenSettings { get; set; }

    public IReadOnlyList<DayOfWeekOption> DayOptions { get; } = Enum.GetValues<DayOfWeek>().Select(d => new DayOfWeekOption(d)).ToList();
    public ObservableCollection<WeekOption> WeekOptions { get; } = new();
    [ObservableProperty] private WeekOption? _selectedWeek;
    [ObservableProperty] private WeeklySpendingReport? _report;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _status = "";

    // Schedule
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private DayOfWeekOption _selectedDay;
    [ObservableProperty] private string _scheduleTime;
    [ObservableProperty] private string _recipientsText;
    [ObservableProperty] private bool _includeAi;

    // Report display
    [ObservableProperty] private ISeries[] _trendSeries = [];
    [ObservableProperty] private Axis[] _trendXAxes;
    [ObservableProperty] private Axis[] _trendYAxes;
    [ObservableProperty] private string _aiMarkdown = "";
    public bool HasReport => Report is not null;
    public bool HasAnalysis => !string.IsNullOrWhiteSpace(Report?.AiAnalysis);
    public string Headline => Report is null ? "" : Report.Title;
    public string TotalText => Report is null ? "" : Report.Total.ToString("C0", CultureInfo.CurrentCulture);
    public string TotalDetail => Report is null ? "" : $"{Report.TransactionCount} transaction(s)";
    public decimal VsPriorWeek => Report is null ? 0 : Report.Total - Report.PriorWeek;
    public string PriorDetail => Report is null ? "" : $"{Report.PriorWeek:C0} the week before";
    public decimal VsFourWeek => Report is null ? 0 : Report.Total - Report.FourWeekAverage;
    public string FourWeekDetail => Report is null ? "" : $"average {Report.FourWeekAverage:C0} a week";
    public string IncomeText => Report?.WeeklyIncome is { } w && w > 0 ? (Report.Total / w).ToString("0%", CultureInfo.CurrentCulture) : "—";
    public string IncomeDetail => Report?.WeeklyIncome is { } w && w > 0 ? $"of about {w:C0} average weekly income" : "no income recorded";
    public string BudgetText => Report?.Budget is { Target: > 0 } b
        ? $"{b.Month:MMMM}: {b.Spent:C0} of {b.Target:C0} spent in budgeted groups, on pace for {b.Projected:C0}." +
          (b.Over.Count > 0 ? $" Over: {string.Join(", ", b.Over.Select(o => o.Group))}." : "") +
          (b.AtRisk.Count > 0 ? $" At risk: {string.Join(", ", b.AtRisk.Select(o => o.Group))}." : "")
        : "No budget targets set, so there is nothing to compare against (Budget page).";

    public string ScheduleSummary
    {
        get
        {
            var s = _settings.WeeklyReport;
            var parts = new List<string>();
            if (_service.NextScheduled(DateTime.Now) is { } next)
                parts.Add(s.Recipients.Count == 0 ? "Add recipients to start sending." : $"Next email: {next:dddd, MMM d 'at' h:mm tt}" + (next <= DateTime.Now ? " (due now; sends within a minute)" : ""));
            else parts.Add("Automatic email is off.");
            if (s.LastSentUtc is { } sent) parts.Add($"Last sent {sent.ToLocalTime():ddd MMM d, h:mm tt}.");
            if (!string.IsNullOrWhiteSpace(s.LastError)) parts.Add($"Last attempt failed: {s.LastError}");
            if (!_settings.Email.IsConfigured) parts.Add("Outgoing email is not set up yet (Settings › Email).");
            return string.Join("  ", parts);
        }
    }

    public bool EmailNotConfigured => !_settings.Email.IsConfigured;

    partial void OnReportChanged(WeeklySpendingReport? value)
    {
        foreach (var name in new[] { nameof(HasReport), nameof(HasAnalysis), nameof(Headline), nameof(TotalText), nameof(TotalDetail), nameof(VsPriorWeek),
                     nameof(PriorDetail), nameof(VsFourWeek), nameof(FourWeekDetail), nameof(IncomeText), nameof(IncomeDetail), nameof(BudgetText) })
            OnPropertyChanged(name);
        AiMarkdown = value?.AiAnalysis ?? (value?.AiNote is { } note ? $"*{note}*" : "*Click \"Analyze with AI\" for trends and ways to spend less.*");
        if (value is not null) ShowCharts(value);
        Groups.Clear(); Merchants.Clear(); Largest.Clear();
        if (value is null) return;
        foreach (var g in value.Groups) Groups.Add(g);
        foreach (var m in value.TopMerchants) Merchants.Add(m);
        foreach (var t in value.Largest) Largest.Add(t);
    }

    public ObservableCollection<GroupWeekSpend> Groups { get; } = new();
    public ObservableCollection<MerchantSpend> Merchants { get; } = new();
    public ObservableCollection<WeeklyTransaction> Largest { get; } = new();

    partial void OnSelectedWeekChanged(WeekOption? value) { if (value is not null && !_loading) _ = Guard(() => LoadWeekAsync(value.Start), "Building the report…"); }

    private void RebuildWeeks()
    {
        var latest = WeeklyReportService.LatestWeekStart(DateTime.Now, SelectedDay.Day);
        var keep = SelectedWeek?.Start;
        _loading = true;
        try
        {
            WeekOptions.Clear();
            for (var i = 0; i < 26; i++) WeekOptions.Add(new WeekOption(latest.AddDays(-7 * i)));
            SelectedWeek = WeekOptions.FirstOrDefault(w => w.Start == keep) ?? WeekOptions[0];
        }
        finally { _loading = false; }
    }

    /// <summary>Called when the tab opens: builds the latest week if nothing is showing yet.</summary>
    public Task EnsureLoadedAsync() => Report is null && SelectedWeek is { } w ? Guard(() => LoadWeekAsync(w.Start), "Building the report…") : Task.CompletedTask;

    private async Task LoadWeekAsync(DateOnly start)
    {
        Report = await _service.BuildAsync(start);
        Status = "";
    }

    [RelayCommand]
    private Task RefreshAsync() => SelectedWeek is { } w ? Guard(() => LoadWeekAsync(w.Start), "Building the report…") : Task.CompletedTask;

    [RelayCommand]
    private Task AnalyzeAsync() => Guard(async () =>
    {
        if (Report is null) return;
        await _service.AnalyzeAsync(Report);
        OnReportChanged(Report);
        Status = Report.AiAnalysis is null ? Report.AiNote ?? "" : "Analysis added.";
    }, "Asking Claude for an analysis (this can take a minute)…");

    [RelayCommand]
    private Task SendNowAsync() => Guard(async () =>
    {
        if (Report is null) return;
        var recipients = WeeklyReportService.ParseRecipients(RecipientsText);
        if (recipients.Count == 0) { Status = "Add at least one recipient."; return; }
        if (IncludeAi && Report.AiAnalysis is null)
        {
            BusyText = "Asking Claude for an analysis…";
            await _service.AnalyzeAsync(Report);
            OnReportChanged(Report);
        }
        BusyText = "Sending…";
        await _service.SendAsync(Report, recipients);
        _service.RecordManualSend();
        OnPropertyChanged(nameof(ScheduleSummary));
        Status = $"Sent the report for {Report.Start:MMM d}–{Report.End:MMM d} to {string.Join(", ", recipients)}.";
    }, "Sending…");

    /// <summary>Opens the email exactly as it would be sent, in the default browser.</summary>
    [RelayCommand]
    private void PreviewEmail()
    {
        if (Report is null) return;
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hoard-weekly-{Report.Start:yyyy-MM-dd}.html");
            System.IO.File.WriteAllText(path, WeeklyReportEmail.Html(Report));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"Opened a preview of the email ({WeeklyReportEmail.Subject(Report)}).";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void SaveSchedule()
    {
        var recipients = WeeklyReportService.ParseRecipients(RecipientsText);
        var bad = recipients.Where(r => !r.Contains('@') || r.StartsWith('@') || r.EndsWith('@')).ToList();
        if (bad.Count > 0) { Status = $"Not an email address: {string.Join(", ", bad)}"; return; }
        if (ScheduleEnabled && recipients.Count == 0) { Status = "Add at least one recipient before turning the schedule on."; return; }
        var time = Schedules.ParseTime(ScheduleTime, new TimeOnly(7, 0));
        _service.SaveSchedule(ScheduleEnabled, SelectedDay.Day, time, recipients, IncludeAi, DateTime.Now);
        ScheduleTime = Schedules.FormatTime(time);
        RecipientsText = string.Join("; ", recipients);
        RebuildWeeks();
        OnPropertyChanged(nameof(ScheduleSummary));
        OnPropertyChanged(nameof(EmailNotConfigured));
        Status = ScheduleEnabled ? $"Saved. The report goes out every {SelectedDay.Label} at {time.ToString("h:mm tt", CultureInfo.CurrentCulture)} while Hoard is open." : "Saved. Automatic email is off.";
    }

    [RelayCommand]
    private void GoToEmailSettings() => OpenSettings?.Invoke();

    /// <summary>Called by the scheduler after an automatic send.</summary>
    public void OnScheduledSend(WeeklySendResult result)
    {
        OnPropertyChanged(nameof(ScheduleSummary));
        if (SelectedWeek?.Start == result.Report.Start) Report = result.Report;
    }

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(ScheduleSummary));
        OnPropertyChanged(nameof(EmailNotConfigured));
    }

    private async Task Guard(Func<Task> work, string busyText)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = busyText;
        try { await work(); }
        catch (Exception ex) { Status = ex.Message; ErrorLog.Write(ex, "Weekly report"); }
        finally { IsBusy = false; BusyText = ""; }
    }

    private void ShowCharts(WeeklySpendingReport r)
    {
        var current = r.Trend.Count - 1;
        TrendXAxes = [_t.CategoryAxis(r.Trend.Select(w => w.Start.ToString("MMM d", CultureInfo.CurrentCulture)).ToArray())];
        TrendYAxes = [_t.CurrencyAxis()];
        TrendSeries =
        [
            new ColumnSeries<double>
            {
                Name = "Spent",
                Values = r.Trend.Select(w => (double)w.Amount).ToList(),
                Fill = new SolidColorPaint(_t.Palette[0].WithAlpha(120)),
                MaxBarWidth = 34, Rx = 3, Ry = 3,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
            },
            new ColumnSeries<double?>
            {
                Name = "This week",
                Values = r.Trend.Select((w, i) => i == current ? (double?)w.Amount : null).ToList(),
                Fill = new SolidColorPaint(_t.Palette[0]),
                MaxBarWidth = 34, Rx = 3, Ry = 3,
                IgnoresBarPosition = true,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
            },
            new LineSeries<double>
            {
                Name = "12-week average",
                Values = r.Trend.Select(_ => (double)r.TwelveWeekAverage).ToList(),
                Stroke = new SolidColorPaint(_t.Ink) { StrokeThickness = 1.5f, PathEffect = new LiveChartsCore.SkiaSharpView.Painting.Effects.DashEffect([5, 4]) },
                Fill = null, GeometrySize = 0,
                YToolTipLabelFormatter = p => p.Coordinate.PrimaryValue.ToString("C0"),
            },
        ];
    }
}
