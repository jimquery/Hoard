using Hoard.Core.Configuration;
using Hoard.Core.Data;
using Hoard.Core.Sync;

namespace Hoard.Service;

/// <summary>
/// Background refresh loop. Every <see cref="SyncSettings.IntervalMinutes"/> it syncs each enabled institution.
/// Failures are logged and retried on the next tick; a single bad institution never stops the others.
/// </summary>
public sealed class SyncWorker : BackgroundService
{
    private readonly HoardSettings _settings;
    private readonly IServiceProvider _services;
    private readonly ILogger<SyncWorker> _log;

    public SyncWorker(HoardSettings settings, IServiceProvider services, ILogger<SyncWorker> log)
    {
        _settings = settings;
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Database.IsConfigured)
        {
            _log.LogWarning("Hoard is not configured yet ({Path}). Run the Hoard desktop app once to complete setup. The service will idle.", SettingsStore.DefaultPath);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            return;
        }

        // Give MySQL a moment after boot before the first attempt.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(15, _settings.Sync.IntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await _services.GetRequiredService<SchemaMigrator>().MigrateAsync(stoppingToken);
                var results = await _services.GetRequiredService<SyncService>().SyncAllAsync(stoppingToken);
                _log.LogInformation("Scheduled sync complete: {Count} institution(s), {Added} new transaction(s), {Failed} failure(s).",
                    results.Count, results.Sum(r => r.TransactionsAdded), results.Count(r => r.Status == Core.Domain.SyncStatus.Failed));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Scheduled sync failed; will retry in {Interval}.", interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
