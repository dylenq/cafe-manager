using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

/// <summary>Maps session state to commands for the PC agents. Every outbound send is time-boxed and fault-isolated.</summary>
public sealed class AgentCommandService(
    IHubContext<AgentHub, IAgentClient> hub,
    AgentRegistry registry,
    IDbContextFactory<CafeDbContext> dbFactory,
    TimeProvider time,
    AgentSettingsService settings,
    SessionSettingsService sessionSettingsService,
    ILogger<AgentCommandService> logger) : ISessionEventSink
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public const string DefaultLockReason = "Please see the front desk to start a session";
    public const string UnknownMachineReason = "This PC is not set up yet. Please see the front desk.";

    public const string PausedReason = "Session paused";
    public const string EndedReason = "Session ended. Please see the front desk.";

    /// <summary>
    /// Sends the state the machine should be in right now: Unlock (plus a warning when already inside a warning
    /// window) with an Active session, GraceStarted with the remaining grace in Grace, otherwise Lock.
    /// </summary>
    public async Task PushStateAsync(string machineId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!registry.TryGetConnectionId(machineId, out var connectionId))
            {
                return;
            }

            var cafeName = await settings.GetCafeNameAsync(cancellationToken);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.AgentMachineId == machineId, cancellationToken);
            if (station is null)
            {
                await SendAsync(connectionId, c => c.Lock(new LockInfo(machineId, cafeName, UnknownMachineReason)));
                return;
            }

            var session = await db.Sessions.AsNoTracking().Include(s => s.Segments)
                .FirstOrDefaultAsync(s => s.StationId == station.Id && s.Status != SessionStatus.Ended, cancellationToken);
            var sessionSettings = await sessionSettingsService.GetAsync(cancellationToken);
            switch (session?.Status)
            {
                case SessionStatus.Active:
                {
                    var info = BuildSessionInfo(session, station.Name);
                    await SendAsync(connectionId, c => c.Unlock(info));
                    if (info.RemainingSeconds is { } remaining
                        && sessionSettings.WarningMinutes.Where(m => remaining <= m * 60L).DefaultIfEmpty(0).Min() is var window and > 0)
                    {
                        await SendAsync(connectionId, c => c.ShowWarning(window));
                    }

                    return;
                }

                case SessionStatus.Grace:
                {
                    var left = RemainingGraceSeconds(session, sessionSettings);
                    if (left > 0)
                    {
                        await SendAsync(connectionId, c => c.GraceStarted(left));
                        return;
                    }

                    break;
                }
            }

            var reason = session is { Status: SessionStatus.Paused } ? PausedReason : DefaultLockReason;
            await SendAsync(connectionId, c => c.Lock(new LockInfo(station.Name, cafeName, reason)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not push state to agent.");
        }
    }

    /// <summary>Locks the PC of a station with the given reason. A quiet no-op when it is not a connected PC agent.</summary>
    public async Task LockStationAsync(Station station, string reason, CancellationToken cancellationToken = default)
    {
        if (station.Kind != StationKind.PC || string.IsNullOrWhiteSpace(station.AgentMachineId)
            || !registry.TryGetConnectionId(station.AgentMachineId, out var connectionId))
        {
            return;
        }

        var cafeName = await settings.GetCafeNameAsync(cancellationToken);
        await SendAsync(connectionId, c => c.Lock(new LockInfo(station.Name, cafeName, reason)));
    }

    public async Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            switch (signal.Kind)
            {
                case SessionSignalKind.Started:
                case SessionSignalKind.Resumed:
                    await WithSessionAsync(signal.StationId, signal.SessionId, cancellationToken, async (target, session) =>
                    {
                        if (session.Status == SessionStatus.Active)
                        {
                            var info = BuildSessionInfo(session, target.Station.Name);
                            await SendAsync(target.ConnectionId, c => c.Unlock(info));
                        }
                    });
                    break;

                case SessionSignalKind.Paused:
                    await LockByIdAsync(signal.StationId, PausedReason, cancellationToken);
                    break;

                case SessionSignalKind.Stopped:
                    await LockByIdAsync(signal.StationId, EndedReason, cancellationToken);
                    break;

                case SessionSignalKind.Extended:
                    await WithSessionAsync(signal.StationId, signal.SessionId, cancellationToken, async (target, session) =>
                    {
                        if (session.Status is not (SessionStatus.Active or SessionStatus.Grace))
                        {
                            return; // Paused stays locked.
                        }

                        var info = BuildSessionInfo(session, target.Station.Name);
                        if (signal.ExtendedFromGrace)
                        {
                            await SendAsync(target.ConnectionId, c => c.Unlock(info));
                        }

                        await SendAsync(target.ConnectionId, c => c.SessionUpdated(info));
                    });
                    break;

                case SessionSignalKind.Moved:
                    await OnMovedAsync(signal, cancellationToken);
                    break;

                case SessionSignalKind.WarningReached:
                    await WithSessionAsync(signal.StationId, signal.SessionId, cancellationToken, async (target, session) =>
                    {
                        if (session.Status == SessionStatus.Active && signal.Minutes is { } minutes)
                        {
                            await SendAsync(target.ConnectionId, c => c.ShowWarning(minutes));
                        }
                    });
                    break;

                case SessionSignalKind.GraceStarted:
                    if (signal.GraceSeconds is { } seconds and > 0)
                    {
                        var target = await ResolveAsync(signal.StationId, cancellationToken);
                        if (target is not null)
                        {
                            await SendAsync(target.ConnectionId, c => c.GraceStarted(seconds));
                        }
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not deliver {Kind} signal to agent.", signal.Kind);
        }
    }

    private async Task OnMovedAsync(SessionSignal signal, CancellationToken ct)
    {
        string targetName;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            targetName = await db.Stations.AsNoTracking().Where(s => s.Id == signal.StationId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "another station";
        }

        if (signal.FromStationId is { } from)
        {
            await LockByIdAsync(from, $"Session moved to {targetName}", ct);
        }

        await WithSessionAsync(signal.StationId, signal.SessionId, ct, async (target, session) =>
        {
            if (session.Status == SessionStatus.Active)
            {
                var info = BuildSessionInfo(session, target.Station.Name);
                await SendAsync(target.ConnectionId, c => c.Unlock(info));
            }
            else if (session.Status == SessionStatus.Paused)
            {
                var cafeName = await settings.GetCafeNameAsync(ct);
                await SendAsync(target.ConnectionId, c => c.Lock(new LockInfo(target.Station.Name, cafeName, PausedReason)));
            }
        });
    }

    private sealed record Target(Station Station, string ConnectionId);

    /// <summary>The PC station and live connection for a station id, or null when it is not a connected PC agent.</summary>
    private async Task<Target?> ResolveAsync(int stationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId && s.Kind == StationKind.PC, ct);
        if (station is null || string.IsNullOrWhiteSpace(station.AgentMachineId)
            || !registry.TryGetConnectionId(station.AgentMachineId, out var connectionId))
        {
            return null;
        }

        return new Target(station, connectionId);
    }

    private async Task LockByIdAsync(int stationId, string reason, CancellationToken ct)
    {
        var target = await ResolveAsync(stationId, ct);
        if (target is not null)
        {
            await LockStationAsync(target.Station, reason, ct);
        }
    }

    private async Task WithSessionAsync(int stationId, int sessionId, CancellationToken ct, Func<Target, Session, Task> action)
    {
        var target = await ResolveAsync(stationId, ct);
        if (target is null)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.AsNoTracking().Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is not null)
        {
            await action(target, session);
        }
    }

    private int RemainingGraceSeconds(Session session, SessionSettings sessionSettings)
    {
        if (session.GraceStartedAtUtc is not { } start)
        {
            return 0;
        }

        var left = (start.AddMinutes(sessionSettings.GracePeriodMinutes) - time.GetUtcNow()).TotalSeconds;
        return left <= 0 ? 0 : (int)Math.Ceiling(left);
    }

    private SessionInfo BuildSessionInfo(Session session, string stationName)
    {
        var now = time.GetUtcNow();
        var closed = session.Segments.Where(x => x.EndUtc is not null).Sum(x => (long)(x.EndUtc!.Value - x.StartUtc).TotalSeconds);
        var open = session.Segments.FirstOrDefault(x => x.EndUtc is null)?.StartUtc;
        var active = SessionClock.ActiveSeconds(closed, open, now);
        return session.Mode == SessionMode.Prepaid && session.PurchasedMinutes is { } purchased
            ? new SessionInfo(AgentSessionMode.Prepaid, Math.Max(SessionClock.RemainingSeconds(purchased, active), 0), active, stationName, now)
            : new SessionInfo(AgentSessionMode.OpenEnded, null, active, stationName, now);
    }

    private async Task SendAsync(string connectionId, Func<IAgentClient, Task> send)
    {
        try
        {
            await send(hub.Clients.Client(connectionId)).WaitAsync(SendTimeout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sending a command to an agent failed.");
        }
    }
}
