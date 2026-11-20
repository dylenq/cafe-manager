using Cafe.Server.Data;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cafe.Server.Devices;

/// <summary>
/// Polls every enabled console TV every Tv:PollSeconds and keeps <see cref="TvStateCache"/> fresh. Passes never overlap,
/// the TVs of one pass are polled in parallel, and a TV that hangs or throws reads as Unreachable without holding up the
/// others. Poll results are not stored in the database and are logged only at Debug level.
/// </summary>
public sealed class TvMonitorService(
    IDbContextFactory<CafeDbContext> dbFactory,
    ITvDriver driver,
    TvStateCache cache,
    IOptions<TvOptions> options,
    ILogger<TvMonitorService> logger) : BackgroundService
{
    public const string RunMonitorKey = "Tv:RunMonitor";

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue(RunMonitorKey, true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("TV monitor pass failed: {Error}", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass over every eligible station. Public so tests can drive it without the timer.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        List<Station> stations;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            stations = await db.Stations.AsNoTracking()
                .Where(s => s.IsEnabled && s.Kind == StationKind.Console && s.TvIpAddress != null && s.TvIpAddress != "")
                .ToListAsync(cancellationToken);
        }

        stations = stations.Where(s => !string.IsNullOrWhiteSpace(s.TvIpAddress)).ToList();
        var eligible = stations.Select(s => s.Id).ToHashSet();
        foreach (var id in cache.KnownStationIds.Where(id => !eligible.Contains(id)))
        {
            cache.Remove(id);
        }

        await Task.WhenAll(stations.Select(s => PollOneAsync(s, cancellationToken)));
    }

    private async Task PollOneAsync(Station station, CancellationToken cancellationToken)
    {
        var state = TvPowerState.Unreachable;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.RestTimeoutSeconds) + 1));
            state = await driver.GetStateAsync(station, cts.Token).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug("TV poll for station {StationId} failed: {Error}", station.Id, ex.Message);
        }

        cache.Set(station.Id, state);
        logger.LogDebug("TV of station {StationId} is {State}.", station.Id, state);
    }
}
