using Microsoft.Extensions.Options;

namespace ReleaseCalendar.Web.Services;

/// <summary>
/// Keeps the cache current in the background. Nothing in the UI ever waits on Steam —
/// which is also why an undated game simply appears once Valve fills in its date.
/// </summary>
public sealed class SyncWorker(
    SyncService sync,
    IOptions<CalendarOptions> options,
    ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        if (opts.SteamId == 0)
        {
            log.LogWarning("Calendar:SteamId is not set — background sync is idle. Set it on the Settings page.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Clamp(opts.SyncIntervalHours, 1, 24 * 7));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, opts.StartupSyncDelaySeconds)), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sync.SyncAsync(force: false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad run kill the worker — the next tick may well succeed.
                log.LogError(ex, "Background sync threw; retrying at the next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
