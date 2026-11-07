using System.Windows.Threading;
using Hoard.Core.Configuration;
using Hoard.Core.Reporting.Weekly;
using Hoard.Core.Scheduling;
using Hoard.Desktop.ViewModels;

namespace Hoard.Desktop.Services;

/// <summary>
/// Jobs that run while the desktop app is open: the daily sync of every institution and the weekly spending email.
/// It checks once a minute (first a minute after start, so start-up and the first page load finish first). A job
/// missed while the app was closed runs once at the next check; each scheduled occurrence runs at most once.
/// </summary>
public sealed class DesktopScheduler
{
    private readonly HoardSettings _settings;
    private readonly MainViewModel _main;
    private readonly WeeklyReportService _weekly;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _running;

    public DesktopScheduler(HoardSettings settings, MainViewModel main, WeeklyReportService weekly)
    {
        _settings = settings;
        _main = main;
        _weekly = weekly;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start() => _timer.Start();

    private async Task TickAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            await DailySyncAsync();
            await WeeklyReportAsync();
        }
        finally { _running = false; }
    }

    private async Task DailySyncAsync()
    {
        var sync = _settings.Sync;
        if (!sync.DailySyncEnabled) return;
        var latest = Schedules.LatestDaily(DateTime.Now, Schedules.ParseTime(sync.DailySyncTime, new TimeOnly(7, 0)));
        if (!Schedules.IsDue(latest, sync.LastDailySyncFor)) return;
        if (_main.IsBusy) return; // a manual sync or import is running; try again next minute

        // Recorded before running, so a failing sync is retried tomorrow rather than every minute.
        sync.LastDailySyncFor = latest;
        SettingsStore.Save(_settings);
        try
        {
            _main.StatusMessage = "Automatic daily sync…";
            await _main.SyncAllCommand.ExecuteAsync(null);
            _main.StatusMessage = "Automatic daily sync: " + _main.StatusMessage;
        }
        catch (Exception ex)
        {
            _main.StatusMessage = "Automatic daily sync failed: " + ex.Message;
            ErrorLog.Write(ex, "Daily sync");
        }
        _main.Settings.RefreshSchedules();
    }

    private async Task WeeklyReportAsync()
    {
        try
        {
            if (await _weekly.RunScheduledAsync(DateTime.Now) is { } sent)
            {
                _main.StatusMessage = $"Weekly spending report for {sent.Report.Start:MMM d}–{sent.Report.End:MMM d} emailed to {string.Join(", ", sent.Recipients)}.";
                _main.Reports.Weekly.OnScheduledSend(sent);
            }
        }
        catch (Exception ex)
        {
            _main.StatusMessage = "Weekly report not sent (will retry in an hour): " + ex.Message;
            _main.Reports.Weekly.RefreshSummary();
            ErrorLog.Write(ex, "Weekly report");
        }
    }
}
