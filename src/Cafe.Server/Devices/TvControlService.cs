using Cafe.Server.Data;
using Cafe.Server.Agents;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Devices;

/// <summary>Outcome of a manual TV command, with a staff-readable message.</summary>
public sealed record TvCommandResult(bool Success, string Message);

/// <summary>
/// The single place for TV power actions (manual, session driven and expiry). Every attempt is audited in the
/// AgentCommandLog, updates the state cache and raises a desk notice on failure. Driver exceptions never escape (except
/// the caller's own cancellation) and the TV token is never logged.
/// </summary>
public sealed class TvControlService(
    ITvDriver driver,
    IDbContextFactory<CafeDbContext> dbFactory,
    TvStateCache cache,
    TimeProvider time,
    ILogger<TvControlService> logger)
{
    public const string NoTvMessage = "This station has no TV configured";

    public Task<TvCommandResult> PowerOnAsync(int stationId, string source = "Manual", CancellationToken cancellationToken = default) =>
        ManualAsync(stationId, on: true, source, cancellationToken);

    public Task<TvCommandResult> PowerOffAsync(int stationId, string source = "Manual", CancellationToken cancellationToken = default) =>
        ManualAsync(stationId, on: false, source, cancellationToken);

    /// <summary>Reads the TV state only (no power action) and returns it as text. Updates the cache.</summary>
    public async Task<TvCommandResult> TestAsync(int stationId, CancellationToken cancellationToken = default)
    {
        var station = await LoadAsync(stationId, cancellationToken);
        if (station is null || !HasTv(station))
        {
            return new TvCommandResult(false, NoTvMessage);
        }

        try
        {
            var state = await driver.GetStateAsync(station, cancellationToken);
            cache.Set(station.Id, state);
            return state == TvPowerState.Unreachable
                ? new TvCommandResult(false, $"The TV of {station.Name} did not respond")
                : new TvCommandResult(true, $"TV is {StateText(state)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Reading the TV state of station {StationId} failed: {Error}", station.Id, ex.Message);
            return new TvCommandResult(false, $"The TV of {station.Name} did not respond");
        }
    }

    internal async Task<TvActionResult> ExecuteAsync(Station station, bool on, string source, CancellationToken cancellationToken)
    {
        var kind = on ? AgentCommandKind.TvPowerOn : AgentCommandKind.TvPowerOff;
        if (!HasTv(station))
        {
            var none = new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, NoTvMessage);
            await WriteLogAsync(station, kind, $"{source}: Failed: {NoTvMessage}");
            return none;
        }

        TvActionResult result;
        try
        {
            // WaitAsync abandons a driver that ignores cancellation, so a hung TV cannot hold the caller forever.
            result = await (on
                ? driver.PowerOnAsync(station, cancellationToken)
                : driver.PowerOffAsync(station, cancellationToken)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteLogAsync(station, kind, $"{source}: Failed: timed out or cancelled");
            cache.RaiseNotice(station.Id, $"TV for {station.Name} did not respond");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("TV {Command} for station {Station} failed: {Error}", kind, station.Name, ex.Message);
            result = new TvActionResult(TvActionOutcome.Failed, TvPowerState.Unknown, ex.Message);
        }

        cache.Set(station.Id, result.State);
        await WriteLogAsync(station, kind, $"{source}: {Describe(result)}");

        if (result.Outcome == TvActionOutcome.Failed)
        {
            logger.LogWarning("TV {Command} for station {Station} failed: {Message}", kind, station.Name, result.Message);
            cache.RaiseNotice(station.Id, $"TV for {station.Name} did not respond");
        }
        else if (!on && result.Outcome == TvActionOutcome.Unreachable && source == "Expiry")
        {
            // The expiry handler treats this as success (nothing to power off), so tell staff to look at the TV.
            cache.RaiseNotice(station.Id, $"TV for {station.Name} unreachable at expiry \u2014 check it's off");
        }
        else if (on && result.Outcome == TvActionOutcome.WakeSent && result.State == TvPowerState.Unreachable)
        {
            cache.RaiseNotice(station.Id, $"TV for {station.Name} did not wake up");
        }

        return result;
    }

    private async Task<TvCommandResult> ManualAsync(int stationId, bool on, string source, CancellationToken cancellationToken)
    {
        var station = await LoadAsync(stationId, cancellationToken);
        if (station is null)
        {
            return new TvCommandResult(false, "That station no longer exists.");
        }

        if (!HasTv(station))
        {
            return new TvCommandResult(false, NoTvMessage);
        }

        var result = await ExecuteAsync(station, on, source, cancellationToken);
        var message = string.IsNullOrWhiteSpace(result.Message) ? Describe(result) : result.Message;
        return new TvCommandResult(result.IsSuccess, message);
    }

    private async Task<Station?> LoadAsync(int stationId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken);
    }

    private static bool HasTv(Station station) =>
        station.Kind == StationKind.Console && !string.IsNullOrWhiteSpace(station.TvIpAddress);

    private static string Describe(TvActionResult result) =>
        string.IsNullOrWhiteSpace(result.Message) ? result.Outcome.ToString() : $"{result.Outcome}: {result.Message}";

    private static string StateText(TvPowerState state) => state switch
    {
        TvPowerState.On => "on",
        TvPowerState.Standby => "in standby",
        TvPowerState.Unreachable => "unreachable",
        _ => "in an unknown state",
    };

    private async Task WriteLogAsync(Station station, AgentCommandKind kind, string details)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.AgentCommandLogs.Add(new AgentCommandLog
            {
                StationId = station.Id,
                StationName = AgentRemoteControlService.Truncate(station.Name, 100),
                Command = kind,
                AtUtc = time.GetUtcNow(),
                Details = AgentRemoteControlService.Truncate(details, 300),
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError("Could not write the TV command log for station {StationId}: {Error}", station.Id, ex.Message);
        }
    }
}
