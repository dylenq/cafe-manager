using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Devices;

/// <summary>
/// Turns session signals for Console stations into TV power actions. It only reads the database and enqueues; the TV
/// is never awaited here, so a slow or dead TV cannot delay or fail a session operation or the PC agent sink.
/// Start and resume power on, stop powers off (unless Tv:PowerOffOnStop is false), a move powers the old TV off and
/// the new one on (only while the session is active); pause and the rest do nothing.
/// </summary>
public sealed class TvSessionEventSink(
    TvControlService control,
    TvActionQueue queue,
    TvSettingsService settings,
    IDbContextFactory<CafeDbContext> dbFactory,
    ILogger<TvSessionEventSink> logger) : ISessionEventSink
{
    public async Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            switch (signal.Kind)
            {
                case SessionSignalKind.Started:
                case SessionSignalKind.Resumed:
                    await EnqueueAsync(signal.StationId, on: true, "Session start", cancellationToken);
                    break;

                case SessionSignalKind.Stopped:
                    if (await settings.GetPowerOffOnStopAsync(cancellationToken))
                    {
                        await EnqueueAsync(signal.StationId, on: false, "Session stop", cancellationToken);
                    }

                    break;

                case SessionSignalKind.Moved:
                    if (signal.FromStationId is { } from)
                    {
                        await EnqueueAsync(from, on: false, "Session move", cancellationToken);
                    }

                    SessionStatus? status;
                    await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
                    {
                        status = await db.Sessions.AsNoTracking()
                            .Where(s => s.Id == signal.SessionId)
                            .Select(s => (SessionStatus?)s.Status)
                            .FirstOrDefaultAsync(cancellationToken);
                    }

                    if (status == SessionStatus.Active)
                    {
                        await EnqueueAsync(signal.StationId, on: true, "Session move", cancellationToken);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("TV sink could not handle {Kind} for station {StationId}: {Error}", signal.Kind, signal.StationId, ex.Message);
        }
    }

    private async Task EnqueueAsync(int stationId, bool on, string source, CancellationToken cancellationToken)
    {
        Station? station;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken);
        }

        if (station is null || station.Kind != StationKind.Console || string.IsNullOrWhiteSpace(station.TvIpAddress))
        {
            return;
        }

        queue.Enqueue(station.Id, ct => control.ExecuteAsync(station, on, source, ct));
    }
}
