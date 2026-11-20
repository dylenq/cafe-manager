using System.Collections.Concurrent;
using Cafe.Server.Data;
using Cafe.Server.Sessions;
using Cafe.Shared;
using Cafe.Shared.Agents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cafe.Server.Agents;

/// <summary>
/// Maps session state to commands for the PC agents. Every outbound send is time-boxed and fault-isolated. Work for one
/// machine is serialized (one command flow at a time per machine id) and every state-changing message carries a
/// monotonic sequence number, so a late Lock can never land after a newer Unlock (or the reverse) and the agent can
/// drop anything older than what it has already applied. Pause, Stop, Start and Resume are state driven: the command
/// is computed from the database at send time, not from the signal kind alone.
/// </summary>
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

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _machineLocks = new(StringComparer.OrdinalIgnoreCase);

    // Seeded from the clock (milliseconds * 1000) so it keeps growing across a server restart.
    private long _sequence = time.GetUtcNow().ToUnixTimeMilliseconds() * 1000;

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// Sends the state the machine should be in right now: Unlock (plus a warning when already inside a warning
    /// window) with an Active session, GraceStarted with the remaining grace in Grace, otherwise Lock.
    /// </summary>
    public async Task PushStateAsync(string machineId, CancellationToken cancellationToken = default)
    {
        try
        {
            await WithMachineLockAsync(machineId, () => PushStateCoreAsync(machineId, null, includeWarning: true, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not push state to agent.");
        }
    }

    /// <summary>
    /// Call while holding the machine lock. Returns true when every command reached the agent. The warning for a session
    /// already inside a warning window is sent only when <paramref name="includeWarning"/> (a reconnect), never when a
    /// session has just started, resumed or moved.
    /// </summary>
    private async Task<bool> PushStateCoreAsync(string machineId, string? lockReason, bool includeWarning, CancellationToken cancellationToken)
    {
        if (!registry.IsConnected(machineId))
        {
            return false;
        }

        var cafeName = await settings.GetCafeNameAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(
            s => EF.Functions.Collate(s.AgentMachineId!, "NOCASE") == machineId, cancellationToken); // same collation as the unique index
        if (station is null)
        {
            return await SendAsync(machineId, (c, seq) => c.Lock(new LockInfo(machineId, cafeName, UnknownMachineReason, seq)));
        }

        var session = await db.Sessions.AsNoTracking().Include(s => s.Segments)
            .FirstOrDefaultAsync(s => s.StationId == station.Id && s.Status != SessionStatus.Ended, cancellationToken);
        var sessionSettings = await sessionSettingsService.GetAsync(cancellationToken);
        switch (session?.Status)
        {
            case SessionStatus.Active:
            {
                var info = BuildSessionInfo(session, station.Name);
                var delivered = await SendAsync(machineId, (c, seq) => c.Unlock(info with { Sequence = seq }));
                if (includeWarning && info.RemainingSeconds is { } remaining
                    && sessionSettings.WarningMinutes.Where(m => remaining <= m * 60L).DefaultIfEmpty(0).Min() is var window and > 0)
                {
                    delivered &= await SendAsync(machineId, (c, _) => c.ShowWarning(window));
                }

                return delivered;
            }

            case SessionStatus.Grace:
            {
                var left = RemainingGraceSeconds(session, sessionSettings);
                if (left > 0)
                {
                    return await SendAsync(machineId, (c, _) => c.GraceStarted(left));
                }

                break;
            }
        }

        var reason = session is { Status: SessionStatus.Paused } ? PausedReason : lockReason ?? DefaultLockReason;
        return await SendAsync(machineId, (c, seq) => c.Lock(new LockInfo(station.Name, cafeName, reason, seq)));
    }

    /// <summary>
    /// Forces a Lock on the PC of a station with the given reason. Returns true when it is not a PC agent station (nothing
    /// to do) or the command reached the agent; false when the agent is offline or the send failed.
    /// </summary>
    public async Task<bool> LockStationAsync(Station station, string reason, CancellationToken cancellationToken = default)
    {
        if (!IsAgentStation(station))
        {
            return true;
        }

        return await WithMachineLockAsync(
            station.AgentMachineId!, () => SendLockAsync(station, reason, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The expiry lock: like <see cref="LockStationAsync"/>, but a no-op (true) when a newer session already runs on the
    /// station, so a retried expiry action can never lock a PC that was started again in the meantime.
    /// </summary>
    public async Task<bool> LockExpiredStationAsync(Station station, string reason, CancellationToken cancellationToken = default)
    {
        if (!IsAgentStation(station))
        {
            return true;
        }

        return await WithMachineLockAsync(station.AgentMachineId!, async () =>
        {
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                if (await db.Sessions.AsNoTracking().AnyAsync(s => s.StationId == station.Id && s.Status != SessionStatus.Ended, cancellationToken))
                {
                    return true;
                }
            }

            return await SendLockAsync(station, reason, cancellationToken);
        }, cancellationToken);
    }

    private static bool IsAgentStation(Station station) =>
        station.Kind == StationKind.PC && !string.IsNullOrWhiteSpace(station.AgentMachineId);

    private async Task<bool> SendLockAsync(Station station, string reason, CancellationToken ct)
    {
        var cafeName = await settings.GetCafeNameAsync(ct);
        return await SendAsync(station.AgentMachineId!, (c, seq) => c.Lock(new LockInfo(station.Name, cafeName, reason, seq)));
    }

    public async Task OnSessionSignalAsync(SessionSignal signal, CancellationToken cancellationToken)
    {
        try
        {
            switch (signal.Kind)
            {
                case SessionSignalKind.Started:
                case SessionSignalKind.Resumed:
                case SessionSignalKind.Paused:
                    await SyncStationAsync(signal.StationId, null, cancellationToken);
                    break;

                case SessionSignalKind.Stopped:
                    await SyncStationAsync(signal.StationId, EndedReason, cancellationToken);
                    break;

                case SessionSignalKind.Extended:
                    await WithSessionAsync(signal.StationId, signal.SessionId, cancellationToken, async (machineId, station, session) =>
                    {
                        if (session.Status is not (SessionStatus.Active or SessionStatus.Grace))
                        {
                            return; // Paused stays locked.
                        }

                        var info = BuildSessionInfo(session, station.Name);
                        if (signal.ExtendedFromGrace)
                        {
                            await SendAsync(machineId, (c, seq) => c.Unlock(info with { Sequence = seq }));
                        }

                        await SendAsync(machineId, (c, seq) => c.SessionUpdated(info with { Sequence = seq }));
                    });
                    break;

                case SessionSignalKind.Moved:
                    await OnMovedAsync(signal, cancellationToken);
                    break;

                case SessionSignalKind.WarningReached:
                    await WithSessionAsync(signal.StationId, signal.SessionId, cancellationToken, async (machineId, _, session) =>
                    {
                        if (session.Status == SessionStatus.Active && signal.Minutes is { } minutes)
                        {
                            await SendAsync(machineId, (c, _) => c.ShowWarning(minutes));
                        }
                    });
                    break;

                case SessionSignalKind.GraceStarted:
                    if (signal.GraceSeconds is { } seconds and > 0)
                    {
                        var machineId = await MachineIdForAsync(signal.StationId, cancellationToken);
                        if (machineId is not null)
                        {
                            await WithMachineLockAsync(machineId, () => SendAsync(machineId, (c, _) => c.GraceStarted(seconds)), cancellationToken);
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
            await SyncStationAsync(from, $"Session moved to {targetName}", ct);
        }

        await SyncStationAsync(signal.StationId, null, ct);
    }

    /// <summary>
    /// Brings the PC of a station to the state the database says it should be in right now (state driven, so signals that
    /// race each other converge on the truth). <paramref name="lockReason"/> is used when the result is a plain Lock.
    /// </summary>
    private async Task SyncStationAsync(int stationId, string? lockReason, CancellationToken ct)
    {
        var machineId = await MachineIdForAsync(stationId, ct);
        if (machineId is not null)
        {
            await WithMachineLockAsync(machineId, () => PushStateCoreAsync(machineId, lockReason, includeWarning: false, ct), ct);
        }
    }

    /// <summary>The machine id of a PC station that has an agent assigned, or null.</summary>
    private async Task<string?> MachineIdForAsync(int stationId, CancellationToken ct)
    {
        var station = await LoadAgentStationAsync(stationId, ct);
        return station?.AgentMachineId;
    }

    private async Task<Station?> LoadAgentStationAsync(int stationId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId && s.Kind == StationKind.PC, ct);
        return station is not null && !string.IsNullOrWhiteSpace(station.AgentMachineId) ? station : null;
    }

    /// <summary>Runs the action while holding the machine lock, with the session re-read inside the lock.</summary>
    private async Task WithSessionAsync(int stationId, int sessionId, CancellationToken ct, Func<string, Station, Session, Task> action)
    {
        var station = await LoadAgentStationAsync(stationId, ct);
        if (station is null)
        {
            return;
        }

        var machineId = station.AgentMachineId!;
        await WithMachineLockAsync(machineId, async () =>
        {
            if (!registry.IsConnected(machineId))
            {
                return true;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var session = await db.Sessions.AsNoTracking().Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
            if (session is not null)
            {
                await action(machineId, station, session);
            }

            return true;
        }, ct);
    }

    private async Task<T> WithMachineLockAsync<T>(string machineId, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = _machineLocks.GetOrAdd(machineId.Trim(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
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
        return session.Mode.IsTimed() && session.PurchasedMinutes is { } purchased
            ? new SessionInfo(AgentSessionMode.Prepaid, Math.Max(SessionClock.RemainingSeconds(purchased, active), 0), active, stationName, now)
            : new SessionInfo(AgentSessionMode.OpenEnded, null, active, stationName, now);
    }

    /// <summary>Sends to the machine's live connection. False when it is not connected or the send failed or timed out.</summary>
    private async Task<bool> SendAsync(string machineId, Func<IAgentClient, long, Task> send)
    {
        if (!registry.TryGetConnectionId(machineId, out var connectionId))
        {
            return false;
        }

        try
        {
            await send(hub.Clients.Client(connectionId), NextSequence()).WaitAsync(SendTimeout);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sending a command to an agent failed.");
            return false;
        }
    }
}
